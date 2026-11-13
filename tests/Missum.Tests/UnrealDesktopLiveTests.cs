using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.App.Services.Extensions;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Opt-in verification against an installed Unreal Engine and a disposable project.</summary>
public sealed class UnrealDesktopLiveTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task LauncherCreatesBlueprintProjectAndRespondsFromEditor()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_UNREAL_LIVE") != "1") return;

        var repository = FindRepositoryRoot();
        var engine = Environment.GetEnvironmentVariable("MISSUM_UNREAL_LIVE_ENGINE") ?? "5.8";
        var root = Path.Combine(Path.GetTempPath(), "missum-unreal-live", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "Project");
        var content = Path.Combine(root, "Extension");
        var source = Path.Combine(repository, "extensions", "unreal", "editor", "MissumBridge");
        var destination = Path.Combine(content, "runtime", "MissumBridge");
        Directory.CreateDirectory(workspace);
        CopyDirectory(source, destination);
        output.WriteLine($"Engine association: {engine}");
        output.WriteLine($"Disposable fixture: {root}");

        UnrealApplicationEndpoint? endpoint = null;
        var passed = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        try
        {
            output.WriteLine("Building the project plugin and starting the Unreal Editor.");
            endpoint = await UnrealApplicationLauncher.EnsureStartedAsync(content, workspace, null, engine, timeout.Token);
            var project = Assert.Single(Directory.GetFiles(workspace, "*.uproject"));
            Assert.True(File.Exists(project), $"The blank Blueprint project was not created in {workspace}.");
            Assert.Equal(engine, UnrealApplicationLauncher.ReadAssociation(project));
            Assert.True(File.Exists(Path.Combine(workspace, "Plugins", "MissumBridge", "MissumBridge.uplugin")));
            var engineSettings = await File.ReadAllTextAsync(Path.Combine(workspace, "Config", "DefaultEngine.ini"), timeout.Token);
            Assert.Contains("EditorStartupMap=/Game/Maps/Main", engineSettings, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GameDefaultMap=/Game/Maps/Main", engineSettings, StringComparison.OrdinalIgnoreCase);
            Assert.True(UnrealApplicationLauncher.TryGetEndpoint(workspace, out var reused));
            Assert.Equal(endpoint, reused);

            await AssertBlankWorldAsync(endpoint, project, timeout.Token);
            Assert.True(File.Exists(Path.Combine(workspace, "Content", "Maps", "Main.umap")),
                "The configured blank Main map was not saved to the project.");
            output.WriteLine($"Editor PID {endpoint.ProcessId} answered for {project}.");
            await StopOwnedEditorAsync(workspace, endpoint.ProcessId);
            output.WriteLine("Reopening the project to verify the saved startup map.");
            endpoint = null;
            endpoint = await UnrealApplicationLauncher.EnsureStartedAsync(content, workspace, project, engine, timeout.Token);
            await AssertBlankWorldAsync(endpoint, project, timeout.Token);
            output.WriteLine($"Reopened editor PID {endpoint.ProcessId} loaded the saved blank Main map.");
            passed = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            output.WriteLine($"Unreal live verification failed. The fixture is retained at {root}.");
            output.WriteLine(exception.ToString());
            throw;
        }
        finally
        {
            // The lease identifies only the process started for this unique workspace,
            // including when startup failed before its endpoint became ready.
            await StopOwnedEditorAsync(workspace, endpoint?.ProcessId);
            if (passed && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertBlankWorldAsync(UnrealApplicationEndpoint endpoint, string project,
        CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port, cancellationToken);
        await using var stream = tcp.GetStream();
        var requestId = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new
        {
            token = endpoint.Token,
            requestId,
            command = "status",
            arguments = new { },
        });
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"), cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(line), "Unreal closed the connection without returning status.");
        using var response = JsonDocument.Parse(line!);
        var value = response.RootElement;
        Assert.True(value.GetProperty("ok").GetBoolean(), $"Unreal rejected status: {value}");
        Assert.Equal(requestId, value.GetProperty("requestId").GetString());
        var status = value.GetProperty("result");
        Assert.Equal(endpoint.ProcessId, status.GetProperty("processId").GetInt32());
        Assert.Equal(Path.GetFullPath(project), Path.GetFullPath(status.GetProperty("projectPath").GetString()!),
            ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("instanceId").GetString()));
        Assert.Equal("/Game/Maps/Main.Main", status.GetProperty("world").GetString(), ignoreCase: true);
        Assert.Empty(status.GetProperty("cameras").EnumerateArray());
        Assert.False(status.GetProperty("actorsTruncated").GetBoolean(), "The world actor list was truncated.");
        var actors = status.GetProperty("worldActors").EnumerateArray().ToArray();
        Assert.Equal(status.GetProperty("actorCount").GetInt32(), actors.Length);
        Assert.All(actors, actor =>
        {
            var actorClass = actor.GetProperty("classPath").GetString();
            var actorName = actor.GetProperty("name").GetString();
            var engineActor = (actorName, actorClass) switch
            {
                ("WorldSettings", "/Script/Engine.WorldSettings") => true,
                ("Brush_0", "/Script/Engine.Brush") => true,
                ("DefaultPhysicsVolume_0", "/Script/Engine.DefaultPhysicsVolume") => true,
                ("BuoyancyManager_0", "/Script/Water.BuoyancyManager") => true,
                ("GameplayDebuggerPlayerManager_0", "/Script/GameplayDebugger.GameplayDebuggerPlayerManager") => true,
                ("ChaosDebugDrawActor", "/Script/Engine.Actor") => true,
                ("AbstractNavData-Default", "/Script/NavigationSystem.AbstractNavData") => true,
                _ => false,
            };
            Assert.True(engineActor,
                $"The blank map contains an unexpected actor: {actor}");
        });
    }

    private static async Task StopOwnedEditorAsync(string workspace, int? expectedProcessId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)).ToUpperInvariant())));
        var leasePath = Path.Combine(Path.GetTempPath(), "missum-program-integrations", "unreal-launches", key, "lease.json");
        if (!File.Exists(leasePath)) return;
        using var lease = JsonDocument.Parse(await File.ReadAllTextAsync(leasePath));
        var entry = lease.RootElement;
        var pid = entry.GetProperty("processId").GetInt32();
        if (expectedProcessId is { } expected && expected != pid) return;
        var project = Path.GetFullPath(entry.GetProperty("projectPath").GetString()!);
        if (!project.StartsWith(Path.GetFullPath(workspace) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || !process.ProcessName.Equals("UnrealEditor", StringComparison.OrdinalIgnoreCase)
                || process.StartTime.ToUniversalTime().Ticks != entry.GetProperty("startTime").GetInt64()) return;
            if (!process.CloseMainWindow())
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                return;
            }
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(closing.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Missum.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The Missum repository was not found.");
    }
}
