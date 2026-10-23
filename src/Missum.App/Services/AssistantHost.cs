using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Missum.Core.Contracts;
using Missum.Core.Chat;
using Missum.Core.Extensions;
using Missum.Core.Models;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Application-lifetime command/event host for native WinUI.</summary>
public sealed class AssistantHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, Exception?> LogPeerReasoningUnavailable = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(9421, "PeerReasoningUnavailable"), "Die gemeinsame Reasoning-Anzeige für Client {ClientId} konnte nicht aktualisiert werden.");
    private static readonly HashSet<string> SharedEvents = new(StringComparer.Ordinal)
    {
        "chat.queued", "queue.changed", "chat.started", "chat.delta", "chat.completed", "chat.cancelled", "chat.failed",
        "conversation.messageCommitted", "conversation.toolStepChanged", "coding.changes", "status.changed", "subagent.snapshot", "workingPlan.changed",
    };
    private readonly IServiceProvider _services;
    private readonly SettingsCoordinator _settings;
    private readonly AssistantCoordinator _coordinator;
    private readonly AssistantClientStateStore _views;
    private readonly ConcurrentDictionary<string, Func<string, object, string?, Task>?> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _receipts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly SemaphoreSlim _receiptGate = new(1, 1);
    private readonly object _admissionGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _receiptPath;
    private bool _restoring;
    private int _restorePending;
    private int _admittedCommands;
    private int _disposed;
    private int _started;

    public AssistantHost(IServiceProvider services, SettingsCoordinator settings, AssistantCoordinator coordinator, AssistantClientStateStore views)
    {
        _services = services; _settings = settings; _coordinator = coordinator; _views = views;
        _receiptPath = Path.Combine(settings.DataDirectory, "ClientState", "requests.json");
        if (File.Exists(_receiptPath))
        {
            try
            {
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_receiptPath), JsonOptions) ?? [])
                    _receipts[pair.Key] = pair.Value;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    public Task StartAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            var settingsService = _services.GetRequiredService<AssistantSettingsService>();
            settingsService.Changed += OnSettingsChanged;
            settingsService.RestoreHandler = RestoreBackupAsync;

        }
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(string clientId, Func<string, object, string?, Task> receive)
    {
        if (!AssistantClientStateStore.IsValidId(clientId)) throw new ArgumentException("Ungültige Clientkennung.", nameof(clientId));
        _clients[clientId] = receive;
        return new Subscription(() => ((ICollection<KeyValuePair<string, Func<string, object, string?, Task>?>>)_clients)
            .Remove(new(clientId, receive)));
    }

    public async Task SaveDraftAsync(string clientId, Guid sessionId, string draft, CancellationToken token = default)
    {
        AdmitCommand("session.draft");
        try
        {
            using var scope = AssistantClientExecutionScope.Enter(_views, clientId);
            await _coordinator.SaveDraftAsync(sessionId, draft, token).ConfigureAwait(false);
        }
        finally { ReleaseCommand(); }
    }

    public async Task HandleAsync(string clientId, WebBridgeEnvelope envelope,
        Func<string, object, string?, Task>? receive = null, CancellationToken cancellationToken = default)
    {
        if (!AssistantClientStateStore.IsValidId(clientId)) throw new ArgumentException("Ungültige Clientkennung.", nameof(clientId));
        using var scope = AssistantClientExecutionScope.Enter(_views, clientId);
        _clients.TryAdd(clientId, receive);
        var key = clientId + ":" + envelope.RequestId;
        var deduplicate = IsMutation(envelope.Type);
        var admitted = false;
        var claimed = false;
        async Task Emit(string type, object payload, string? id)
        {
            await PublishAsync(clientId, type, payload, id ?? envelope.RequestId, receive).ConfigureAwait(false);
            if (envelope.Type == "reasoning.set" && type == "reasoning.snapshot")
                await RefreshPeerReasoningAsync(clientId, payload, cancellationToken).ConfigureAwait(false);
            if (type == "document.changed")
            {
                var snapshot = JsonSerializer.SerializeToElement(payload, JsonOptions);
                if (snapshot.TryGetProperty("activeSessionId", out var session) && session.ValueKind == JsonValueKind.String && session.TryGetGuid(out var sessionId))
                    await RefreshDocumentsAsync(clientId, sessionId, id ?? envelope.RequestId, cancellationToken).ConfigureAwait(false);
            }
        }
        try
        {
            if (!AssistantProtocol.IsIncomingTypeAllowed(envelope.Type))
                throw new InvalidOperationException("Unbekannter Missum-Befehl: " + envelope.Type);
            AdmitCommand(envelope.Type);
            // A restore commit waits for other admissions, never for its own handler.
            admitted = envelope.Type != "backup.restoreCommit";
            if (deduplicate) claimed = await ClaimAsync(key, cancellationToken).ConfigureAwait(false);
            if (deduplicate && !claimed)
            {
                var receiptStatus = _receipts.GetValueOrDefault(key);
                await Emit("action.completed", new { duplicate = true, originalRequestId = envelope.RequestId,
                    receiptStatus }, envelope.RequestId).ConfigureAwait(false);
                if (receiptStatus != "accepted")
                    await Emit("state.snapshot", await _coordinator.BuildSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
                return;
            }
            var initialView = AssistantClientExecutionScope.Resolve(_settings.Current);
            Guid? affectedSessionId = envelope.Type == "session.delete"
                ? Guid.TryParse(Text(envelope.Payload, "sessionId"), out var removedSession) ? removedSession : initialView.ActiveSessionId
                : null;
            ChatMode? clearedMode = envelope.Type == "session.clear"
                ? AssistantCoordinator.ParseChatMode(Text(envelope.Payload, "chatMode", Text(envelope.Payload, "mode", initialView.SelectedChatMode.ToString())))
                : null;
            if (AssistantSettingsService.CanHandle(envelope.Type))
                await _services.GetRequiredService<AssistantSettingsService>().HandleAsync(envelope, Emit, cancellationToken).ConfigureAwait(false);
            else if (envelope.Type == "coding.pickWorkspace")
            {
                var path = Text(envelope.Payload, "workspacePath");
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Gib den Projektordner auf dem Windows-PC an.");
                await _coordinator.SetCodingWorkspacePathAsync(Session(envelope.Payload), path, cancellationToken).ConfigureAwait(false);
                await Emit("state.snapshot", await _coordinator.BuildSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
            }
            else if (envelope.Type == "artifact.preview")
            {
                var artifactId = Guid.Parse(Text(envelope.Payload, "artifactId"));
                var preview = await _services.GetRequiredService<AssistantArtifactPreviewService>().PrepareAsync(artifactId, cancellationToken).ConfigureAwait(false);
                await Emit("artifact.previewReady", new { artifactId, url = preview.Url, posterUrl = preview.PosterUrl }, envelope.RequestId).ConfigureAwait(false);
            }
            else
                await _coordinator.HandleAsync(envelope with { ClientId = clientId }, Emit, cancellationToken).ConfigureAwait(false);

            if (envelope.Type == "app.ready")
            {
                await Emit("settings.snapshot", await _services.GetRequiredService<AssistantSettingsService>().GetSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
            }
            if (envelope.Type.StartsWith("session.", StringComparison.Ordinal) && envelope.Type != "session.draft")
                await RefreshSidebarsAsync(clientId, affectedSessionId, clearedMode, cancellationToken).ConfigureAwait(false);
            if (deduplicate) await RecordAsync(key, "complete", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (claimed) await RecordAsync(key, "failed", CancellationToken.None).ConfigureAwait(false);
            await Emit("host.error", new { message = exception.Message, type = envelope.Type }, envelope.RequestId).ConfigureAwait(false);
            if (clientId == "desktop" && receive is null) throw;
        }
        finally { if (admitted) ReleaseCommand(); }
    }

    private void AdmitCommand(string type)
    {
        lock (_admissionGate)
        {
            if (_restoring) throw new InvalidOperationException("Missum stellt ein Backup wieder her. Bitte warte auf die Wiederverbindung.");
            if (_restorePending != 0 && type is "chat.send" or "chat.resume" or "chat.compact")
                throw new InvalidOperationException("Die Wiederherstellung wartet auf die laufenden Aufträge. Neue Aufträge können nach dem Neustart gestartet werden.");
            if (type != "backup.restoreCommit") _admittedCommands++;
        }
    }

    private void ReleaseCommand()
    {
        lock (_admissionGate) _admittedCommands--;
    }

    private async Task PublishAsync(string owner, string type, object payload, string? requestId,
        Func<string, object, string?, Task>? receive = null)
    {
        await _eventGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var targets = SharedEvents.Contains(type) ? _clients.Keys.ToArray() : [owner];
            foreach (var clientId in targets)
            {
                var callback = clientId == owner && receive is not null ? receive : _clients.GetValueOrDefault(clientId);
                if (callback is not null)
                    await callback(type, payload, requestId).ConfigureAwait(false);

            }
        }
        finally { _eventGate.Release(); }
    }

    private async Task RefreshSidebarsAsync(string origin, Guid? affectedSessionId, ChatMode? clearedMode, CancellationToken token)
    {
        var peers = _clients.Keys.Where(id => id != origin).ToArray();
        var chats = _services.GetRequiredService<IChatRepository>();
        // Resolve missing views first: a peer may need to create the replacement
        // for its mode before all clients receive their updated sidebars.
        foreach (var id in peers)
        {
            using var scope = AssistantClientExecutionScope.Enter(_views, id);
            var view = AssistantClientExecutionScope.Resolve(_settings.Current);
            var session = view.ActiveSessionId is { } activeId
                ? await chats.GetSessionAsync(activeId, token).ConfigureAwait(false) : null;
            if (session is null || session.ChatMode != view.SelectedChatMode
                || affectedSessionId.HasValue && view.ActiveSessionId == affectedSessionId
                || clearedMode == view.SelectedChatMode)
                await PublishAsync(id, "session.changed", await _coordinator.BuildSnapshotAsync(token).ConfigureAwait(false), null).ConfigureAwait(false);
        }
        foreach (var id in peers)
        {
            using var scope = AssistantClientExecutionScope.Enter(_views, id);
            await PublishAsync(id, "session.grouped", await _coordinator.BuildSessionSidebarSnapshotAsync(false, token).ConfigureAwait(false), null).ConfigureAwait(false);
        }
    }

    private async Task RefreshDocumentsAsync(string origin, Guid sessionId, string? requestId, CancellationToken token)
    {
        foreach (var id in _clients.Keys.Where(id => id != origin))
        {
            using var scope = AssistantClientExecutionScope.Enter(_views, id);
            if (AssistantClientExecutionScope.Resolve(_settings.Current).ActiveSessionId == sessionId)
                await PublishAsync(id, "document.changed", await _coordinator.BuildSnapshotAsync(token).ConfigureAwait(false), requestId).ConfigureAwait(false);
        }
    }

    private async Task RefreshPeerReasoningAsync(string origin, object payload, CancellationToken token)
    {
        var changed = payload as ComposerReasoningOptions
            ?? JsonSerializer.SerializeToElement(payload, JsonOptions).Deserialize<ComposerReasoningOptions>(JsonOptions);
        if (changed is null) return;
        var modelId = changed.ModelId;
        var service = _services.GetRequiredService<MissumAiAssistantService>();
        foreach (var id in _clients.Keys.Where(id => id != origin))
        {
            using var scope = AssistantClientExecutionScope.Enter(_views, id);
            var view = AssistantClientExecutionScope.Resolve(_settings.Current);
            if (!string.Equals(view.SelectedModel, modelId, StringComparison.Ordinal)) continue;
            var role = view.SelectedChatMode == ChatMode.Coding ? "coding" : "general";
            try
            {
                var options = role == changed.Role ? changed
                    : await service.GetReasoningOptionsAsync(modelId, role, token).ConfigureAwait(false);
                if (AssistantClientExecutionScope.Resolve(_settings.Current).SelectedModel != modelId) continue;
                await PublishAsync(id, "reasoning.snapshot", options, null).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
            {
                // A peer display failure must not mark the successful durable
                // preference write as a failed command for the originating tab.
                LogPeerReasoningUnavailable(_services.GetRequiredService<ILogger<AssistantHost>>(), id, exception);
            }
        }
    }

    private async void OnSettingsChanged(object? sender, EventArgs args)
    {
        try { await RefreshAppearanceAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    public async Task RefreshAppearanceAsync()
    {
        var snapshot = await _services.GetRequiredService<AssistantSettingsService>().GetSnapshotAsync(_lifetime.Token).ConfigureAwait(false);
        foreach (var clientId in _clients.Keys) await PublishAsync(clientId, "settings.changed", snapshot, null).ConfigureAwait(false);
    }

    private async Task<bool> RestoreBackupAsync(AssistantPreparedBackupRestore prepared, CancellationToken token)
    {
        await PerformRestoreAsync(() => _services.GetRequiredService<AssistantSettingsService>().CompleteRestoreAsync(prepared.Id, token), prepared.Id, token).ConfigureAwait(false);
        _ = RestartAfterRestoreAsync();
        return true;
    }

    public Task RestoreBackupFileAsync(string path, CancellationToken token = default) =>
        PerformRestoreAsync(() => _services.GetRequiredService<AssistantSettingsService>().RestoreBackupAsync(path, token), null, token);

    private async Task PerformRestoreAsync(Func<Task> restore, Guid? restoreId, CancellationToken token)
    {
        lock (_admissionGate)
        {
            if (_restorePending != 0 || _restoring)
                throw new InvalidOperationException("Eine Wiederherstellung ist bereits vorgemerkt.");
            _restorePending = 1;
        }
        try
        {
            var scheduler = _services.GetService<IAssistantRunScheduler>();
            var assistant = _services.GetService<MissumAiAssistantService>();
            var deferred = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (_admissionGate)
                {
                    // An admitted send may still be persisting its receipt or
                    // resolving its input before it reaches the scheduler.
                    if (_admittedCommands == 0 && scheduler?.Snapshot.IsIdle != false && assistant?.IsRunning != true)
                    {
                        _restoring = true;
                        break;
                    }
                }
                if (!deferred)
                {
                    deferred = true;
                    await PublishAsync(AssistantClientExecutionScope.ClientId, "backup.restoreDeferred", new
                    { restoreId, message = "Wiederherstellung wartet auf den Abschluss der aktiven Aufträge." }, null).ConfigureAwait(false);
                }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            await restore().ConfigureAwait(false);
        }
        catch { lock (_admissionGate) _restoring = false; throw; }
        finally { lock (_admissionGate) _restorePending = 0; }
    }

    private async Task RestartAfterRestoreAsync()
    {
        await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false);
        App.Current.MainWindow?.DispatcherQueue.TryEnqueue(() => Microsoft.Windows.AppLifecycle.AppInstance.Restart("--restored-backup"));
    }

    private async Task<bool> ClaimAsync(string key, CancellationToken token)
    {
        await _receiptGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_receipts.TryAdd(key, "accepted")) return false;
            await SaveReceiptsAsync(token).ConfigureAwait(false);
            return true;
        }
        finally { _receiptGate.Release(); }
    }

    private async Task RecordAsync(string key, string status, CancellationToken token)
    {
        await _receiptGate.WaitAsync(token).ConfigureAwait(false);
        try { _receipts[key] = status; await SaveReceiptsAsync(token).ConfigureAwait(false); }
        finally { _receiptGate.Release(); }
    }

    private async Task SaveReceiptsAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_receiptPath)!);
        var temporary = _receiptPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_receipts, JsonOptions), token).ConfigureAwait(false);
        File.Move(temporary, _receiptPath, overwrite: true);
    }

    private static bool IsMutation(string type) => type is "chat.send" or "chat.resume" or "chat.compact" or "action.invoke"
        or "document.remove" or "attachment.remove" or "settings.update" or "promptTriggers.apply" or "backup.restoreCommit"
        or "session.create" or "session.rename" or "session.delete" or "session.clear" or "session.projectCreate" or "session.workspaceCreate" or "session.groupDeleteEmpty"
        or "microphone.speak" or "message.exportPdf" or "research.export";
    private static string Text(JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? fallback : fallback;
    private static Guid Session(JsonElement payload) => Guid.Parse(Text(payload, "sessionId"));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _services.GetRequiredService<AssistantSettingsService>().Changed -= OnSettingsChanged;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
    private sealed class Subscription(Action release) : IDisposable { public void Dispose() => release(); }
}
