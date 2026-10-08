using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Missum.Core.Contracts;
using Missum.Core.Chat;
using Missum.Core.Extensions;
using Missum.Core.Models;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>One application-lifetime command/event host for native WinUI and LAN clients.</summary>
public sealed class AssistantHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Exception?> LogBrowserRouteUnavailable = LoggerMessage.Define(
        LogLevel.Warning, new EventId(9420, "BrowserRouteUnavailable"), "Die lokale Browserroute auf Port 8080 ist nicht verfügbar.");
    private static readonly Action<ILogger, string, Exception?> LogPeerReasoningUnavailable = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(9421, "PeerReasoningUnavailable"), "Die gemeinsame Reasoning-Anzeige für Client {ClientId} konnte nicht aktualisiert werden.");
    private static readonly HashSet<string> SharedEvents = new(StringComparer.Ordinal)
    {
        "chat.queued", "queue.changed", "chat.started", "chat.delta", "chat.completed", "chat.cancelled", "chat.failed",
        "conversation.messageCommitted", "coding.changes", "status.changed", "subagent.snapshot",
    };
    private readonly IServiceProvider _services;
    private readonly SettingsCoordinator _settings;
    private readonly AssistantCoordinator _coordinator;
    private readonly AssistantClientStateStore _views;
    private readonly ConcurrentDictionary<string, Func<string, object, string?, Task>?> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _commands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _receipts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _resources = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _audioDownloads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly SemaphoreSlim _receiptGate = new(1, 1);
    private readonly object _admissionGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly string _receiptPath;
    private readonly string _localUrl = "http://127.0.0.1:" + (Environment.GetEnvironmentVariable("ASSISTANT_GATEWAY_PORT") ?? "8080") + "/assistant/";
    private AssistantLanWebHost? _lan;
    private long _revision;
    private bool _restoring;
    private int _restorePending;
    private int _admittedCommands;
    private int _disposed;

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

    public string? AccessFilePath => _lan?.AccessFilePath;
    public IReadOnlyList<string> AccessUrls => _lan?.AccessUrls ?? [];
    public string LocalUrl => _localUrl;

    public async Task StartAsync(CancellationToken token = default)
    {
        if (_lan is not null) return;
        var host = new AssistantLanWebHost(_settings.DataDirectory, _services.GetRequiredService<ILogger<AssistantHost>>());
        host.ArtifactResolver = ResolveArtifactAsync;
        host.ResourceResolver = ResolveResourceAsync;
        host.CodingPreviewResolver = ResolveCodingPreviewAsync;
        host.MessageReceived += OnLanMessage;
        var settingsService = _services.GetRequiredService<AssistantSettingsService>();
        settingsService.Changed += OnSettingsChanged;
        settingsService.RestoreHandler = RestoreBackupAsync;
        var speech = _services.GetRequiredService<BrowserSpeechService>();
        _coordinator.BrowserSpeechHandler = speech.HandleAsync;
        _coordinator.BrowserAutomaticSpeech = (id, update, emit) => speech.ObserveAutomaticSpeech(id, update, emit, _lifetime.Token);
        _services.GetRequiredService<ScientificPresentationCoordinator>().SnapshotPublished += OnSciencePublished;
        try
        {
            await host.StartAsync(ApplicationAssets.ResolvePath("Assets", "Web"),
                _services.GetRequiredService<AssistantArtifactPreviewService>().CacheRoot, token).ConfigureAwait(false);
            _lan = host;
            _ = EnsureLocalBrowserRouteAsync();
        }
        catch
        {
            host.MessageReceived -= OnLanMessage;
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureLocalBrowserRouteAsync()
    {
        var lifecycle = _services.GetService<MissumAiStackLifecycleService>();
        if (lifecycle is null) return;
        try
        {
            // The installed Caddy route belongs to this PC, even when the user edits the AI gateway address.
            await lifecycle.EnsureStartedAsync(new Uri(new Uri(LocalUrl).GetLeftPart(UriPartial.Authority)), _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogBrowserRouteUnavailable(_services.GetRequiredService<ILogger<AssistantHost>>(), exception);
        }
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

    private void OnLanMessage(object? sender, WebBridgeMessageEventArgs args)
    {
        var clientId = args.Envelope.ClientId;
        if (!AssistantClientStateStore.IsValidId(clientId) || clientId == "desktop") return;
        _clients.TryAdd(clientId!, null);
        var key = clientId + ":" + args.Envelope.RequestId;
        // Execute remains alive across transport reconnects. The transport acknowledgement is not the run lifetime.
        var task = HandleAsync(clientId!, args.Envelope, cancellationToken: _lifetime.Token);
        _commands[key] = task;
        _ = ObserveCommandAsync(key, task);
    }

    private async Task ObserveCommandAsync(string key, Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally { _commands.TryRemove(key, out _); }
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
            if (clientId != "desktop" && type == "research.exported")
                await ExportResearchDownloadAsync(clientId, payload, id ?? envelope.RequestId, receive, cancellationToken).ConfigureAwait(false);
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
            AdmitCommand(envelope.Type);
            // A restore commit waits for other admissions, never for its own handler.
            admitted = envelope.Type != "backup.restoreCommit";
            if (envelope.Type == "app.ready" && clientId != "desktop")
                await _services.GetRequiredService<BrowserSpeechService>().HandleAsync(clientId,
                    envelope with { Type = "microphone.stopSpeech" }, Emit, cancellationToken).ConfigureAwait(false);
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
            else if (clientId != "desktop" && await _services.GetRequiredService<BrowserSpeechService>().HandleAsync(clientId, envelope, Emit, cancellationToken).ConfigureAwait(false)) { }
            else if (envelope.Type == "document.upload") await UploadAsync(envelope, Emit, cancellationToken).ConfigureAwait(false);
            else if (envelope.Type == "workspace.browse")
            {
                var offset = envelope.Payload.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var parsedOffset) ? parsedOffset : 0;
                var listing = await ServerWorkspaceBrowser.BrowseAsync(Text(envelope.Payload, "path"), Text(envelope.Payload, "filter"), offset, cancellationToken).ConfigureAwait(false);
                await Emit("workspace.list", listing, envelope.RequestId).ConfigureAwait(false);
            }
            else if (envelope.Type == "coding.pickWorkspace")
            {
                var path = Text(envelope.Payload, "workspacePath");
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Gib den Projektordner auf dem Windows-PC an.");
                await _coordinator.SetCodingWorkspacePathAsync(Session(envelope.Payload), path, cancellationToken).ConfigureAwait(false);
                await Emit("state.snapshot", await _coordinator.BuildSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
            }
            else if (envelope.Type == "message.exportPdf" || envelope.Type == "action.invoke" && Text(envelope.Payload, "actionId") == BuiltInActionIds.ExportChatPdf)
                await ExportPdfAsync(envelope, Emit, cancellationToken).ConfigureAwait(false);
            else if (envelope.Type == "artifact.preview")
            {
                var artifactId = Guid.Parse(Text(envelope.Payload, "artifactId"));
                var preview = await _services.GetRequiredService<AssistantArtifactPreviewService>().PrepareAsync(artifactId, cancellationToken).ConfigureAwait(false);
                await Emit("artifact.previewReady", new { artifactId, url = preview.Url, posterUrl = preview.PosterUrl }, envelope.RequestId).ConfigureAwait(false);
            }
            else if (envelope.Type == "science.presentation.get")
                await SendScienceAsync(clientId, Text(envelope.Payload, "projectId"), receive, envelope.RequestId, cancellationToken).ConfigureAwait(false);
            else if (clientId != "desktop" && envelope.Type is "screen.capture" or "screenClip.start" or "audioCapture.start" or "microphone.start" or "microphone.audio" or "liveCaption.start")
                throw new InvalidOperationException("Aufnahme ist über HTTP nicht verfügbar. Lade eine Datei oder einen Screenshot vom Mac hoch.");
            else
                await _coordinator.HandleAsync(envelope with { ClientId = clientId }, Emit, cancellationToken).ConfigureAwait(false);

            if (envelope.Type == "app.ready")
            {
                await Emit("settings.snapshot", await _services.GetRequiredService<AssistantSettingsService>().GetSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
                await Emit("host.capabilities", new { microphone = clientId == "desktop", screenCapture = clientId == "desktop", browserSpeech = "supertonic-3-f5-cuda", localUrl = LocalUrl, accessUrls = AccessUrls }, envelope.RequestId).ConfigureAwait(false);
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
            if (_restorePending != 0 && type is "chat.send" or "chat.resume")
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
            var revision = ++_revision;
            var targets = SharedEvents.Contains(type) ? _clients.Keys.ToArray() : [owner];
            foreach (var clientId in targets)
            {
                var callback = clientId == owner && receive is not null ? receive : _clients.GetValueOrDefault(clientId);
                if (callback is not null)
                    await callback(type, payload, requestId).ConfigureAwait(false);
                else if (_lan is not null)
                {
                    var envelope = JsonSerializer.Serialize(new
                    {
                        version = 2, type, requestId, payload, revision, hostEpoch = _epoch,
                        clientId, tabId = clientId[(clientId.LastIndexOf('.') + 1)..], originClientId = owner,
                    }, JsonOptions);
                    await _lan.SendToClientAsync(clientId, envelope, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        finally { _eventGate.Release(); }
        if (type == "research.snapshot")
        {
            var json = JsonSerializer.SerializeToElement(payload, JsonOptions);
            var id = Text(json, "selectedProjectId");
            if (string.IsNullOrWhiteSpace(id) && json.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("project", out var project)) id = Text(project, "id");
            if (!string.IsNullOrWhiteSpace(id)) await SendScienceAsync(owner, id, receive, requestId, _lifetime.Token).ConfigureAwait(false);
        }
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

    private async Task UploadAsync(WebBridgeEnvelope envelope, Func<string, object, string?, Task> emit, CancellationToken token)
    {
        var session = Session(envelope.Payload);
        if (!envelope.Payload.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32)
            throw new InvalidDataException("Wähle zwischen einer und 32 Dateien aus.");
        var files = new List<(string Name, string Type, byte[] Bytes)>();
        long size = 0;
        foreach (var file in array.EnumerateArray())
        {
            var name = Text(file, "fileName");
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Length > 240) throw new InvalidDataException("Ungültiger Dateiname.");
            var bytes = Convert.FromBase64String(Text(file, "base64"));
            size += bytes.Length;
            if (bytes.Length is 0 or > 33_554_432 || size > 50_331_648) throw new InvalidDataException("Der Upload überschreitet 32 MB pro Datei oder 48 MB insgesamt.");
            files.Add((name, Text(file, "contentType", "application/octet-stream"), bytes));
        }
        await emit("document.import.started", new { files = files.Select(file => file.Name).ToArray() }, envelope.RequestId).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                await using var content = new MemoryStream(file.Bytes, writable: false);
                if (_coordinator.SupportedDocumentExtensions.Contains(Path.GetExtension(file.Name)))
                    await _coordinator.ImportDocumentAsync(session, file.Name, content, token).ConfigureAwait(false);
                else await _coordinator.ImportAttachmentAsync(session, file.Name, file.Type, content, token).ConfigureAwait(false);
                await emit("document.import.progress", new { remaining = files.Skip(i + 1).Select(file => file.Name).ToArray() }, envelope.RequestId).ConfigureAwait(false);
            }
        }
        finally { await emit("document.import.completed", new { }, envelope.RequestId).ConfigureAwait(false); }
        await emit("document.changed", await _coordinator.BuildSnapshotAsync(token).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
    }

    private async Task ExportPdfAsync(WebBridgeEnvelope envelope, Func<string, object, string?, Task> emit, CancellationToken token)
    {
        Guid? messageId = Guid.TryParse(Text(envelope.Payload, "messageId"), out var parsed) ? parsed : null;
        var message = messageId.HasValue ? await _services.GetRequiredService<IChatRepository>().GetMessageAsync(messageId.Value, token).ConfigureAwait(false) : null;
        var sessionId = Guid.TryParse(Text(envelope.Payload, "sessionId"), out var requestedSession) ? requestedSession
            : message?.SessionId ?? AssistantClientExecutionScope.Resolve(_settings.Current).ActiveSessionId
                ?? throw new InvalidOperationException("Öffne zuerst einen Chat für den PDF-Export.");
        if (messageId.HasValue && (message is null || message.SessionId != sessionId)) throw new InvalidOperationException("Die Nachricht gehört nicht zu dieser Sitzung.");
        var root = Path.Combine(_settings.DataDirectory, "LanWeb", "Exports");
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        var path = Path.Combine(root, "Missum-Chat-" + id.ToString("N") + ".pdf");
        await _services.GetRequiredService<NativeChatPdfExportService>().ExportAsync(sessionId, path, messageId, token).ConfigureAwait(false);
        _resources[id] = path;
        await emit("download.ready", new { url = "science-resources/" + id.ToString("D"), fileName = Path.GetFileName(path) }, envelope.RequestId).ConfigureAwait(false);
        await emit("conversation.snapshot", await _coordinator.BuildConversationForHostAsync(sessionId, token).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
    }

    private async Task ExportResearchDownloadAsync(string clientId, object payload, string? requestId,
        Func<string, object, string?, Task>? receive, CancellationToken token)
    {
        var details = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var directory = Path.GetFullPath(Text(details, "directory"));
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var exportRoot = Path.Combine(_settings.DataDirectory, "LanWeb", "Exports");
        Directory.CreateDirectory(exportRoot);
        var id = Guid.NewGuid();
        var path = Path.Combine(exportRoot, "Missum-Forschung-" + id.ToString("N") + ".zip");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var item in details.GetProperty("files").EnumerateArray())
                {
                    var file = Path.GetFullPath(item.GetString()!);
                    if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Eine Exportdatei liegt außerhalb des Forschungspakets.");
                    var entry = archive.CreateEntry(Path.GetRelativePath(directory, file).Replace('\\', '/'));
                    await using var input = File.OpenRead(file);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }
            _resources[id] = path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        await PublishAsync(clientId, "download.ready", new { url = "science-resources/" + id.ToString("D"), fileName = Path.GetFileName(path) }, requestId, receive).ConfigureAwait(false);
    }

    private async Task<LanArtifactResource?> ResolveArtifactAsync(Guid id, CancellationToken token)
    {
        var artifact = await _services.GetRequiredService<IChatArtifactRepository>().GetAsync(id, token).ConfigureAwait(false);
        if (artifact is null) return null;
        var buffer = new MemoryStream();
        await _services.GetRequiredService<IBinaryObjectStore>().ExportAsync(artifact.BlobId, buffer, token).ConfigureAwait(false);
        buffer.Position = 0;
        return new(buffer, artifact.ContentType, artifact.FileName);
    }

    private async Task<LanArtifactResource?> ResolveResourceAsync(string kind, string id, CancellationToken token)
    {
        if (kind == "gateway-artifacts")
        {
            var speech = _services.GetRequiredService<BrowserSpeechService>();
            if (!speech.TryGetArtifact(id, out var artifact) || artifact is null) return null;
            _ = speech.TryGetArtifactGateway(id, out var gatewayAddress);
            var root = Path.Combine(_settings.DataDirectory, "LanWeb", "Audio");
            Directory.CreateDirectory(root);
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".wav";
            var path = Path.Combine(root, name);
            var gate = _audioDownloads.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path))
                {
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using var client = await _services.GetRequiredService<MissumAiConnectionService>().CreateClientAsync(
                            gatewayAddress, ensureProfileNativeRuntime: false, token).ConfigureAwait(false);
                        await client.DownloadArtifactAsync(artifact.ArtifactId, temporary, cancellationToken: token).ConfigureAwait(false);
                        await using (var content = File.OpenRead(temporary))
                        {
                            var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, token).ConfigureAwait(false));
                            if (content.Length != artifact.Length || !hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Der F5-Audioabschnitt ist unvollständig.");
                        }
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            finally { gate.Release(); }
            return new(File.OpenRead(path), artifact.MediaType, artifact.FileName);
        }
        if (!Guid.TryParse(id, out var resourceId)) return null;
        if (kind == "settings-resources" && _services.GetRequiredService<AssistantSettingsService>().TryGetResource(resourceId, out var settingsResource)
            && settingsResource is not null && File.Exists(settingsResource.Path))
            return new(File.OpenRead(settingsResource.Path), settingsResource.ContentType, settingsResource.FileName);
        if (kind == "science-resources" && _resources.TryGetValue(resourceId, out var file) && File.Exists(file))
            return new(File.OpenRead(file), ContentType(file), Path.GetFileName(file));
        return null;
    }

    private async Task<string?> ResolveCodingPreviewAsync(Guid messageId, string stepId, CancellationToken token)
    {
        var message = await _services.GetRequiredService<IChatRepository>().GetMessageAsync(messageId, token).ConfigureAwait(false);
        return message?.ToolSteps?.FirstOrDefault(step => step.Id == stepId)?.PreviewHtml;
    }

    private async void OnSciencePublished(object? sender, ScientificPresentationPublishedEventArgs args)
    {
        try
        {
            foreach (var clientId in _clients.Keys) await SendScienceAsync(clientId, args.ProjectId, null, null, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private async Task SendScienceAsync(string clientId, string projectId, Func<string, object, string?, Task>? receive, string? requestId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        var service = _services.GetRequiredService<ScientificPresentationCoordinator>();
        var presentation = service.GetSnapshot(projectId);
        if (presentation is null)
        {
            service.Queue(projectId);
            await PublishAsync(clientId, "science.presentation", new { projectId, status = "preparing" }, requestId, receive).ConfigureAwait(false);
            return;
        }
        string? Register(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var key = _resources.FirstOrDefault(pair => pair.Value == path).Key;
            if (key == Guid.Empty) { key = Guid.NewGuid(); _resources[key] = path; }
            return "science-resources/" + key.ToString("D");
        }
        var publication = presentation.Publication;
        var figures = presentation.Simulation?.Artifacts.Select(item => new
        {
            item.Id, item.Title, url = Register(item.ImagePath), scriptUrl = Register(item.ScriptPath), dataUrl = Register(item.DataPath),
            item.Provenance, item.IsResearchData, item.Sha256, item.Kind, item.ContentType,
        }).ToArray();
        await PublishAsync(clientId, "science.presentation", new
        {
            projectId, revision = presentation.Version,
            publication = publication is null ? null : new { pdfUrl = Register(publication.PdfPath), markdownUrl = Register(publication.MarkdownPath), publication.IsDraft, publication.Revision },
            simulation = presentation.Simulation is null ? null : new { presentation.Simulation.Status, presentation.Simulation.Detail, artifacts = figures },
            presentation.PublicationError, presentation.SimulationError,
        }, requestId, receive).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
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

    private static bool IsMutation(string type) => type is "chat.send" or "chat.resume" or "action.invoke" or "document.upload"
        or "document.remove" or "attachment.remove" or "settings.update" or "promptTriggers.apply" or "backup.restoreCommit"
        or "session.create" or "session.rename" or "session.delete" or "session.clear" or "session.projectCreate" or "session.workspaceCreate" or "session.groupDeleteEmpty"
        or "microphone.speak" or "message.exportPdf" or "research.export";
    private static string Text(JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? fallback : fallback;
    private static Guid Session(JsonElement payload) => Guid.Parse(Text(payload, "sessionId"));
    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    { ".pdf" => "application/pdf", ".zip" => "application/zip", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".svg" => "image/svg+xml", ".json" => "application/json", ".html" or ".htm" => "text/html", _ => "text/plain" };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _services.GetRequiredService<AssistantSettingsService>().Changed -= OnSettingsChanged;
        _services.GetRequiredService<ScientificPresentationCoordinator>().SnapshotPublished -= OnSciencePublished;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_lan is not null) await _lan.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
    private sealed class Subscription(Action release) : IDisposable { public void Dispose() => release(); }
}
