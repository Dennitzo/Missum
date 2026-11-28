using Missum.App.Services;
using Missum.App.Services.AiStack;

namespace Missum.Tests;

/// <summary>
/// The in-app setup of the local AI stack (installer path): compose variables, service selection,
/// pinned worker models and the location of the stack sources.
/// </summary>
public sealed class AiStackSetupServiceTests
{
    private static readonly Dictionary<string, string> NoValues = new(StringComparer.Ordinal);

    [Fact]
    public void ANewInstallationIsLocalOnlyAndUsesTheUsersOwnFolders()
    {
        var values = AiStackSetupService.BuildEnvironment(NoValues, @"C:\ProgramData\Missum-AI-Stack",
            @"C:\ProgramData\Missum-AI-Stack\Models", @"C:\Users\Nutzer", "2.1.0", gpuCount: 2);

        Assert.Equal("127.0.0.1", values["MISSUM_AI_EXPECTED_LAN_IP"]);
        Assert.Equal("http://127.0.0.1:8080", values["MISSUM_AI_PUBLIC_URL"]);
        Assert.Equal("127.0.0.1", values["MISSUM_AI_BIND_ADDRESS"]);
        Assert.Equal("C:/Users/Nutzer/.cache/huggingface/hub", values["MISSUM_AI_CODING_MODEL_ROOT"]);
        Assert.Equal(values["MISSUM_AI_CODING_MODEL_ROOT"], values["MISSUM_AI_NATIVE_MODEL_ROOT"]);
        Assert.Equal("C:/ProgramData/Missum-AI-Stack", values["MISSUM_AI_DATA_ROOT"]);
        Assert.Equal("2.1.0", values["MISSUM_AI_IMAGE_VERSION"]);
        Assert.Equal("http://host.docker.internal:8081", values["MISSUM_AI_MODEL_RUNTIME_URL"]);
        // Two GPUs keep the compose defaults (speech on 0+1, image on 1).
        Assert.False(values.ContainsKey("MISSUM_AI_IMAGE_CUDA_DEVICES"));
        Assert.DoesNotContain(values.Values, static value => value.Contains("AMD", StringComparison.Ordinal) || value.Contains("192.168.", StringComparison.Ordinal));
    }

    [Fact]
    public void ASingleGpuPcRunsSpeechAndImageOnGpuZero()
    {
        var values = AiStackSetupService.BuildEnvironment(NoValues, @"C:\Data", @"C:\Data\Models", @"C:\Users\Nutzer", "2.1.0", gpuCount: 1);
        Assert.Equal("0", values["MISSUM_AI_SPEECH_CUDA_DEVICES"]);
        Assert.Equal("0", values["MISSUM_AI_SUPERTONIC_CUDA_DEVICE"]);
        Assert.Equal("0", values["MISSUM_AI_IMAGE_CUDA_DEVICES"]);
    }

