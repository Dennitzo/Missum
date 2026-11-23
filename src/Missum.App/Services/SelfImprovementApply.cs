using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.Ai.Client;
using Missum.Ai.Contracts;

namespace Missum.App.Services;

/// <summary>
/// Hands one verified self-improvement candidate to the external apply broker
/// (windows/self-improvement-apply.ps1) and resolves its durable outcome.
/// The operation is keyed by session and proposal, so a duplicate proposal, a lost
/// acknowledgement or an app restart always joins the same broker operation and its
/// recorded result instead of starting a second apply.
/// </summary>
internal static class SelfImprovementApply
{
    internal const string ToolName = "selfimprovement.apply";
    private static readonly string[] TerminalStatuses = ["applied", "rejected", "rolledBack", "failed"];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    private static readonly JsonSerializerOptions Indented = new(MissumAiProtocol.CreateJsonOptions()) { WriteIndented = true };
    private static readonly Runtime Default = new();
    private static readonly AsyncLocal<Runtime?> Scoped = new();
    private static Runtime Current => Scoped.Value ?? Default;

    // The hold and the planned-restart latch belong to one app process. Tests run inside their own
    // scope, so an apply under test never holds back chats of unrelated tests running in parallel.
    private sealed class Runtime
    {
        internal int Hold;
        internal int RestartRequested;
        internal Func<string, Task>? PlannedRestartHandler;
        internal Func<Guid, string, CancellationToken, Task<JsonNode?>>? ResumeSnapshotProvider;
        internal Func<ApplyContext, CancellationToken, Task<(Installation? Installation, string? Problem)>>? InstallationOverride;
        internal TimeSpan DrainTimeout = TimeSpan.FromSeconds(90);
        internal JsonObject? BrokerTimeouts;
        internal string? StateRootOverride;
        internal Func<(int ProcessId, DateTime StartedAtUtc)>? AppIdentityOverride;
    }

    internal sealed record ApplyContext(string ServerRunId, Guid? AssistantMessageId, Uri Gateway);

    internal sealed record Installation(
        string Executable, string InstallDirectory, string DataDirectory, string StackDataRoot, string EnvFile,
        IReadOnlyList<string> ComposeFiles, string Project, string CurrentImageVersion);

    /// <summary>Raised once per process when the broker waits for this app to exit.</summary>
    internal static Func<string, Task>? PlannedRestartHandler { get => Current.PlannedRestartHandler; set => Current.PlannedRestartHandler = value; }

    /// <summary>Supplies the visible chat and run checkpoint that are recorded before the restart.</summary>
    internal static Func<Guid, string, CancellationToken, Task<JsonNode?>>? ResumeSnapshotProvider { get => Current.ResumeSnapshotProvider; set => Current.ResumeSnapshotProvider = value; }

    // Tests substitute the installation, the drain timeout and the broker script.
    internal static Func<ApplyContext, CancellationToken, Task<(Installation? Installation, string? Problem)>>? InstallationOverride
    { get => Current.InstallationOverride; set => Current.InstallationOverride = value; }
    internal static TimeSpan DrainTimeout { get => Current.DrainTimeout; set => Current.DrainTimeout = value; }
    internal static JsonObject? BrokerTimeouts { get => Current.BrokerTimeouts; set => Current.BrokerTimeouts = value; }
    internal static string? StateRootOverride { get => Current.StateRootOverride; set => Current.StateRootOverride = value; }
    // Tests run the broker against a stand-in for the old app instead of the test host itself.
    internal static Func<(int ProcessId, DateTime StartedAtUtc)>? AppIdentityOverride
    { get => Current.AppIdentityOverride; set => Current.AppIdentityOverride = value; }

    /// <summary>Gives the calling test flow its own hold, restart latch and overrides until disposed.</summary>
    internal static IDisposable BeginTestScope()
    {
        Scoped.Value = new Runtime();
        return new ScopeEnd();
    }

    private sealed class ScopeEnd : IDisposable
    {
        public void Dispose() => Scoped.Value = null;
    }

