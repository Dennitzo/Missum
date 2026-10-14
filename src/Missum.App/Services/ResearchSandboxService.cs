using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Research;

namespace Missum.App.Services;

/// <summary>
/// Owns each Claude Science project's private data tree and executes Python only
/// in a constrained Docker container. A missing or unbuildable runner is a hard
/// failure; this service never falls back to host PowerShell or host Python.
/// </summary>
public sealed partial class ResearchSandboxService(AssistantRuntimeProfile profile, Missum.Core.Contracts.IChatRepository? chats = null) : IResearchSandboxService, IDisposable
{
    public const string RunnerImage = "missum-ai/research-runner:2";
    private const long MaximumProjectBytes = 100L * 1024 * 1024 * 1024;
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private static readonly Regex SafeId = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ExecutionInputRoots = ["work", "inputs", "env", "manuscripts"];
    private static readonly string[] ExecutionOutputRoots = ["artifacts", "notebooks"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Root => Path.Combine(profile.DataDirectory, "ResearchSandbox");
    private string Trash => Path.Combine(Root, "Trash");

    private async Task<string> ResolveStorageRootAsync(string projectId, CancellationToken cancellationToken)
    {
        var storageRoot = Root;
        if (chats is not null && projectId.StartsWith("research-", StringComparison.Ordinal)
            && Guid.TryParseExact(projectId[9..], "N", out var sessionId))
        {
            var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die Forschungssitzung wurde nicht gefunden.");
            var workspace = session.CodingWorkspacePath;
            if (string.IsNullOrWhiteSpace(workspace) && session.SessionGroupId is { } groupId)
                workspace = (await chats.ListSessionGroupsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(group => group.Id == groupId)?.WorkspacePath;
            if (string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace))
                throw new InvalidOperationException("Wähle für Claude Science einen vorhandenen Projektordner in der Sidebar.");
            storageRoot = Path.Combine(Path.GetFullPath(workspace), "Science");
        }
        return storageRoot;
    }

    public async Task<ResearchSandboxLayout> EnsureProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        var storageRoot = await ResolveStorageRootAsync(projectId, cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(Path.Combine(storageRoot, projectId));
        EnsureBelowRoot(root, Path.GetFullPath(storageRoot));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(storageRoot);
            RejectReparsePoint(Path.GetFullPath(storageRoot));
            var legacy = Path.GetFullPath(Path.Combine(Root, projectId));
            if (!Directory.Exists(root) && Directory.Exists(legacy) && !string.Equals(root, legacy, StringComparison.OrdinalIgnoreCase))
            {
                // Copy once; preserve the original research archive until the new
                // workspace has been used successfully. Never follow junctions.
                RejectReparsePoint(legacy);
                var staging = Path.Combine(storageRoot, ".migration-" + Guid.NewGuid().ToString("N"));
                EnsureBelowRoot(staging, Path.GetFullPath(storageRoot));
                Directory.CreateDirectory(staging);
                try
                {
                    var pending = new Queue<string>(); pending.Enqueue(legacy);
                    while (pending.TryDequeue(out var sourceDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        RejectReparsePoint(sourceDirectory);
                        var destination = Path.Combine(staging, Path.GetRelativePath(legacy, sourceDirectory));
                        Directory.CreateDirectory(destination);
                        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
                        { RejectReparsePoint(file); File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false); }
                        foreach (var child in Directory.EnumerateDirectories(sourceDirectory)) { RejectReparsePoint(child); pending.Enqueue(child); }
                    }
                    Directory.Move(staging, root);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            }
            Directory.CreateDirectory(root);
            RejectReparsePoint(root);
            foreach (var name in new[] { "inputs", "work", "artifacts", "notebooks", "manuscripts", "env", "runs", "snapshots" })
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                RejectReparsePoint(path);
            }
            var manifest = Path.Combine(root, "sandbox.json");
            if (!File.Exists(manifest))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 2,
                    projectId,
                    createdAt = DateTimeOffset.UtcNow,
                    runtime = "docker-linux-isolated",
                    network = "disabled",
                    limits = new { cpuCores = 16, memoryBytes = 48L * 1024 * 1024 * 1024,
                        maximumActiveProjectBytes = MaximumProjectBytes,
                        minimumFreeBytes = MinimumFreeBytes, maximumStageSeconds = 7200 },
                }, JsonOptions);
                await File.WriteAllBytesAsync(manifest, bytes, cancellationToken).ConfigureAwait(false);
            }
            var now = File.GetCreationTimeUtc(manifest);
            return new(projectId, root,
                Path.Combine(root, "inputs"), Path.Combine(root, "work"), Path.Combine(root, "artifacts"),
                Path.Combine(root, "notebooks"), Path.Combine(root, "manuscripts"), Path.Combine(root, "env"),
                Path.Combine(root, "runs"), Path.Combine(root, "snapshots"), new DateTimeOffset(now, TimeSpan.Zero),
                "ready; execution is network-isolated; GPU passthrough is disabled until verified");
        }
        finally { _gate.Release(); }
    }

    public async Task<ResearchSandboxRuntimeStatus> PrepareRuntimeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var daemon = await RunDockerCaptureAsync(["info", "--format", "{{.ServerVersion}}"], cancellationToken).ConfigureAwait(false);
            if (daemon.ExitCode != 0) return new(false, "docker-unavailable", daemon.StandardError.Trim());
            await EnsureRunnerImageAsync(cancellationToken).ConfigureAwait(false);
            return new(true, "ready-network-isolated", "Docker runner ready; container egress is disabled and GPU passthrough is not enabled.");
        }
        catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
                                          && !cancellationToken.IsCancellationRequested)
        {
            return new(false, "runner-unavailable", exception.Message);
        }
    }

    public async Task<ResearchSandboxFileChange> WriteTextAsync(
        string projectId,
        string relativePath,
        string content,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var layout = await EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        using var projectMutation = await AcquireProjectExecutionAsync(layout, cancellationToken).ConfigureAwait(false);
        var parentRelative = Path.GetDirectoryName(relativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(parentRelative)) CreateSafeWorkDirectory(layout.WorkPath, parentRelative);
        var destination = ResolveWorkFile(layout.WorkPath, relativePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var exists = File.Exists(destination);
            var beforeHash = exists ? await FileHashAsync(destination, cancellationToken).ConfigureAwait(false) : null;
            if (expectedSha256 is not null && !string.Equals(expectedSha256, beforeHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Die Sandboxdatei wurde seit dem erwarteten Stand geändert; es wurde nichts überschrieben.");
            var changeSetId = Guid.NewGuid().ToString("N");
            var contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
            var temp = destination + "." + changeSetId + ".tmp";
            await File.WriteAllBytesAsync(temp, contentBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
            var afterHash = Hash(contentBytes);
            var change = new ResearchSandboxFileChange(changeSetId, relativePath.Replace('\\', '/'), beforeHash, afterHash, !exists);
            await File.WriteAllTextAsync(Path.Combine(layout.RunsPath, "edit-" + changeSetId + ".json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 2, storage = "current-project-tree", projectId, change.ChangeSetId, change.RelativePath,
                change.BeforeSha256, change.AfterSha256, change.IsNewFile, createdAt = DateTimeOffset.UtcNow,
            }, JsonOptions), cancellationToken).ConfigureAwait(false);
            return change;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreChangeSetAsync(string projectId, string changeSetId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ValidateProjectId(changeSetId);
        var layout = await EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var snapshotDirectory = Path.Combine(layout.SnapshotsPath, changeSetId);
        var manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            if (File.Exists(Path.Combine(layout.RunsPath, "edit-" + changeSetId + ".json")))
                throw new InvalidOperationException("Das Projekt verwendet einen aktuellen Arbeitsbaum ohne Dateirevisionen. Dieser Änderungsbeleg enthält nur Hashes und keine rücksetzbare Dateikopie.");
            throw new FileNotFoundException("Das Änderungspaket wurde nicht gefunden.", manifestPath);
        }
        using var projectMutation = await AcquireProjectExecutionAsync(layout, cancellationToken).ConfigureAwait(false);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        var root = manifest.RootElement;
        if (!string.Equals(root.GetProperty("projectId").GetString(), projectId, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("changeSetId").GetString(), changeSetId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Das Änderungspaket gehört zu einem anderen Forschungsvorhaben.");
        var relativePath = root.GetProperty("relativePath").GetString()!;
        var target = ResolveWorkFile(layout.WorkPath, relativePath);
        var expectedAfter = root.GetProperty("afterSha256").GetString()!;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(target) || !string.Equals(Hash(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false)), expectedAfter, StringComparison.OrdinalIgnoreCase))
            {
                var recovery = Path.Combine(layout.SnapshotsPath, "recovery-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(recovery);
                if (File.Exists(target)) File.Copy(target, Path.Combine(recovery, "external-current.bin"));
                if (File.Exists(Path.Combine(snapshotDirectory, "before.bin")))
                    File.Copy(Path.Combine(snapshotDirectory, "before.bin"), Path.Combine(recovery, "expected-before.bin"));
                await File.WriteAllTextAsync(Path.Combine(recovery, "conflict.json"), JsonSerializer.Serialize(new { projectId, changeSetId, relativePath, expectedAfter, detectedAt = DateTimeOffset.UtcNow }), cancellationToken).ConfigureAwait(false);
                throw new IOException("Die Datei wurde nach der AI-Änderung manuell verändert. Das Tool hat nichts überschrieben und ein Recovery-Bundle erstellt.");
            }
            if (root.GetProperty("isNewFile").GetBoolean()) File.Delete(target);
            else
            {
                var before = await File.ReadAllBytesAsync(Path.Combine(snapshotDirectory, "before.bin"), cancellationToken).ConfigureAwait(false);
                var temp = target + ".restore.tmp";
                await File.WriteAllBytesAsync(temp, before, cancellationToken).ConfigureAwait(false);
                File.Move(temp, target, overwrite: true);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<ResearchSandboxRunResult> RunPythonAsync(
        string projectId,
        string relativeScriptPath,
        IReadOnlyList<string>? arguments = null,
        int timeoutSeconds = 7200,
        string? relativeWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var layout = await EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (timeoutSeconds is < 1 or > 7200) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Eine Rechenetappe darf höchstens zwei Stunden laufen.");
        var script = ResolveWorkFile(layout.WorkPath, relativeScriptPath);
        if (!File.Exists(script)) throw new FileNotFoundException("Das Python-Skript liegt nicht im Forschungsbereich work.", script);
        var workingDirectory = ResolveWorkDirectory(layout.WorkPath, relativeWorkingDirectory);
        await ValidateExecutionCapacityAsync(layout, MaximumProjectBytes, cancellationToken).ConfigureAwait(false);
        await EnsureRunnerImageAsync(cancellationToken).ConfigureAwait(false);

        var runId = Guid.NewGuid().ToString("N");
        var containerName = "missum-research-" + runId;
        ResearchExecutionSnapshot snapshot;
        using var projectExecution = await AcquireProjectExecutionAsync(layout, cancellationToken).ConfigureAwait(false);
        snapshot = await CaptureExecutionSnapshotAsync(layout, runId, script, token: cancellationToken).ConfigureAwait(false);
        var scriptContainerPath = "/sandbox/work/" + Path.GetRelativePath(layout.WorkPath, script).Replace('\\', '/');
        var startInfo = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        Add(startInfo, "run"); Add(startInfo, "--rm"); Add(startInfo, "--init"); Add(startInfo, "--name"); Add(startInfo, containerName);
        Add(startInfo, "--label"); Add(startInfo, "com.missum.runtime.profile=" + profile.Name);
        Add(startInfo, "--label"); Add(startInfo, "com.missum.runtime.stack-data-root=" + Path.GetFullPath(profile.StackDataRoot));
        Add(startInfo, "--network"); Add(startInfo, "none"); Add(startInfo, "--read-only"); Add(startInfo, "--cap-drop"); Add(startInfo, "ALL");
        Add(startInfo, "--security-opt"); Add(startInfo, "no-new-privileges:true"); Add(startInfo, "--pids-limit"); Add(startInfo, "256");
        Add(startInfo, "--cpus"); Add(startInfo, "16"); Add(startInfo, "--memory"); Add(startInfo, "48g"); Add(startInfo, "--memory-swap"); Add(startInfo, "48g");
        Add(startInfo, "--user"); Add(startInfo, "10001:10001"); Add(startInfo, "--tmpfs"); Add(startInfo, "/tmp:rw,noexec,nosuid,nodev,size=536870912");
        // There is one authoritative project tree. Its source/data hashes are
        // measured before and after execution; application writes are serialized.
        AddMount(startInfo, layout.InputsPath, "/sandbox/inputs", readOnly: true);
        AddMount(startInfo, snapshot.ExecutionWorkPath, "/sandbox/work", readOnly: false);
        AddMount(startInfo, layout.ArtifactsPath, "/sandbox/artifacts", readOnly: false);
        AddMount(startInfo, layout.NotebooksPath, "/sandbox/notebooks", readOnly: false);
        AddMount(startInfo, layout.ManuscriptsPath, "/sandbox/manuscripts", readOnly: true);
        AddMount(startInfo, layout.EnvironmentPath, "/sandbox/env", readOnly: true);
        Add(startInfo, "--workdir");
        var relativeContainerWorkingDirectory = Path.GetRelativePath(layout.WorkPath, workingDirectory).Replace('\\', '/');
        Add(startInfo, "/sandbox/work" + (relativeContainerWorkingDirectory == "." ? string.Empty : "/" + relativeContainerWorkingDirectory));
        Add(startInfo, RunnerImage);
        foreach (var argument in CreatePythonInvocation(scriptContainerPath)) Add(startInfo, argument);
        foreach (var argument in arguments ?? [])
        {
            if (argument.Length > 4000 || argument.Any(char.IsControl)) throw new ArgumentException("Ein Python-Argument ist ungültig.", nameof(arguments));
            Add(startInfo, argument);
        }
        snapshot = snapshot with { Arguments = (arguments ?? []).ToArray(), WorkingDirectory = relativeContainerWorkingDirectory };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var started = DateTimeOffset.UtcNow;
        await WriteExecutionStatusAsync(layout, runId, snapshot, "prepared", started, null, null, null).ConfigureAwait(false);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Docker konnte den isolierten Forschungscontainer nicht starten.");
        }
        catch (Exception exception)
        {
            await WriteExecutionStatusAsync(layout, runId, snapshot, "failed_to_start", started, DateTimeOffset.UtcNow, null, exception.Message).ConfigureAwait(false);
            throw;
        }
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters, preserveTail: true);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaximumOutputCharacters);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var capacityMonitoring = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var capacityViolation = WaitForCapacityViolationAsync(layout, token: capacityMonitoring.Token);
        var timedOut = false;
        string? resourceFailure = null;
        try
        {
            var exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(exited, capacityViolation).ConfigureAwait(false) == capacityViolation)
            {
                resourceFailure = await capacityViolation.ConfigureAwait(false);
                await KillContainerAsync(containerName).ConfigureAwait(false);
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            }
            else await exited.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            await KillContainerAsync(containerName).ConfigureAwait(false);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
        catch (OperationCanceledException)
        {
            await KillContainerAsync(containerName).ConfigureAwait(false);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await WriteExecutionStatusAsync(layout, runId, snapshot, "cancelled", started, DateTimeOffset.UtcNow, null, "Die Ausführung wurde abgebrochen; kein erfolgreicher Ausführungsbeleg.").ConfigureAwait(false);
            throw;
        }
        finally
        {
            await capacityMonitoring.CancelAsync().ConfigureAwait(false);
            try { await capacityViolation.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (resourceFailure is not null) stderr += "\n" + resourceFailure;
        var completed = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<string, string> outputHashes;
        try
        {
            var parsed = ParseExecutionReadTrace(stdout);
            stdout = parsed.StandardOutput;
            if (parsed.Trace is not null) snapshot = BindExecutionReadTrace(snapshot, parsed.Trace);
            outputHashes = await CompleteExecutionSnapshotAsync(layout, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await WriteExecutionStatusAsync(layout, runId, snapshot, "invalid_provenance", started, completed,
                resourceFailure is not null ? 137 : timedOut ? 124 : process.ExitCode, exception.Message, stdout, stderr).ConfigureAwait(false);
            throw;
        }
        var result = new ResearchSandboxRunResult(runId, resourceFailure is not null ? 137 : timedOut ? 124 : process.ExitCode, timedOut, stdout, stderr, started, completed,
            $"docker run --workdir /sandbox/work{(snapshot.WorkingDirectory == "." ? string.Empty : "/" + snapshot.WorkingDirectory)} {RunnerImage} python3 -I -B -X pycache_prefix=/tmp/missum-bytecode -c [missum-sandbox-launcher-v1] {JsonSerializer.Serialize(new[] { scriptContainerPath }.Concat(snapshot.Arguments ?? []), JsonOptions)}", snapshot.ExecutedScriptPath,
            snapshot.ScriptSha256, snapshot.SnapshotId, snapshot.InputHashes, outputHashes);
        var runRecord = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 3, projectId, runId, script = snapshot.ExecutedScriptPath,
            phase = resourceFailure is not null ? "resource_failed" : timedOut ? "timed_out" : process.ExitCode == 0 ? "completed" : "failed",
            result.ExecutedScriptPath, result.ScriptSha256, result.SnapshotId, result.InputHashes, result.OutputHashes,
            startedAt = started, completedAt = completed, result.ExitCode, result.TimedOut,
            result.Command, environment = RunnerImage, networkIsolation = "docker --network none",
            arguments = snapshot.Arguments, workingDirectory = snapshot.WorkingDirectory,
            provenance = new { version = 1, mode = "current-project-tree", sourceHashes = snapshot.SourceHashes,
                inputsSha256Before = snapshot.InputHashes, outputsSha256After = outputHashes },
            launcher = new { version = 1, sha256 = Hash(System.Text.Encoding.UTF8.GetBytes(PythonLauncher)),
                projectImportRoots = new[] { "/sandbox/work", Path.GetDirectoryName(scriptContainerPath)?.Replace('\\', '/') } },
            resourceLimits = new { cpuCores = 16, memory = "48g", pids = 256, timeoutSeconds }, result.StandardOutput, result.StandardError,
        }, JsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(layout.RunsPath, runId + ".json"), runRecord, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public async Task<string> ArchiveProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        var storage = Path.GetFullPath(await ResolveStorageRootAsync(projectId, cancellationToken).ConfigureAwait(false));
        using var projectMutation = await AcquireProjectExecutionAsync(Path.Combine(storage, projectId), cancellationToken).ConfigureAwait(false);
        var trash = Path.Combine(storage, "Trash");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = Path.Combine(storage, projectId);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Das Forschungsprojekt wurde nicht gefunden.");
            Directory.CreateDirectory(trash);
            var archiveId = projectId + "_" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            var destination = Path.Combine(trash, archiveId);
            await File.WriteAllTextAsync(Path.Combine(source, "archive.json"), JsonSerializer.Serialize(new { projectId, archivedAt = DateTimeOffset.UtcNow, purgeAfter = DateTimeOffset.UtcNow.AddDays(30) }), cancellationToken).ConfigureAwait(false);
            EnsureBelowRoot(source, storage); EnsureBelowRoot(destination, storage);
            RejectReparsePoint(source);
            Directory.Move(source, destination);
            return destination;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        var storage = Path.GetFullPath(await ResolveStorageRootAsync(projectId, cancellationToken).ConfigureAwait(false));
        using var projectMutation = await AcquireProjectExecutionAsync(Path.Combine(storage, projectId), cancellationToken).ConfigureAwait(false);
        var trash = Path.Combine(storage, "Trash");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = Path.Combine(storage, projectId);
            if (Directory.Exists(destination)) throw new IOException("Das Forschungsprojekt ist bereits aktiv.");
            if (!Directory.Exists(trash)) throw new DirectoryNotFoundException("Es gibt keinen Wiederherstellungsbereich.");
            var matches = Directory.GetDirectories(trash, projectId + "_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
            var source = matches.FirstOrDefault() ?? throw new DirectoryNotFoundException("Es wurde kein archiviertes Forschungsprojekt gefunden.");
            EnsureBelowRoot(source, storage); EnsureBelowRoot(destination, storage);
            RejectReparsePoint(source);
            Directory.Move(source, destination);
            var metadata = Path.Combine(destination, "archive.json");
            if (File.Exists(metadata)) File.Delete(metadata);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Trash)) return 0;
        var removed = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var directory in Directory.GetDirectories(Trash, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadataPath = Path.Combine(directory, "archive.json");
                if (!File.Exists(metadataPath)) continue;
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
                    if (!document.RootElement.TryGetProperty("purgeAfter", out var value) || !value.TryGetDateTimeOffset(out var purgeAfter) || purgeAfter > DateTimeOffset.UtcNow) continue;
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (JsonException) { /* Leave corrupt recovery entries untouched. */ }
            }
        }
        finally { _gate.Release(); }
        return removed;
    }

    private static async Task EnsureRunnerImageAsync(CancellationToken cancellationToken)
    {
        var image = await RunDockerCaptureAsync(["image", "inspect", RunnerImage], cancellationToken).ConfigureAwait(false);
        if (image.ExitCode == 0) return;
        var context = Path.Combine(AppContext.BaseDirectory, "research-runner");
        if (!File.Exists(Path.Combine(context, "Dockerfile")))
            throw new InvalidOperationException("Der isolierte Research-Runner fehlt in der Installation; es wird kein Host-Python als Ersatz gestartet.");
        var build = await RunDockerCaptureAsync(["build", "--tag", RunnerImage, context], cancellationToken).ConfigureAwait(false);
        if (build.ExitCode != 0) throw new InvalidOperationException("Der isolierte Research-Runner konnte nicht gebaut werden: " + build.StandardError);
    }

    private static async Task KillContainerAsync(string containerName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { _ = await RunDockerCaptureAsync(["rm", "--force", containerName], timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or OperationCanceledException) { }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunDockerCaptureAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) Add(start, argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Docker CLI konnte nicht gestartet werden.");
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumOutputCharacters);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, bool preserveTail = false)
    {
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder(Math.Min(maximumCharacters, 8192));
        var tail = new System.Text.StringBuilder();
        var tailBudget = preserveTail ? maximumCharacters / 4 : 0;
        var prefixBudget = maximumCharacters - tailBudget;
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            var append = Math.Min(count, prefixBudget - builder.Length);
            if (append > 0) builder.Append(buffer, 0, append);
            if (append < count)
            {
                if (preserveTail)
                {
                    tail.Append(buffer, append, count - append);
                    if (tail.Length > tailBudget)
                    {
                        tail.Remove(0, tail.Length - tailBudget);
                        truncated = true;
                    }
                }
                else truncated = true;
            }
        }
        if (truncated) builder.Append("\n[Ausgabe gekürzt]");
        if (tail.Length > 0) builder.Append(tail);
        return builder.ToString();
    }

    private static async Task<long> DirectoryBytesAsync(string root, CancellationToken token)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectReparsePoint(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Verknüpfungen sind innerhalb der Sandboxkontingentprüfung unzulässig.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else
                {
                    total = checked(total + new FileInfo(entry).Length);
                    if (total > MaximumProjectBytes) return total;
                }
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return total;
    }

    internal sealed record ResearchExecutionSnapshot(string SnapshotId, string ExecutionWorkPath,
        string ExecutedScriptPath, string ScriptSha256, IReadOnlyDictionary<string, string> InputHashes,
        IReadOnlyDictionary<string, string> SourceHashes, IReadOnlyDictionary<string, string> BeforeOutputHashes,
        IReadOnlyDictionary<string, DateTime> BeforeOutputTimes, bool ReadsMeasured = false,
        IReadOnlyList<string>? Arguments = null, string? WorkingDirectory = null,
        IReadOnlyDictionary<string, string>? BaselineInputHashes = null);

    internal static async Task<ResearchExecutionSnapshot> CaptureExecutionSnapshotAsync(ResearchSandboxLayout layout,
        string runId, string script, long maximumBytes = MaximumProjectBytes, CancellationToken token = default)
    {
        ValidateProjectId(runId);
        EnsureBelowRoot(script, layout.WorkPath);
        await ValidateExecutionCapacityAsync(layout, maximumBytes, token).ConfigureAwait(false);
        EnsureFreeSpace(layout.RootPath);
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sourceHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var beforeTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var name in ExecutionInputRoots)
        {
            foreach (var pair in await HashTreeAsync(Path.Combine(layout.RootPath, name), name, token).ConfigureAwait(false))
            {
                inputs.Add(pair.Key, pair.Value);
                if (IsExecutionSource(pair.Key)) sourceHashes.Add(pair.Key, pair.Value);
                if (pair.Key.StartsWith("work/", StringComparison.Ordinal))
                    beforeTimes.Add(pair.Key, File.GetLastWriteTimeUtc(Path.Combine(layout.RootPath, pair.Key)));
            }
            if (inputs.Count > 4096) throw new IOException("Der vollständige Ausführungsbeleg besitzt mehr als 4096 Eingabedateien.");
        }
        ValidateHashBudget(inputs);
        var before = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ExecutionOutputRoots)
            foreach (var pair in await HashTreeAsync(Path.Combine(layout.RootPath, name), name, token).ConfigureAwait(false))
            {
                before.Add(pair.Key, pair.Value);
                beforeTimes.Add(pair.Key, File.GetLastWriteTimeUtc(Path.Combine(layout.RootPath, pair.Key)));
            }
        var scriptPath = "work/" + Path.GetRelativePath(layout.WorkPath, script).Replace('\\', '/');
        sourceHashes[scriptPath] = inputs[scriptPath];
        return new("current-" + runId, layout.WorkPath, scriptPath, inputs[scriptPath], inputs, sourceHashes, before, beforeTimes);
    }

    internal static async Task<IReadOnlyDictionary<string, string>> CompleteExecutionSnapshotAsync(ResearchSandboxLayout layout,
        ResearchExecutionSnapshot snapshot, CancellationToken token = default)
    {
        var after = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ExecutionInputRoots)
            foreach (var pair in await HashTreeAsync(Path.Combine(layout.RootPath, name), name, token).ConfigureAwait(false))
                after.Add(pair.Key, pair.Value);
        foreach (var pair in snapshot.SourceHashes)
        {
            if (!after.TryGetValue(pair.Key, out var hash) && File.Exists(Path.Combine(layout.RootPath, pair.Key)))
                hash = await FileHashAsync(Path.Combine(layout.RootPath, pair.Key), token).ConfigureAwait(false);
            if (hash != pair.Value)
                throw new IOException("Eine ausgeführte Quelldatei wurde während der Python-Ausführung geändert. "
                    + "Es wurde kein erfolgreicher Ausführungsbeleg erstellt: " + pair.Key);
        }
        foreach (var pair in after)
            if (!snapshot.ReadsMeasured && IsExecutionSource(pair.Key) && !snapshot.SourceHashes.ContainsKey(pair.Key))
                throw new IOException("Während der Python-Ausführung wurde eine neue ausführbare Quelldatei angelegt. "
                    + "Ihr Ausgangsstand ist nicht belegt; es wurde kein erfolgreicher Ausführungsbeleg erstellt: " + pair.Key);
        foreach (var pair in snapshot.InputHashes.Where(pair => IsReadOnlyInput(pair.Key)))
            if (!after.TryGetValue(pair.Key, out var hash) || hash != pair.Value)
                throw new IOException("Eine schreibgeschützte Eingabedatei wurde während der Python-Ausführung geändert: " + pair.Key);
        foreach (var pair in after.Where(pair => IsReadOnlyInput(pair.Key)))
            if (!snapshot.ReadsMeasured && !snapshot.InputHashes.ContainsKey(pair.Key))
                throw new IOException("Während der Python-Ausführung wurde eine neue schreibgeschützte Eingabedatei angelegt: " + pair.Key);
        var changed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in after.Where(pair => pair.Key.StartsWith("work/", StringComparison.Ordinal)))
        {
            (snapshot.BaselineInputHashes ?? snapshot.InputHashes).TryGetValue(pair.Key, out var before);
            if (pair.Value == before && snapshot.BeforeOutputTimes.TryGetValue(pair.Key, out var beforeTime)
                && File.GetLastWriteTimeUtc(Path.Combine(layout.RootPath, pair.Key)) == beforeTime) continue;
            changed.Add(pair.Key, pair.Value);
        }
        foreach (var name in ExecutionOutputRoots)
            foreach (var pair in await HashTreeAsync(Path.Combine(layout.RootPath, name), name, token).ConfigureAwait(false))
                if (!snapshot.BeforeOutputHashes.TryGetValue(pair.Key, out var before) || pair.Value != before
                    || !snapshot.BeforeOutputTimes.TryGetValue(pair.Key, out var previousTime)
                    || File.GetLastWriteTimeUtc(Path.Combine(layout.RootPath, pair.Key)) != previousTime)
                    changed.Add(pair.Key, pair.Value);
        if (changed.Count > 4096) throw new IOException("Der vollständige Ausführungsbeleg besitzt mehr als 4096 Ergebnisdateien.");
        ValidateHashBudget(changed);
        await ValidateExecutionCapacityAsync(layout, MaximumProjectBytes, token).ConfigureAwait(false);
        return changed;
    }

    private static async Task<SortedDictionary<string, string>> HashTreeAsync(string root, string prefix, CancellationToken token)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectReparsePoint(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                RejectReparsePoint(path);
                var relative = Path.GetRelativePath(root, path);
                if (Directory.Exists(path))
                {
                    pending.Push(path); continue;
                }
                hashes.Add(prefix + "/" + relative.Replace('\\', '/'), await FileHashAsync(path, token).ConfigureAwait(false));
                if (hashes.Count > 4096) throw new IOException("Ein vollständiger Ausführungsstand darf höchstens 4096 Dateien enthalten.");
            }
        }
        return hashes;
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static void ValidateHashBudget(SortedDictionary<string, string> hashes)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(hashes, JsonOptions).Length > 512 * 1024)
            throw new IOException("Der vollständige Dateihashbeleg überschreitet das 512-KiB-Metadatenlimit.");
    }

    private static string ResolveWorkFile(string workRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Any(char.IsControl))
            throw new UnauthorizedAccessException("Der Skriptpfad muss relativ zum Sandbox-Arbeitsverzeichnis sein.");
        var root = Path.GetFullPath(workRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(workRoot, relativePath));
        EnsureBelowRoot(full, root);
        var relativeParent = Path.GetRelativePath(workRoot, Path.GetDirectoryName(full)!);
        var currentPath = Path.GetFullPath(workRoot);
        RejectReparsePoint(currentPath);
        if (relativeParent != ".")
        {
            foreach (var segment in relativeParent.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, segment);
                if (Directory.Exists(currentPath)) RejectReparsePoint(currentPath);
                else break;
            }
        }
        if (File.Exists(full)) RejectReparsePoint(full);
        return full;
    }

    private static string ResolveWorkDirectory(string workRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".") return Path.GetFullPath(workRoot);
        return CreateSafeWorkDirectory(workRoot, relativePath);
    }

    private static string CreateSafeWorkDirectory(string workRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Any(char.IsControl))
            throw new UnauthorizedAccessException("Der Arbeitsordner muss relativ zu work sein.");
        var root = Path.GetFullPath(workRoot);
        RejectReparsePoint(root);
        var current = root;
        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") throw new UnauthorizedAccessException("Punktsegmente sind in Sandboxpfaden unzulässig.");
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) RejectReparsePoint(current);
            else
            {
                Directory.CreateDirectory(current);
                RejectReparsePoint(current);
            }
            EnsureBelowRoot(current, root);
        }
        return current;
    }

    private static void AddMount(ProcessStartInfo start, string source, string target, bool readOnly)
    {
        RejectReparsePoint(source);
        Add(start, "--mount");
        Add(start, $"type=bind,source={Path.GetFullPath(source)},target={target}{(readOnly ? ",readonly" : string.Empty)}");
    }

    private static void Add(ProcessStartInfo start, string argument) => start.ArgumentList.Add(argument);
    public void Dispose() => _gate.Dispose();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void ValidateProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !SafeId.IsMatch(projectId) || projectId is "." or "..")
            throw new ArgumentException("Die Forschungsprojektkennung ist ungültig.", nameof(projectId));
    }

    private static void EnsureBelowRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der Sandboxpfad verlässt den Forschungsbereich.");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Verknüpfungen sind innerhalb der Forschungs-Sandboxgrenze nicht zulässig.");
    }
}
