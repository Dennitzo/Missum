using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;

namespace Missum.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SelfImprovementApplyTestGroup
{
    public const string Name = "Self-improvement apply";
}

/// <summary>
/// An isolated installation for one apply: its own install directory, app data with WAL databases,
/// stack data, state root, fake gateway on a free loopback port and a scriptable fake app.
/// The old app is the installed app.cmd (v1) started by the test; the candidate contains app.cmd (v2).
/// </summary>
internal sealed class SelfImprovementApplyRig : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    private static readonly string Shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private readonly IDisposable _scope;
    private Process? _oldApp;

    internal string Root { get; }
    internal string Install => Path.Combine(Root, "install");
    internal string Portable => Path.Combine(Root, "candidate", "portable");
    internal string PortableManifest => Path.Combine(Root, "candidate", "portable-files.json");
    internal string Data => Path.Combine(Root, "data");
    internal string Stack => Path.Combine(Root, "stack");
    internal string EnvFile => Path.Combine(Stack, "stack.env");
    internal string StateRoot => Path.Combine(Root, "state");
    internal string StartsLog => Path.Combine(Root, "starts.log");
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal string ServerRunId { get; } = "run-si-" + Guid.NewGuid().ToString("N")[..8];
    internal string CandidateId { get; } = "sitest" + Guid.NewGuid().ToString("N")[..10];
    internal FakeGateway Gateway { get; }
    internal JsonObject Images { get; set; } = new();
    internal string Executable { get; set; }
    internal IReadOnlyList<string> ComposeFiles { get; set; } = [];
    internal string Project { get; set; } = "missum-sitest-unused";
    internal string CurrentImageVersion { get; set; } = "2.0.0";
    internal TaskCompletionSource<string> Handoff { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource OldAppCancellation { get; } = new();

    private SelfImprovementApplyRig(string root, FakeGateway gateway)
    {
        Root = root;
        Gateway = gateway;
        Executable = Path.Combine(Install, "app.cmd");
        _scope = SelfImprovementApply.BeginTestScope();
    }

    // Synchronous on purpose: the test scope is an AsyncLocal, and only a synchronous call sets it for the caller.
    internal static SelfImprovementApplyRig Create()
    {
        foreach (var name in new[] { "MISSUM_SELF_IMPROVEMENT_TEST_FAULTS", "MISSUM_SELF_IMPROVEMENT_FAULT" })
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "msi-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);
        var gateway = FakeGateway.StartAsync().GetAwaiter().GetResult();
        var rig = new SelfImprovementApplyRig(root, gateway);
        rig.Gateway.EnvFile = rig.EnvFile;
        rig.Gateway.RunId = rig.ServerRunId;
        rig.Layout();
        SelfImprovementApply.StateRootOverride = rig.StateRoot;
        SelfImprovementApply.DrainTimeout = TimeSpan.FromSeconds(2);
        SelfImprovementApply.BrokerTimeouts = new JsonObject
        {
            ["appExitSeconds"] = 30, ["drainSeconds"] = 3, ["gatewaySeconds"] = 20, ["reattachSeconds"] = 45,
        };
        SelfImprovementApply.InstallationOverride = (_, _) => Task.FromResult<(SelfImprovementApply.Installation?, string?)>(
            (new(rig.Executable, rig.Install, rig.Data, rig.Stack, rig.EnvFile, rig.ComposeFiles, rig.Project, rig.CurrentImageVersion), null));
        SelfImprovementApply.AppIdentityOverride = () => (rig._oldApp!.Id, rig._oldApp.StartTime.ToUniversalTime());
        SelfImprovementApply.PlannedRestartHandler = opId =>
        {
            // The old app ends itself for the planned restart: its process tree goes, its in-flight call is cancelled.
            rig.StopOldApp();
            rig.OldAppCancellation.Cancel();
            rig.Handoff.TrySetResult(opId);
            return Task.CompletedTask;
        };
        return rig;
    }

    private void Layout()
    {
        Directory.CreateDirectory(StateRoot);
        File.WriteAllText(Path.Combine(Root, "fakeapp.ps1"), """
            param([string] $Version, [string] $Root)
            Add-Content -LiteralPath (Join-Path $Root 'starts.log') -Value ("$Version " + $PID)
            $modeFile = Join-Path $Root ("mode-" + $Version + ".txt")
            $mode = if (Test-Path -LiteralPath $modeFile) { (Get-Content -LiteralPath $modeFile -Raw).Trim() } else { 'wait' }
            if ($mode -eq 'exit') { exit 1 }
            if ($mode -eq 'migrate-exit') {
                [IO.File]::WriteAllText((Join-Path $Root 'data\Missum.db'), 'half-migrated')
                Remove-Item -LiteralPath (Join-Path $Root 'data\Missum.db-wal') -ErrorAction SilentlyContinue
                exit 1
            }
            $deadline = (Get-Date).AddSeconds(240)
            while (-not (Test-Path -LiteralPath (Join-Path $Root ("stop-" + $Version + ".flag"))) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
            """, new UTF8Encoding(true));
        WriteApp(Install, "v1");
        Write(Path.Combine(Install, "lib", "core.txt"), "core v1");
        Write(Path.Combine(Install, "lib", "removed.txt"), "only in v1");
        WriteApp(Portable, "v2");
        Write(Path.Combine(Portable, "lib", "core.txt"), "core v2");
        Write(Path.Combine(Portable, "lib", "added.txt"), "only in v2");
        WritePortableManifest();

        Directory.CreateDirectory(Data);
        SeedWalDatabase(Path.Combine(Data, "Missum.db"), "chat");
        SeedWalDatabase(Path.Combine(Data, "project-memory.db"), "memory");
        Directory.CreateDirectory(Path.Combine(Stack, "data", "database"));
        File.WriteAllText(EnvFile, "MISSUM_AI_IMAGE_VERSION=" + CurrentImageVersion + "\nMISSUM_AI_OTHER=kept\n");
    }

    private readonly List<string> _dockerImages = [];
    private bool _stackUp;
    internal string? LiveImageId { get; private set; }
    internal string? CandidateImageId { get; private set; }
    internal string OriginalEnv { get; private set; } = "";
    internal string GatewayDatabase => Path.Combine(Stack, "data", "database", "missum-ai-server.db");

    /// <summary>
    /// Puts a real compose stack behind the installation: its own project, its own image tags (built FROM the local
    /// gateway image with a distinguishing label, so digests differ) and a bind mount of this rig's stack data.
    /// The container only sleeps; health and runs still come from the loopback fake gateway.
    /// </summary>
    internal void UseStack(bool changeApp)
    {
        var suffix = CandidateId.ToLowerInvariant();
        CurrentImageVersion = "sitest-" + suffix;
        Project = "missum-sitest-" + suffix;
        var live = "missum-ai/gateway:" + CurrentImageVersion;
        var candidate = "missum-ai/gateway:candidate-" + suffix;
        LiveImageId = BuildImage(live, "live-" + suffix);
        CandidateImageId = BuildImage(candidate, "candidate-" + suffix);
        Images = new JsonObject { [candidate] = CandidateImageId };
        if (!changeApp)
        {
            Directory.Delete(Portable, true);
            CopyTree(Install, Portable);
            WritePortableManifest();
        }
        var compose = Path.Combine(Stack, "compose.yaml");
        File.WriteAllText(compose, """
            services:
              gateway:
                image: missum-ai/gateway:${MISSUM_AI_IMAGE_VERSION:?}
                entrypoint: ["sleep", "infinity"]
                volumes:
                  - ./data:/data
            """);
        ComposeFiles = [compose];
        OriginalEnv = "MISSUM_AI_IMAGE_VERSION=" + CurrentImageVersion + "\nMISSUM_AI_OTHER=kept\n";
        File.WriteAllText(EnvFile, OriginalEnv);
        SeedWalDatabase(GatewayDatabase, "gateway");
        _stackUp = true;
        Docker(null, "compose", "--env-file", EnvFile, "-p", Project, "-f", compose, "up", "-d");
        Assert.Equal(LiveImageId, GatewayContainerImage());
    }

    private string BuildImage(string tag, string label)
    {
        _dockerImages.Add(tag);
        Docker("FROM missum-ai/gateway:2.0.0\nLABEL missum.sitest=\"" + label + "\"\n", "build", "-q", "-t", tag, "-");
        return Docker(null, "image", "inspect", "--format", "{{.Id}}", tag).Trim();
    }

    internal string GatewayContainerImage()
    {
        var id = Docker(null, "ps", "-q", "--filter", "label=com.docker.compose.project=" + Project, "--filter", "label=com.docker.compose.service=gateway").Trim();
        Assert.False(string.IsNullOrEmpty(id), "Der Test-Gateway-Container läuft nicht.");
        return Docker(null, "inspect", "--format", "{{.Image}}", id).Trim();
    }

    internal static string Docker(string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000)) { process.Kill(true); throw new TimeoutException("docker " + string.Join(' ', arguments)); }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker {string.Join(' ', arguments)} failed ({process.ExitCode}): {error.Result}");
        return output.Result;
    }

    private void CleanUpDocker()
    {
        if (_stackUp)
            try { Docker(null, "compose", "--env-file", EnvFile, "-p", Project, "-f", ComposeFiles[0], "down", "--remove-orphans", "--timeout", "1"); }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
        foreach (var tag in _dockerImages)
            try { Docker(null, "image", "rm", "-f", tag); }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
    }

    internal void WriteApp(string directory, string version) => Write(Path.Combine(directory, "app.cmd"),
        $"@\"{Shell}\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(Root, "fakeapp.ps1")}\" -Version {version} -Root \"{Root}\"\r\n");

    internal void WritePortableManifest()
    {
        var entries = Directory.EnumerateFiles(Portable, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => new JsonObject { ["path"] = Path.GetRelativePath(Portable, file).Replace('\\', '/'), ["sha256"] = Hash(file) });
        File.WriteAllText(PortableManifest, new JsonArray(entries.ToArray<JsonNode?>()).ToJsonString());
    }

    internal static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    internal static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    /// <summary>Leaves rows that exist only in the WAL: a backup without its -wal file would lose them.</summary>
    internal static void SeedWalDatabase(string path, string label)
    {
        var scratch = path + ".seed";
        using (var connection = new SqliteConnection($"Data Source={scratch};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE rows(value TEXT NOT NULL);");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            for (var index = 0; index < 5; index++) Execute(connection, $"INSERT INTO rows(value) VALUES ('{label}-{index}');");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(scratch + suffix))
                {
                    using var source = new FileStream(scratch + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = File.Create(path + suffix);
                    source.CopyTo(target);
                }
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(scratch + suffix);
        if (new FileInfo(path + "-wal").Length == 0) throw new InvalidOperationException("The WAL fixture holds no rows.");
    }

    internal static string[] ReadRows(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM rows ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return [.. rows];
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal void SetMode(string version, string mode) => File.WriteAllText(Path.Combine(Root, "mode-" + version + ".txt"), mode);

    internal static void SetFaults(string? faults)
    {
        Environment.SetEnvironmentVariable("MISSUM_SELF_IMPROVEMENT_TEST_FAULTS", faults is null ? null : "1");
        Environment.SetEnvironmentVariable("MISSUM_SELF_IMPROVEMENT_FAULT", faults);
    }

    /// <summary>Waits until the fake app logged this many starts (it logs a moment after its process starts), then a bit longer for extra ones.</summary>
    internal async Task<string[]> StartsAfterAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Starts().Length < count && DateTime.UtcNow < deadline) await Task.Delay(100);
        await Task.Delay(1500);
        return Starts();
    }

    internal string[] Starts()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(StartsLog)) return [];
                // The fake app appends while this reads; share the file instead of failing on its write handle.
                using var stream = new FileStream(StartsLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.Split(' ')[0]).ToArray();
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }

    internal void StartOldApp()
    {
        _oldApp = Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Install })!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!Starts().Contains("v1") && DateTime.UtcNow < deadline) Thread.Sleep(100);
        Assert.Contains("v1", Starts());
    }

    internal void StopOldApp()
    {
        try { if (_oldApp is { HasExited: false }) _oldApp.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    internal bool OldAppAlive => _oldApp is { HasExited: false };

    internal JsonElement Report() => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["candidateId"] = CandidateId,
        ["artifacts"] = new JsonObject
        {
            ["images"] = Images.DeepClone(), ["portableDirectory"] = Portable, ["portableFiles"] = PortableManifest,
        },
    });

    internal ToolProposal Proposal(string id) => new(id, ServerRunId, SelfImprovementApply.ToolName,
        JsonSerializer.SerializeToElement(new { candidateId = CandidateId }), ToolRiskClass.Process, "Kandidat übernehmen",
        DateTimeOffset.UtcNow.AddHours(1));

    internal SelfImprovementApply.ApplyContext Context => new(ServerRunId, Guid.NewGuid(), Gateway.Url);
    internal string OpId(string proposalId) => SelfImprovementApply.OperationId(SessionId, proposalId);
    internal string OpDirectory(string proposalId) => SelfImprovementApply.OperationDirectory(OpId(proposalId));

    internal Task<ClientToolResult> ExecuteAsync(string proposalId, CancellationToken cancellationToken) =>
        SelfImprovementApply.ExecuteAsync(Proposal(proposalId), SessionId, CandidateId, Report(), Context, cancellationToken);

    internal JsonElement State(string proposalId)
    {
        using var state = SelfImprovementApply.ReadState(OpDirectory(proposalId));
        return state?.RootElement.Clone() ?? default;
    }

    internal string Phase(string proposalId) => State(proposalId) is { ValueKind: JsonValueKind.Object } state
        && state.TryGetProperty("phase", out var phase) ? phase.GetString() ?? "" : "";

    internal string Code(string proposalId) => State(proposalId) is { ValueKind: JsonValueKind.Object } state
        && state.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() ?? "" : "";

    internal string FaultMarker(string proposalId, string kind, string phase) =>
        Path.Combine(OpDirectory(proposalId), "fault-" + kind + "-" + phase + ".fired");

    internal void Release(string proposalId, string phase) => File.WriteAllText(Path.Combine(OpDirectory(proposalId), "release-" + phase), "");

    internal void AssertInstallIsV1()
    {
        Assert.Equal("core v1", File.ReadAllText(Path.Combine(Install, "lib", "core.txt")));
        Assert.True(File.Exists(Path.Combine(Install, "lib", "removed.txt")));
        Assert.False(File.Exists(Path.Combine(Install, "lib", "added.txt")));
        Assert.Contains("-Version v1", File.ReadAllText(Path.Combine(Install, "app.cmd")));
    }

    internal void AssertDataPreserved()
    {
        Assert.Equal(["chat-0", "chat-1", "chat-2", "chat-3", "chat-4"], ReadRows(Path.Combine(Data, "Missum.db")));
        Assert.Equal(5, ReadRows(Path.Combine(Data, "project-memory.db")).Length);
    }

    internal string Status(string proposalId) => State(proposalId) is { ValueKind: JsonValueKind.Object } state
        && state.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "";

    internal int BrokerStarts(string proposalId) => State(proposalId) is { ValueKind: JsonValueKind.Object } state
        ? state.GetProperty("history").EnumerateArray().Count(entry => entry.GetProperty("event").GetString() == "broker-start") : 0;

    internal Task<RunSnapshot> ReadRunAsync(CancellationToken cancellationToken)
    {
        var client = new MissumAiClient(new HttpClient { BaseAddress = Gateway.Url, Timeout = TimeSpan.FromSeconds(10) }, "missum-sitest", ownsHttpClient: true);
        return ReadAndDispose(client, cancellationToken);

        async Task<RunSnapshot> ReadAndDispose(MissumAiClient owned, CancellationToken token)
        {
            using (owned) return await owned.GetRunAsync(ServerRunId, token);
        }
    }

    /// <summary>
    /// Drives one apply as the real processes would: the old app calls the tool and ends at the handoff,
    /// the broker works alone, and the new app (once started) resolves the recovered proposal.
    /// With <paramref name="newAppResumes"/> false the started app never reattaches (it crashed or never came up);
    /// the result is then read the way the next app start (usually the restored version) reads it.
    /// </summary>
    internal async Task<ClientToolResult> ApplyAsync(string proposalId,
        Func<CancellationToken, Task<RunSnapshot>>? readRun = null, Func<Task>? whileBrokerAlone = null, TimeSpan? timeout = null,
        bool newAppResumes = true)
    {
        if (!OldAppAlive) StartOldApp();
        using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(3));
        var execute = ExecuteAsync(proposalId, OldAppCancellation.Token);
        var first = await Task.WhenAny(execute, Handoff.Task).WaitAsync(limit.Token);
        if (first == execute && execute.IsCompletedSuccessfully) return await execute;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute);
        Assert.True(SelfImprovementApply.IsPlannedRestart);
        if (whileBrokerAlone is not null) await whileBrokerAlone();
        // No app runs between the old app's exit and the new app's start: nothing restarts the broker here.
        if (newAppResumes)
            while (Phase(proposalId) is not ("await-reattach" or "complete" or "rollback") && Status(proposalId) == "running")
                await Task.Delay(200, limit.Token);
        else
            while (Status(proposalId) is "running" or "")
                await Task.Delay(200, limit.Token);
        return await SelfImprovementApply.ResolveRecoveredAsync(proposalId, SessionId, ServerRunId, readRun ?? ReadRunAsync, limit.Token)
            ?? throw new InvalidOperationException("The broker operation disappeared.");
    }

    internal async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Not created in time: " + path + Environment.NewLine + Diagnostics());
            await Task.Delay(100);
        }
    }

    internal async Task WaitForStatusAsync(string proposalId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Status(proposalId) is "running" or "")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No terminal state." + Environment.NewLine + Diagnostics());
            await Task.Delay(200);
        }
    }

    /// <summary>Kills the supervisor and worker of one operation (every process whose command line names it).</summary>
    internal static void KillProcesses(string needle) => RunPowerShell(
        "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.Contains('" + needle.Replace("'", "''") + "') } | "
        + "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }");

    internal static string RunPowerShell(string command)
    {
        var start = new ProcessStartInfo(Shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    internal string Diagnostics()
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.Exists(Path.Combine(StateRoot, "apply"))
                     ? Directory.EnumerateFiles(Path.Combine(StateRoot, "apply"), "state.json", SearchOption.AllDirectories) : [])
            builder.AppendLine(file).AppendLine(File.ReadAllText(file));
        if (File.Exists(StartsLog)) builder.AppendLine("starts: " + string.Join(", ", Starts()));
        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        SetFaults(null);
        foreach (var version in new[] { "v1", "v2" }) File.WriteAllText(Path.Combine(Root, "stop-" + version + ".flag"), "");
        StopOldApp();
        await Task.Delay(400);
        KillProcesses(Root);
        CleanUpDocker();
        _scope.Dispose();
        OldAppCancellation.Dispose();
        await Gateway.DisposeAsync();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); break; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { await Task.Delay(300); }
        }
    }
}