    internal static bool IsPlannedRestart => Volatile.Read(ref Current.RestartRequested) != 0;

    internal static string StateRoot => StateRootOverride ?? Path.Combine(AssistantRuntimeProfile.Resolve().DataDirectory, "SelfImprovement");
    private static string PointerPath => Path.Combine(StateRoot, "active-apply.json");

    internal static string OperationId(Guid sessionId, string proposalId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("apply:" + sessionId.ToString("D") + ":" + proposalId)))[..32];

    internal static string OperationDirectory(string opId) => Path.Combine(StateRoot, "apply", opId);

    internal static bool IsApplyTool(string? name) => string.Equals(name, ToolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A reason while an apply holds new runs back; null otherwise.</summary>
    internal static string? HoldReason()
    {
        if (Volatile.Read(ref Current.Hold) == 0 && ActiveOperation() is null) return null;
        return "Missum übernimmt gerade eine geprüfte Selbstverbesserung. Neue Aufträge starten, sobald die Übernahme abgeschlossen ist.";
    }

    /// <summary>The operation the pointer names while it has not reached a terminal state.</summary>
    internal static (string OpId, Guid SessionId)? ActiveOperation()
    {
        try
        {
            if (!File.Exists(PointerPath)) return null;
            using var pointer = JsonDocument.Parse(File.ReadAllText(PointerPath));
            var opId = pointer.RootElement.GetProperty("opId").GetString();
            if (opId is not { Length: 32 }) return null;
            using var state = ReadState(OperationDirectory(opId));
            if (state is not null && IsTerminal(state.RootElement)) return null;
            return (opId, pointer.RootElement.GetProperty("sessionId").GetGuid());
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        { return null; }
    }

    /// <summary>Restarts the broker of an unfinished operation after an app start (for example after a crash).</summary>
    internal static void EnsureActiveBrokerRunning()
    {
        if (ActiveOperation() is { } active) EnsureBrokerRunning(OperationDirectory(active.OpId));
    }

    internal static async Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal, Guid sessionId, string candidateId, JsonElement report, ApplyContext context,
        CancellationToken cancellationToken)
    {
        var opId = OperationId(sessionId, proposal.ProposalId);
        var directory = OperationDirectory(opId);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (var existing = ReadState(directory))
                if (existing is not null && IsTerminal(existing.RootElement))
                    return Finish(proposal.ProposalId, opId, existing.RootElement);

            if (!File.Exists(Path.Combine(directory, "operation.json")))
            {
                if (ActiveOperation() is { } other && other.OpId != opId)
                    return Rejected(proposal.ProposalId, opId, "selfImprovement.apply_in_progress",
                        "Eine andere Übernahme ist noch nicht abgeschlossen.");
                var (installation, problem) = await (InstallationOverride ?? ResolveInstallationAsync)(context, cancellationToken).ConfigureAwait(false);
                if (installation is null)
                    return Rejected(proposal.ProposalId, opId, "selfImprovement.ownership_mismatch", problem ?? "Die Installation ist nicht eindeutig zugeordnet.");

                Interlocked.Exchange(ref Current.Hold, 1);
                var pending = await DrainAsync(context, cancellationToken).ConfigureAwait(false);
                if (pending is not null)
                {
                    Interlocked.Exchange(ref Current.Hold, 0);
                    return Rejected(proposal.ProposalId, opId, "selfImprovement.drain_pending", pending);
                }
                var resume = ResumeSnapshotProvider is null ? null
                    : await ResumeSnapshotProvider(sessionId, context.ServerRunId, cancellationToken).ConfigureAwait(false);
                await WriteOperationAsync(directory, opId, proposal, sessionId, candidateId, report, context, installation, resume, cancellationToken)
                    .ConfigureAwait(false);
            }
            EnsureBrokerRunning(directory);
        }
        catch
        {
            if (!File.Exists(Path.Combine(directory, "operation.json"))) Interlocked.Exchange(ref Current.Hold, 0);
            throw;
        }
        finally { Gate.Release(); }

        try
        {
            while (true)
            {
                using (var state = ReadState(directory))
                {
                    if (state is not null && IsTerminal(state.RootElement)) return Finish(proposal.ProposalId, opId, state.RootElement);
                    if (state is not null && Phase(state.RootElement) is not ("revalidate" or "") && Interlocked.Exchange(ref Current.RestartRequested, 1) == 0)
                    {
                        // The broker waits for this process to exit. Everything needed to resume is durable now:
                        // the journal keeps this proposal 'executing' and the run stays WaitingForClient.
                        if (PlannedRestartHandler is { } handler) _ = Task.Run(() => handler(opId), CancellationToken.None);
                    }
                    if (state is null || !IsTerminal(state.RootElement)) EnsureBrokerRunning(directory);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!IsPlannedRestart)
        {
            // Cancelled before the handoff: the broker rejects the operation without changing anything.
            await WriteJsonAsync(Path.Combine(directory, "abort.json"), new JsonObject { ["opId"] = opId, ["at"] = DateTimeOffset.UtcNow.ToString("O") })
                .ConfigureAwait(false);
            // The broker checks abort.json before every phase up to the backup, so it can no longer change
            // anything; new runs only wait for the pointer until the broker has recorded its rejection.
            Interlocked.Exchange(ref Current.Hold, 0);
            throw;
        }
    }

    /// <summary>
    /// Resolves an apply proposal that was still 'executing' when the app stopped. Returns null when no
    /// broker operation exists for it, so the caller reports an unknown outcome as for any other tool.
    /// </summary>
    internal static async Task<ClientToolResult?> ResolveRecoveredAsync(
        string proposalId, Guid sessionId, string serverRunId, Func<CancellationToken, Task<RunSnapshot>> readRun,
        CancellationToken cancellationToken)
    {
        var opId = OperationId(sessionId, proposalId);
        var directory = OperationDirectory(opId);
        if (!File.Exists(Path.Combine(directory, "operation.json"))) return null;
        var reattachPath = Path.Combine(directory, "reattach.json");
        while (true)
        {
            using (var state = ReadState(directory))
            {
                if (state is not null && IsTerminal(state.RootElement)) return Finish(proposalId, opId, state.RootElement);
                if (state is not null && Phase(state.RootElement) == "await-reattach" && !File.Exists(reattachPath))
                {
                    string status, detail = "";
                    try
                    {
                        var run = await readRun(cancellationToken).ConfigureAwait(false);
                        status = run.RunId == serverRunId && run.State is RunState.WaitingForClient or RunState.Running ? "attached" : "failed";
                        if (status != "attached") detail = $"Der Gateway-Lauf {serverRunId} ist im Zustand {run.State}.";
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException
                        && !cancellationToken.IsCancellationRequested)
                    {
                        status = "failed";
                        detail = "Der Gateway-Lauf ist nicht lesbar: " + exception.Message;
                    }
                    await WriteJsonAsync(reattachPath, new JsonObject
                    {
                        ["opId"] = opId, ["serverRunId"] = serverRunId, ["sessionId"] = sessionId.ToString("D"),
                        ["status"] = status, ["detail"] = detail, ["pid"] = Environment.ProcessId,
                        ["at"] = DateTimeOffset.UtcNow.ToString("O"),
                    }).ConfigureAwait(false);
                }
                if (state is null || !IsTerminal(state.RootElement)) EnsureBrokerRunning(directory);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ClientToolResult Finish(string proposalId, string opId, JsonElement state)
    {
        ReleaseIfActive(opId);
        var status = S(state, "status");
        var code = S(state, "code");
        var detail = S(state, "detail");
        var output = new JsonObject
        {
            ["opId"] = opId, ["candidateId"] = S(state, "candidateId"), ["status"] = status,
            ["changeApp"] = state.TryGetProperty("changeApp", out var app) && app.ValueKind == JsonValueKind.True,
            ["changeStack"] = state.TryGetProperty("changeStack", out var stack) && stack.ValueKind == JsonValueKind.True,
            ["detail"] = detail, ["completedAt"] = S(state, "completedAt"),
        };
        if (state.TryGetProperty("problems", out var problems) && problems.ValueKind == JsonValueKind.Array)
            output["problems"] = JsonNode.Parse(problems.GetRawText());
        if (code.Length > 0) output["cause"] = code;
        var result = JsonSerializer.SerializeToElement(output, Json);
        return status switch
        {
            "applied" => new(proposalId, "completed", result),
            "rejected" => new(proposalId, "rejected", result, code.Length > 0 ? code : "selfImprovement.apply_rejected", detail),
            "rolledBack" => new(proposalId, "failed", result, "selfImprovement.rolled_back", detail),
            _ => new(proposalId, "failed", result, "selfImprovement.rollback_failed",
                detail + " Chat und Checkpoint bleiben erhalten; prüfe die Installation, bevor du fortfährst."),
        };
    }

    private static ClientToolResult Rejected(string proposalId, string opId, string code, string detail) =>
        new(proposalId, "rejected", JsonSerializer.SerializeToElement(new JsonObject
        {
            ["opId"] = opId, ["status"] = "rejected", ["detail"] = detail,
            ["problems"] = new JsonArray(new JsonObject { ["code"] = code, ["detail"] = detail }),
        }, Json), code, detail);

    private static void ReleaseIfActive(string opId)
    {
        try
        {
            if (File.Exists(PointerPath))
            {
                using var pointer = JsonDocument.Parse(File.ReadAllText(PointerPath));
                if (pointer.RootElement.TryGetProperty("opId", out var id) && id.GetString() == opId) File.Delete(PointerPath);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        Interlocked.Exchange(ref Current.Hold, 0);
    }

    // Holds the run open until every other gateway run (including background jobs) has finished.
    private static async Task<string?> DrainAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = context.Gateway, Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(MissumAiHeaders.ClientId, "missum-self-improvement");
        var deadline = DateTime.UtcNow + DrainTimeout;
        var active = "";
        do
        {
            try
            {
                using var response = await http.GetAsync("v1/runs/activity?except=" + Uri.EscapeDataString(context.ServerRunId), cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                    return $"Das Gateway meldet keine Laufaktivität (HTTP {(int)response.StatusCode}); die Übernahme wird nicht begonnen.";
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var runs = body.RootElement.GetProperty("activeRuns").EnumerateArray()
                    .Select(run => $"{S(run, "runId")} ({S(run, "state")}, {S(run, "mode")})").ToArray();
                if (runs.Length == 0) return null;
                active = string.Join(", ", runs);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return "Das Gateway ist nicht erreichbar; die Übernahme wird nicht begonnen: " + exception.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        } while (DateTime.UtcNow < deadline);
        return "Andere Läufe sind noch aktiv und wurden nicht unterbrochen: " + active + ". Versuche die Übernahme danach erneut.";
    }

    /// <summary>Accepts only a portable installation and the stack container that belong to this Missum profile.</summary>
    internal static async Task<(Installation? Installation, string? Problem)> ResolveInstallationAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        if (!context.Gateway.IsLoopback)
            return (null, "Das Gateway ist nicht lokal verwaltet; die Übernahme ist nur für den lokalen Missum-AI-Stack möglich.");
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !string.Equals(Path.GetFileName(executable), "Missum Labs.exe", StringComparison.OrdinalIgnoreCase))
            return (null, "Missum läuft nicht aus einer Portable-Installation (Missum Labs.exe); eine Entwicklungsinstanz wird nicht ersetzt.");
        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var repository = Path.GetFullPath(SelfImprovementTool.Repository).TrimEnd('\\') + "\\";
        if ((installDirectory + "\\").StartsWith(repository, StringComparison.OrdinalIgnoreCase))
            return (null, "Missum läuft aus dem Quellrepository; nur eine separate Portable-Installation wird ersetzt.");
        try
        {
            var probe = Path.Combine(installDirectory, ".missum-write-probe-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(probe, "", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return (null, "Das Installationsverzeichnis ist nicht beschreibbar: " + exception.Message); }

        var profile = AssistantRuntimeProfile.Resolve();
        // The install directory is replaced in place: data that lives inside it would be lost.
        foreach (var data in new[] { profile.DataDirectory, profile.StackDataRoot, StateRoot })
            if ((Path.GetFullPath(data).TrimEnd('\\') + "\\").StartsWith(installDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return (null, $"Das Datenverzeichnis {data} liegt im Installationsverzeichnis; die Übernahme würde es überschreiben.");
        var ids = (await DockerAsync(["ps", "-a", "-q", "--filter", "label=com.docker.compose.project=" + MissumAiStackLifecycleService.ComposeProject,
            "--filter", "label=com.docker.compose.service=gateway"], cancellationToken).ConfigureAwait(false))?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids is not { Length: 1 })
            return (null, "Der Gateway-Container dieser Missum-Installation ist nicht eindeutig auffindbar.");
        var inspect = await DockerAsync(["inspect", ids[0]], cancellationToken).ConfigureAwait(false);
        if (inspect is null) return (null, "Der Gateway-Container konnte nicht geprüft werden.");
        using var document = JsonDocument.Parse(inspect);
        var container = document.RootElement[0];
        var expectedData = Path.Combine(profile.StackDataRoot, "data");
        if (!container.GetProperty("Mounts").EnumerateArray().Any(mount => S(mount, "Destination") == "/data"
                && MissumAiStackLifecycleService.PathsMatch(S(mount, "Source"), expectedData)))
            return (null, $"Der Gateway-Container verwendet nicht das Datenverzeichnis dieser Installation ({expectedData}).");
        var labels = container.GetProperty("Config").GetProperty("Labels");
        var composeFiles = S(labels, "com.docker.compose.project.config_files").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (composeFiles.Length == 0 || composeFiles.Any(file => !File.Exists(file)))
            return (null, "Die Compose-Dateien des Gateways sind nicht vorhanden.");
        var image = S(container.GetProperty("Config"), "Image");
        var version = image.Contains(':') ? image[(image.LastIndexOf(':') + 1)..] : "";
        if (!image.StartsWith("missum-ai/gateway:", StringComparison.Ordinal) || version.Length == 0)
            return (null, $"Der Gateway-Container verwendet kein verwaltetes Missum-Image ({image}).");
        var envFile = Path.Combine(profile.StackDataRoot, "stack.env");
        if (!File.Exists(envFile)) return (null, "Die Stack-Konfiguration (stack.env) fehlt.");
        return (new(Path.GetFullPath(executable), installDirectory, profile.DataDirectory, profile.StackDataRoot, envFile,
            composeFiles, MissumAiStackLifecycleService.ComposeProject, version), null);
    }

    private static async Task WriteOperationAsync(string directory, string opId, ToolProposal proposal, Guid sessionId, string candidateId,
        JsonElement report, ApplyContext context, Installation installation, JsonNode? resume, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var artifacts = report.GetProperty("artifacts");
        using var self = Process.GetCurrentProcess();
        var (appPid, appStarted) = AppIdentityOverride?.Invoke() ?? (Environment.ProcessId, self.StartTime.ToUniversalTime());
        var environment = new JsonObject();
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = (string)variable.Key;
            if (name.StartsWith("ASSISTANT_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MISSUM_", StringComparison.OrdinalIgnoreCase))
                environment[name] = (string?)variable.Value;
        }
        var operation = new JsonObject
        {
            ["opId"] = opId, ["candidateId"] = candidateId, ["sessionId"] = sessionId.ToString("D"),
            ["proposalId"] = proposal.ProposalId, ["serverRunId"] = context.ServerRunId,
            ["assistantMessageId"] = context.AssistantMessageId?.ToString("D"),
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["app"] = new JsonObject
            {
                ["pid"] = appPid, ["startedAt"] = appStarted.ToString("O"),
                ["executable"] = installation.Executable, ["installDir"] = installation.InstallDirectory, ["dataDir"] = installation.DataDirectory,
            },
            ["stack"] = new JsonObject
            {
                ["dataRoot"] = installation.StackDataRoot, ["envFile"] = installation.EnvFile,
                ["composeFiles"] = new JsonArray(installation.ComposeFiles.Select(file => (JsonNode?)file).ToArray()),
                ["project"] = installation.Project, ["gatewayUrl"] = context.Gateway.ToString(),
                ["currentImageVersion"] = installation.CurrentImageVersion,
            },
            ["candidate"] = new JsonObject
            {
                ["imageVersion"] = "candidate-" + candidateId.ToLowerInvariant(),
                ["images"] = JsonNode.Parse(artifacts.GetProperty("images").GetRawText()),
                ["portableDirectory"] = S(artifacts, "portableDirectory"), ["portableFiles"] = S(artifacts, "portableFiles"),
            },
            ["resume"] = resume,
            ["environment"] = environment,
        };
        if (BrokerTimeouts is not null) operation["timeouts"] = BrokerTimeouts.DeepClone();
        File.Copy(ApplicationAssets.ResolvePath("Assets", "SelfImprovement", "self-improvement-apply.ps1"),
            Path.Combine(directory, "self-improvement-apply.ps1"), overwrite: true);
        await WriteJsonAsync(Path.Combine(directory, "operation.json"), operation).ConfigureAwait(false);
        Directory.CreateDirectory(StateRoot);
        await WriteJsonAsync(PointerPath, new JsonObject { ["opId"] = opId, ["sessionId"] = sessionId.ToString("D") }).ConfigureAwait(false);
    }

    internal static JsonDocument? ReadState(string directory)
    {
        var path = Path.Combine(directory, "state.json");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonDocument.Parse(stream);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }

    private static bool IsTerminal(JsonElement state) => TerminalStatuses.Contains(S(state, "status"));
    private static string Phase(JsonElement state) => S(state, "phase");

    /// <summary>The supervisor holds supervisor.lock for its whole life; a free lock means no broker is running.</summary>
    internal static bool BrokerIsRunning(string directory)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(directory, "supervisor.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static void EnsureBrokerRunning(string directory)
    {
        if (BrokerIsRunning(directory)) return;
        var script = Path.Combine(directory, "self-improvement-apply.ps1");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var command = $"\"{shell}\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -OperationPath \"{Path.Combine(directory, "operation.json")}\"";
        StartDetached(shell, command, directory);
        // Give the supervisor a moment to take its lock, so a concurrent check does not start a second one.
        for (var i = 0; i < 40 && !BrokerIsRunning(directory); i++) Thread.Sleep(50);
    }

    // The broker must outlive the app: leave a kill-on-close job when Windows allows it.
    private static void StartDetached(string application, string commandLine, string directory)
    {
        const uint CreateBreakawayFromJob = 0x01000000, CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x00000400, CreateNewProcessGroup = 0x00000200;
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        foreach (var flags in new[] { CreateBreakawayFromJob | CreateNoWindow | CreateUnicodeEnvironment | CreateNewProcessGroup,
                     CreateNoWindow | CreateUnicodeEnvironment | CreateNewProcessGroup })
        {
            // CreateProcessW may modify the command line, so it gets a writable native copy.
            var buffer = Marshal.StringToHGlobalUni(commandLine);
            int error;
            try
            {
                if (CreateProcessW(application, buffer, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, directory, ref startup, out var info))
                {
                    CloseHandle(info.hProcess);
                    CloseHandle(info.hThread);
                    return;
                }
                error = Marshal.GetLastWin32Error();
            }
            finally { Marshal.FreeHGlobal(buffer); }
            if (error != 5) throw new Win32Exception(error, "Der Übernahme-Broker konnte nicht gestartet werden.");
        }
        throw new Win32Exception(5, "Der Übernahme-Broker konnte nicht gestartet werden.");
    }

    private static async Task WriteJsonAsync(string path, JsonNode node)
    {
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        await File.WriteAllTextAsync(pending, node.ToJsonString(Indented), new UTF8Encoding(false)).ConfigureAwait(false);
        File.Move(pending, path, overwrite: true);
    }

    private static async Task<string?> DockerAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (Win32Exception) { return null; }
    }

    private static string S(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string application, IntPtr commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