    [Fact]
    public void AnExistingLanDeploymentKeepsItsAddressAndOwnSettings()
    {
        var existing = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MISSUM_AI_EXPECTED_LAN_IP"] = "192.168.0.67",
            ["MISSUM_AI_MODEL_RUNTIME_URL"] = "http://host.docker.internal:9081",
            ["MISSUM_AI_NATIVE_BINARY_PATH"] = "D:/llama/llama-server.exe",
        };
        var values = AiStackSetupService.BuildEnvironment(existing, @"C:\Data", @"C:\Data\Models", @"C:\Users\Nutzer", "2.1.0", gpuCount: 2);
        Assert.Equal("http://192.168.0.67:8080", values["MISSUM_AI_PUBLIC_URL"]);
        Assert.Equal("0.0.0.0", values["MISSUM_AI_BIND_ADDRESS"]);
        Assert.Equal("http://host.docker.internal:9081", values["MISSUM_AI_MODEL_RUNTIME_URL"]);
        Assert.Equal("D:/llama/llama-server.exe", values["MISSUM_AI_NATIVE_BINARY_PATH"]);
    }

    [Fact]
    public void OptionalComponentsAddOnlyTheirServices()
    {
        Assert.Equal(["gateway", "caddy", "searxng", "media"], AiStackSetupService.ServicesFor(AiStackComponents.None));
        Assert.Equal(["gateway", "caddy", "searxng", "media", "speech", "image"],
            AiStackSetupService.ServicesFor(AiStackComponents.Speech | AiStackComponents.Image));
        Assert.True(AiStackSetupService.RequiredBytes(AiStackComponents.Speech | AiStackComponents.Image)
            > AiStackSetupService.RequiredBytes(AiStackComponents.Speech));
    }

    [Fact]
    public void EveryWorkerModelIsPinnedAndStaysInsideTheModelFolder()
    {
        var files = AiStackWorkerModels.Files;
        Assert.All(files, static file =>
        {
            Assert.Matches("^[0-9a-f]{40}$", file.Revision);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.True(file.Length > 0);
            Assert.StartsWith(file.Component == AiStackComponents.Speech ? "speech/" : "image/", file.Destination, StringComparison.Ordinal);
            var target = AiStackWorkerModels.TargetPath(@"C:\Models", file);
            Assert.StartsWith(@"C:\Models\", target, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("https", AiStackWorkerModels.ResolveUri(file).Scheme);
        });
        Assert.Equal(files.Count, files.Select(static file => file.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // The runtime resources the speech and image workers expect (see deploy-ai-stack.ps1).
        foreach (var required in new[] { "speech/faster-whisper-large-v3/model.bin", "speech/spkrec-ecapa-voxceleb/embedding_model.ckpt",
                     "speech/supertonic-3/onnx/vocoder.onnx", "image/z-image/z_image_turbo-Q4_K.gguf", "image/z-image/ae.safetensors",
                     "image/z-image/Qwen3-4B-Instruct-2507-Q4_K_M.gguf" })
            Assert.Contains(files, file => file.Destination == required);
        Assert.Empty(AiStackWorkerModels.For(AiStackComponents.None));
        Assert.All(AiStackWorkerModels.For(AiStackComponents.Image), static file => Assert.Equal(AiStackComponents.Image, file.Component));
    }

    [Fact]
    public void AModelPathOutsideTheModelFolderIsRejected()
    {
        var escape = new AiStackWorkerModelFile(AiStackComponents.Speech, "a/b", new string('0', 40), "x", "../../Windows/evil.dll", 1, new string('0', 64));
        Assert.Throws<InvalidOperationException>(() => AiStackWorkerModels.TargetPath(@"C:\Models", escape));
    }

    [Fact]
    public async Task TheStackRootComesFromTheExplicitVariableAndTheSetupWaitsForMissingRequirements()
    {
        var root = Directory.CreateTempSubdirectory("missum-aistack-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "deploy", "missum-ai"));
            await File.WriteAllTextAsync(Path.Combine(root, "deploy", "missum-ai", "compose.yaml"), "name: missum-ai-stack\n");
            var profile = AssistantRuntimeProfile.Resolve() with { StackDataRoot = Path.Combine(root, "data"), DataDirectory = Path.Combine(root, "app") };
            var calls = new List<string>();
            using var service = new AiStackSetupService(profile,
                variable => variable == AiStackSetupService.StackRootVariable ? root : null,
                (file, arguments, _, _) =>
                {
                    calls.Add(file + " " + string.Join(' ', arguments));
                    // No Docker on this machine: every tool call fails.
                    return Task.FromResult(new AiStackSetupService.ProcessResult(1, ""));
                },
                _ => 1L << 40);

            Assert.Equal(Path.GetFullPath(root), service.LocateStackRoot());
            var report = await service.CheckRequirementsAsync(AiStackComponents.None, CancellationToken.None);
            Assert.False(report.CanInstall);
            Assert.Contains(report.Items, static item => item.Name == "Docker Desktop" && !item.Met);
            Assert.False(await service.IsInstalledAsync(CancellationToken.None));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.InstallAsync(AiStackComponents.None, new Progress<AiStackSetupProgress>(), CancellationToken.None));
            Assert.Contains("Docker Desktop", error.Message, StringComparison.Ordinal);
            // Nothing was built or started.
            Assert.DoesNotContain(calls, static call => call.StartsWith("docker compose", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
