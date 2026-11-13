using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Services.Extensions;
using Missum.Core.Extensions;
using Missum.Infrastructure.Extensions;

namespace Missum.Tests;

public sealed class BlenderDesktopLiveTests
{
    private static readonly int[] GroundFloorCenter = [0, 0, 1];
    private static readonly int[] UpperFloorCenter = [0, 0, 4];
    private static readonly int[] FloorDimensions = [2, 2, 2];

    [Fact]
    public async Task InstalledBlenderStartsWithoutAddonAndCapturesCorrectedScene()
    {
        // The opt-in fixture directory has the signed package staged under Data/Extensions.
        var fixture = Environment.GetEnvironmentVariable("MISSUM_BLENDER_LIVE_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var workspace = Path.Combine(fixture, "workspace");
        var extensions = Path.Combine(fixture, "Data", "Extensions");
        var inspector = new AextPackageInspector(new DirectoryAextSignatureVerifier(Path.Combine(extensions, "trusted-keys")));
        using var packages = new FileSystemAextPackageStore(inspector, new(extensions, RequireVerifiedSignatures: true));
        var runtime = new ExtensionRuntimeService(packages, ExtensionActionCatalog.CreateWithBuiltIns(), new ExtensionHostSupervisor());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        const string action = "com.missum.blender/edit";
        Task<JsonElement> Invoke(string tool, object input) =>
            runtime.InvokeAsync(action, JsonSerializer.SerializeToElement(input), workspace, "blender." + tool, token);

        await runtime.StartAsync(token);
        Assert.Null(runtime.GetDisabledReason(action, workspace));
        var observation = await Invoke("status", new { connect = false });
        Assert.False(observation.GetProperty("connected").GetBoolean());
        var status = await Invoke("status", new { connect = true });
        var selected = status.GetProperty("selected");
        var processId = selected.GetProperty("processId").GetInt32();
        using var blender = Process.GetProcessById(processId);
        try
        {
            Assert.True(selected.GetProperty("controllable").GetBoolean());
            var project = Path.Combine(workspace, "Szene.blend");
            Assert.Equal(project, selected.GetProperty("projectPath").GetString(), ignoreCase: true);
            Assert.True(new FileInfo(project).Length > 0);
            var first = await Invoke("scene.edit", new { operations = new[] {
                new { op = "object.create", name = "Erdgeschoss", primitive = "cube", location = GroundFloorCenter, dimensions = FloorDimensions },
                new { op = "object.create", name = "Obergeschoss", primitive = "cube", location = UpperFloorCenter, dimensions = FloorDimensions },
            }, save = true });
            var firstId = first.GetProperty("operationId").GetString();
            var firstImage = await Invoke("view.capture", new { view = "window", operationId = firstId });
            Assert.Equal("blender.window", firstImage.GetProperty("captureMethod").GetString());
            var materials = await Invoke("scene.edit", new { operations = new[] {
                new { op = "material.create", target = "Obergeschoss", name = "Putz", material = "" },
                new { op = "material.assign", target = "Erdgeschoss", name = "", material = "Stein" },
            }, save = true });
            Assert.Equal(2, materials.GetProperty("appliedCount").GetInt32());

            var floorsQuery = new { include = new[] { "bounds" }, objectNames = new[] { "Erdgeschoss", "Obergeschoss" } };
            async Task<Dictionary<string, JsonElement>> ReadFloors()
            {
                var read = await Invoke("scene.read", floorsQuery);
                return read.GetProperty("objects").EnumerateArray().ToDictionary(obj => obj.GetProperty("name").GetString()!);
            }
            var floors = await ReadFloors();
            Assert.Equal("Stein", floors["Erdgeschoss"].GetProperty("materials")[0].GetString());
            Assert.Equal("Putz", floors["Obergeschoss"].GetProperty("materials")[0].GetString());
            Assert.Equal(2, floors["Erdgeschoss"].GetProperty("worldBounds").GetProperty("max")[2].GetDouble());
            Assert.Equal(3, floors["Obergeschoss"].GetProperty("worldBounds").GetProperty("min")[2].GetDouble());

            // Reproduce the exact reply shape that previously took Blender's timer offline.
            var second = await Invoke("custom.run", new { code = """
                bpy.data.objects['Obergeschoss'].location.z = 3
                result = {}
                result['summary'] = result
                """, save = true });
            Assert.True(second.TryGetProperty("pythonResultWarning", out _));
            Assert.True(second.GetProperty("saved").GetProperty("saved").GetBoolean());
            floors = await ReadFloors();
            Assert.Equal(2, floors["Obergeschoss"].GetProperty("worldBounds").GetProperty("min")[2].GetDouble());
            var secondId = second.GetProperty("operationId").GetString();
            Assert.NotEqual(firstId, secondId);
            var secondImage = await Invoke("view.capture", new { view = "window", operationId = secondId });
            Assert.Equal("blender.window", secondImage.GetProperty("captureMethod").GetString());
            var screenshot = secondImage.GetProperty("imagePath").GetString()!;
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(screenshot, token)));
            Assert.Equal(hash, secondImage.GetProperty("sha256").GetString(), ignoreCase: true);
            Assert.NotEqual(firstImage.GetProperty("sha256").GetString(), secondImage.GetProperty("sha256").GetString());
            var configured = await Invoke("custom.run", new { code = "result = {'engine': set_render_engine('eevee')}" });
            var engine = configured.GetProperty("pythonResult").GetProperty("engine").GetString();
            Assert.True(engine is "BLENDER_EEVEE" or "BLENDER_EEVEE_NEXT");
            var scene = await Invoke("scene.read", new { detail = "summary" });
            Assert.Equal(engine, scene.GetProperty("renderEngine").GetString());
            var busySignal = Path.Combine(workspace, "bridge-busy.signal");
            var busyCommand = Invoke("custom.run", new { code = """
                from pathlib import Path
                import time
                Path(bpy.path.abspath('//bridge-busy.signal')).touch()
                time.sleep(8)
                """ });
            while (!File.Exists(busySignal) && !busyCommand.IsCompleted)
                await Task.Delay(50, token);
            var busyStatus = await Invoke("status", new { connect = false });
            Assert.True(busyStatus.GetProperty("bridgeUnavailable").GetBoolean());
            Assert.False(busyStatus.GetProperty("rebindRequired").GetBoolean());
            await busyCommand;
            var reconnected = await Invoke("status", new { connect = true, projectPath = "Szene.blend" });
            Assert.Equal(processId, reconnected.GetProperty("selected").GetProperty("processId").GetInt32());
            Assert.Single(await packages.GetActivePackagesAsync(cancellationToken: token));
            var restartedCatalog = ExtensionActionCatalog.CreateWithBuiltIns();
            var restartedRuntime = new ExtensionRuntimeService(packages, restartedCatalog, new ExtensionHostSupervisor());
            await restartedRuntime.StartAsync(token);
            Assert.Contains(restartedCatalog.GetActions(ExtensionChatMode.Coding), item => item.ActionId == action);
            Assert.Single(Directory.GetFiles(workspace, "*.blend*"));
            await File.WriteAllTextAsync(Path.Combine(fixture, "capture-path.txt"), screenshot, token);
        }
        finally
        {
            if (!blender.HasExited)
            {
                _ = blender.CloseMainWindow();
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await blender.WaitForExitAsync(closeTimeout.Token); }
                catch (OperationCanceledException) { if (!blender.HasExited) blender.Kill(entireProcessTree: true); }
            }
        }
    }
}
