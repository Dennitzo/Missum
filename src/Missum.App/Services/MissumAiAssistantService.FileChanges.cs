using Missum.Core.Coding;
using Missum.Core.Models;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Missum.App.Services;

public sealed record CodingChangesSummary(
    Guid SessionId,
    Guid MessageId,
    Guid RunId,
    string WorkspacePath,
    long Revision,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CodingWorkspaceFileChange> Files,
    bool IsPartial,
    string? Notice,
    string? ComparisonKind = null);

public sealed partial class MissumAiAssistantService
{
    private CodingChangesMonitor? _activeFileChanges;

    private async Task StartFileChangesAsync(MissumAiRunRecord run, ChatMessage message, bool resume,
        Func<MissumAiAssistantUpdate, Task> update, CancellationToken cancellationToken)
    {
        if (run.WorkspacePath is not { } workspace) return;
        if (!UsesCodingAgent(run.Action)
            && (await chats.GetSessionAsync(run.SessionId, cancellationToken).ConfigureAwait(false))?.ChatMode != ChatMode.ClaudeScience) return;
        var directory = CodingChangesMonitor.StorageDirectory(settings.DataDirectory, run.SessionId, run.AssistantMessageId);
        var monitor = new CodingChangesMonitor(workspace, directory, run.SessionId, run.AssistantMessageId, run.Id,
            summary => update(new(MissumAiAssistantUpdateKind.FileChangesChanged, message, ChangesSummary: summary)),
            exception => RunDiagnostic(logger, run.Id.ToString("D"), "file change overview unavailable", exception));
        _activeFileChanges = monitor;
        await monitor.StartAsync(resume, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishFileChangesAsync()
    {
        var monitor = Interlocked.Exchange(ref _activeFileChanges, null);
        if (monitor is not null) await monitor.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task<CodingChangesSummary?> GetChangesSummaryAsync(Guid sessionId, IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var message = messages.LastOrDefault(static message => message.Role == ChatRole.Assistant);
        if (message is null) return null;
        var summary = await CodingChangesMonitor.ReadLatestAsync(
            CodingChangesMonitor.StorageDirectory(settings.DataDirectory, sessionId, message.Id),
            cancellationToken).ConfigureAwait(false);
        if (summary is null || summary.ComparisonKind != "git-v1" || summary.SessionId != sessionId || summary.MessageId != message.Id) return null;
        if (_activeFileChanges?.Owns(sessionId, message.Id) != true)
            summary = await CodingChangesMonitor.RecoverIndexLockAsync(
                CodingChangesMonitor.StorageDirectory(settings.DataDirectory, sessionId, message.Id), summary, cancellationToken).ConfigureAwait(false);
        return summary.ComparisonKind == "git-v1" && summary.SessionId == sessionId && summary.MessageId == message.Id ? summary : null;
    }
}

/// <summary>A single run's persistent baseline and independently cancellable UI refresh loop.</summary>
internal sealed class CodingChangesMonitor : IAsyncDisposable
{
    private const string LatestFileName = "latest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublicationGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly CodingGitChangeTracker? _tracker;
    private readonly Exception? _initializationError;
    private readonly string _directory;
    private readonly Guid _sessionId;
    private readonly Guid _messageId;
    private readonly Guid _runId;
    private readonly string _workspace;
    private readonly Func<CodingChangesSummary, Task> _publish;
    private readonly Action<Exception> _diagnostic;
    private readonly Action? _replacementBlocked;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenRegistration _runCancellation;
    private Task _loop = Task.CompletedTask;
    private CodingChangesSummary? _latest;
    private bool _canRefresh;
    private int _disposed;

    internal CodingChangesMonitor(string workspace, string directory, Guid sessionId, Guid messageId, Guid runId,
        Func<CodingChangesSummary, Task> publish, Action<Exception> diagnostic, Action? replacementBlocked = null)
    {
        _workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        _directory = directory;
        _sessionId = sessionId;
        _messageId = messageId;
        _runId = runId;
        _publish = publish;
        _diagnostic = diagnostic;
        _replacementBlocked = replacementBlocked;
        try { _tracker = new CodingGitChangeTracker(_workspace, directory); }
        catch (Exception exception) when (IsStorageError(exception)) { _initializationError = exception; }
    }

    internal static string StorageDirectory(string dataDirectory, Guid sessionId, Guid messageId) =>
        Path.Combine(dataDirectory, "Cache", "CodingChanges", sessionId.ToString("N"), messageId.ToString("N"));

    internal bool Owns(Guid sessionId, Guid messageId) => _sessionId == sessionId && _messageId == messageId && Volatile.Read(ref _disposed) == 0;

    internal static async Task<CodingChangesSummary> RecoverIndexLockAsync(string directory, CodingChangesSummary expected,
        CancellationToken cancellationToken = default)
    {
        if (!IsIndexLockFailure(expected)) return expected;
        var gate = PublicationGates.GetOrAdd(Path.GetFullPath(directory), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadLatestAsync(directory, cancellationToken).ConfigureAwait(false);
            if (current is null || current.SessionId != expected.SessionId || current.MessageId != expected.MessageId
                || current.RunId != expected.RunId || current.Revision != expected.Revision) return current ?? expected;
            if (!IsIndexLockFailure(current)) return current;
            if (!File.Exists(Path.Combine(directory, "git-baseline.json"))) return current;
            // LoadExisting requires the original manifest and validates its Git
            // tree. This repair never captures a replacement baseline or deletes
            // the repository's old index.lock; captures use a private index.
            using var tracker = new CodingGitChangeTracker(current.WorkspacePath, directory);
            var snapshot = await tracker.LoadExistingAsync(cancellationToken).ConfigureAwait(false);
            var repaired = current with
            {
                Files = snapshot.Files, IsPartial = snapshot.IsPartial, Notice = snapshot.Notice,
                UpdatedAt = snapshot.UpdatedAt,
                Revision = Math.Max(current.Revision + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            };
            await WriteLatestAsync(directory, repaired, cancellationToken).ConfigureAwait(false);
            return repaired;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsStorageError(exception)) { return expected; }
        finally { gate.Release(); }

        static bool IsIndexLockFailure(CodingChangesSummary summary) => summary.ComparisonKind == "git-v1" && summary.IsPartial
            && summary.Notice?.Contains("index.lock", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static Task DeleteSessionStorageAsync(string dataDirectory, Guid sessionId) => Task.Run(() =>
    {
        var cacheRoot = Path.GetFullPath(Path.Combine(dataDirectory, "Cache", "CodingChanges"));
        var target = Path.GetFullPath(Path.Combine(cacheRoot, sessionId.ToString("N")));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(target), cacheRoot, comparison))
            throw new IOException("Der Sitzungscache liegt außerhalb des vorgesehenen Verzeichnisses.");
        if (!Directory.Exists(target)) return;
        // Refuse redirected roots and entries. Only the exact GUID-owned cache
        // is removed, after the repository has confirmed the session is gone.
        for (var ancestor = new DirectoryInfo(target); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Ein verknüpfter Sitzungscache wird nicht automatisch gelöscht.");
        var pending = new Stack<string>();
        var files = new List<string>();
        pending.Push(target);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Ein verknüpfter Eintrag im Sitzungscache wird nicht automatisch gelöscht.");
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
                else files.Add(entry);
            }
        }
        // Git object files are read-only on Windows. All paths were validated above.
        foreach (var file in files) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(target, recursive: true);
    });

    internal async Task StartAsync(bool resume, CancellationToken cancellationToken)
    {
        _latest = await ReadLatestAsync(_directory, cancellationToken).ConfigureAwait(false);
        if (_latest is not null && (_latest.ComparisonKind != "git-v1" || _latest.SessionId != _sessionId || _latest.MessageId != _messageId)) _latest = null;
        // Complete the durable initial baseline before the caller creates the server
        // run or replays pending tool proposals. Resume must never invent a baseline.
        try
        {
            if (_initializationError is not null) throw _initializationError;
            var snapshot = await Task.Run(() => resume
                ? _tracker!.LoadExistingAsync(cancellationToken)
                : _tracker!.InitializeAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            _canRefresh = true;
            await PublishAsync(snapshot, force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            _diagnostic(exception);
            await PublishUnavailableAsync("Die Änderungsübersicht ist nicht verfügbar, weil der ursprüngliche Dateistand nicht gespeichert oder geladen werden konnte: " + exception.Message,
                cancellationToken).ConfigureAwait(false);
            return; // No periodic retries against an uninitialized tracker.
        }
        _runCancellation = cancellationToken.Register(static state => ((CodingChangesMonitor)state!).Cancel(), this);
        _loop = PollAsync(_stop.Token);
    }

    internal void Cancel()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _diagnostic(exception); }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_canRefresh) return;
        try
        {
            await PublishAsync(await Task.Run(() => _tracker!.RefreshAsync(cancellationToken), cancellationToken).ConfigureAwait(false),
                force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _diagnostic(exception);
            await PublishUnavailableAsync("Die Dateiänderungen konnten nicht vollständig aktualisiert werden: " + exception.Message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(CodingWorkspaceChangesSnapshot snapshot, bool force, CancellationToken cancellationToken)
    {
        if (!force && _latest is { } previous && previous.UpdatedAt == snapshot.UpdatedAt
            && previous.IsPartial == snapshot.IsPartial && previous.Notice == snapshot.Notice) return;
        var revision = Math.Max((_latest?.Revision ?? 0) + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var summary = new CodingChangesSummary(_sessionId, _messageId, _runId, _workspace, revision,
            snapshot.UpdatedAt, snapshot.Files, snapshot.IsPartial, snapshot.Notice, "git-v1");
        await StoreAndPublishAsync(summary, cancellationToken).ConfigureAwait(false);
    }

    private Task PublishUnavailableAsync(string notice, CancellationToken cancellationToken)
    {
        var summary = new CodingChangesSummary(_sessionId, _messageId, _runId, _workspace,
            Math.Max((_latest?.Revision ?? 0) + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            DateTimeOffset.UtcNow, _latest?.Files ?? [], true, notice, "git-v1");
        return StoreAndPublishAsync(summary, cancellationToken);
    }

    private async Task StoreAndPublishAsync(CodingChangesSummary summary, CancellationToken cancellationToken)
    {
        var gate = PublicationGates.GetOrAdd(Path.GetFullPath(_directory), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteLatestAsync(_directory, summary, cancellationToken, _replacementBlocked).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _diagnostic(exception);
            summary = summary with { IsPartial = true, Notice = "Die Änderungsübersicht konnte nicht dauerhaft gespeichert werden: " + exception.Message };
        }
        finally { gate.Release(); }
        _latest = summary;
        try { await _publish(summary).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _diagnostic(exception); }
    }

    private static async Task WriteLatestAsync(string directory, CodingChangesSummary summary, CancellationToken cancellationToken,
        Action? replacementBlocked = null)
    {
        var temporary = Path.Combine(directory, $".latest-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(summary, JsonOptions), cancellationToken).ConfigureAwait(false);
            var replacement = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Move(temporary, Path.Combine(directory, LatestFileName), overwrite: true); return; }
                catch (Exception exception) when (OperatingSystem.IsWindows()
                    && (exception is IOException or UnauthorizedAccessException)
                    && (exception.HResult & 0xffff) is 5 or 32 or 33
                    && replacement.Elapsed < TimeSpan.FromSeconds(5))
                {
                    replacementBlocked?.Invoke();
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task<CodingChangesSummary?> ReadLatestAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, LatestFileName);
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var summary = await JsonSerializer.DeserializeAsync<CodingChangesSummary>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return summary is not null && summary.Files is not null && Path.IsPathFullyQualified(summary.WorkspacePath)
                && summary.Files.All(static file => file is not null && file.Path is not null && file.Diff is not null)
                ? summary : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool IsStorageError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        await _runCancellation.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
            // An explicit stop still records already completed file writes. This
            // scan has its own bounded cancellation rather than the cancelled run.
            using var finalScan = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await RefreshAsync(finalScan.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                await PublishUnavailableAsync("Die abschließende Prüfung der Dateiänderungen wurde nicht vollständig beendet.",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _tracker?.Dispose();
            _stop.Dispose();
        }
    }
}
