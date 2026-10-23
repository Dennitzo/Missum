using System.Diagnostics;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class GatewayRestartCompactionLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task InstalledGatewayUpdatesOnRestartAndFirstManualCompactionReachesItsEndpoint()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GATEWAY_UPGRADE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var profile = AssistantRuntimeProfile.Resolve();
        Assert.Equal("stable", profile.Name);
        var before = await InspectGatewayAsync(token);
        Assert.False(before.GetProperty("State").GetProperty("Running").GetBoolean(), "The live upgrade requires a stopped gateway.");
        var beforeProxy = await DockerAsync(["inspect", "missum-ai-caddy"], token);
        using (var proxy = JsonDocument.Parse(beforeProxy))
            Assert.False(proxy.RootElement[0].GetProperty("State").GetProperty("Running").GetBoolean());
        string? updatedId = null;
        SessionContextCompactionResponse? result = null;
        using (var lifecycle = new MissumAiStackLifecycleService(profile))
        {
            try
            {
                await lifecycle.EnsureGatewayStartedAsync(profile.GatewayUri, token);
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
                { BaseAddress = profile.GatewayUri, Timeout = Timeout.InfiniteTimeSpan };
                using var client = new MissumAiClient(http);
                while (true)
                {
                    try { await client.GetLiveHealthAsync(token); break; }
                    catch (HttpRequestException) { await Task.Delay(200, token); }
                }
                // An unused session exercises the exact new route without loading
                // a model, creating a run, or changing existing user context.
                result = await client.CompactSessionContextAsync(new(Guid.NewGuid().ToString("D")), token);
                Assert.False(result.Changed);
                Assert.Null(result.SourceRunId);
                var after = await InspectGatewayAsync(token);
                updatedId = after.GetProperty("Id").GetString();
                Assert.True(JsonElement.DeepEquals(before.GetProperty("Mounts"), after.GetProperty("Mounts")));
                Assert.True(JsonElement.DeepEquals(before.GetProperty("Config").GetProperty("Env"), after.GetProperty("Config").GetProperty("Env")));
                await AssertCurrentImageAsync(after, token);
                await lifecycle.EnsureGatewayStartedAsync(profile.GatewayUri, token);
                Assert.Equal(updatedId, (await InspectGatewayAsync(token)).GetProperty("Id").GetString());
            }
            finally { await lifecycle.StopAsync(profile.GatewayUri, CancellationToken.None); }
        }
        // A second cold start must retain the updated container even when OCI
        // index and platform image IDs use different digests.
        using (var restarted = new MissumAiStackLifecycleService(profile))
        {
            try
            {
                await restarted.EnsureGatewayStartedAsync(profile.GatewayUri, token);
                Assert.Equal(updatedId, (await InspectGatewayAsync(token)).GetProperty("Id").GetString());
            }
            finally { await restarted.StopAsync(profile.GatewayUri, CancellationToken.None); }
        }
        var evidence = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "gateway-restart-live.json"), JsonSerializer.Serialize(new
            {
                passed = true, beforeContainer = before.GetProperty("Id").GetString(), afterContainer = updatedId,
                routeStatus = 200, firstManualInteraction = true, noModelLoaded = true, unchangedDataMount = true,
                coldRestartRetainsCurrentImage = true, compactionResult = result,
            }), token);
        }
    }

    private static async Task AssertCurrentImageAsync(JsonElement container, CancellationToken token)
    {
        var manifest = container.GetProperty("ImageManifestDescriptor");
        var platform = manifest.GetProperty("platform");
        var name = platform.GetProperty("os").GetString() + "/" + platform.GetProperty("architecture").GetString();
        using var images = JsonDocument.Parse(await DockerAsync(["image", "inspect", "--platform", name,
            container.GetProperty("Config").GetProperty("Image").GetString()!], token));
        Assert.Equal(images.RootElement[0].GetProperty("Descriptor").GetProperty("digest").GetString(),
            manifest.GetProperty("digest").GetString());
    }

    private static async Task<JsonElement> InspectGatewayAsync(CancellationToken token)
    {
        using var document = JsonDocument.Parse(await DockerAsync(["inspect", "missum-ai-gateway"], token));
        return document.RootElement[0].Clone();
    }

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("docker")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker did not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