/// <summary>
/// A loopback stand-in for the Missum AI gateway: health, readiness, run activity and run snapshots.
/// Faults marked "candidate" apply only while stack.env selects a candidate image, so a rollback heals them.
/// </summary>
internal sealed class FakeGateway : IAsyncDisposable
{
    private readonly WebApplication _app;
    private FakeGateway(WebApplication app, Uri url) { _app = app; Url = url; }

    internal Uri Url { get; }
    internal string RunId { get; set; } = "";
    internal string? EnvFile { get; set; }
    internal volatile string RunState = "waitingForClient";
    internal string? CandidateRunState { get; set; }
    internal string? CandidateProtocol { get; set; }
    internal bool CandidateDown { get; set; }
    internal volatile string[] ActiveRuns = [];
    internal int ActivityRequests;

    private bool CandidateActive => EnvFile is not null && File.Exists(EnvFile)
        && File.ReadAllText(EnvFile).Contains("MISSUM_AI_IMAGE_VERSION=candidate-", StringComparison.Ordinal);

    internal static async Task<FakeGateway> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeGateway? gateway = null;
        app.MapGet("/v1/health/live", () => gateway!.CandidateActive && gateway.CandidateDown
            ? Results.StatusCode(502)
            : Results.Json(new { status = "live", protocolVersion = gateway!.CandidateActive ? gateway.CandidateProtocol ?? "1.0" : "1.0", timestamp = DateTimeOffset.UtcNow }));
        app.MapGet("/v1/health/ready", () => Results.Json(new { status = "ready" }));
        app.MapGet("/v1/runs/activity", () =>
        {
            Interlocked.Increment(ref gateway!.ActivityRequests);
            return Results.Json(new { activeRuns = gateway.ActiveRuns.Select(id => new { runId = id, state = "running", mode = "general" }) });
        });
        app.MapGet("/v1/runs/{id}", (string id) =>
        {
            if (id != gateway!.RunId) return Results.NotFound();
            var state = gateway.CandidateActive ? gateway.CandidateRunState ?? gateway.RunState : gateway.RunState;
            return Results.Text($$"""{"runId":"{{id}}","state":"{{state}}","mode":"coding","selectedModel":"fixture/model","sessionTitle":null,"lastEventId":8,"createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"}""",
                "application/json");
        });
        await app.StartAsync();
        gateway = new FakeGateway(app, new Uri(app.Urls.First().TrimEnd('/') + "/"));
        return gateway;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
