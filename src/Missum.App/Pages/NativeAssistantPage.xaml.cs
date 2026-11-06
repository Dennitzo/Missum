using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.App.Services;
using Missum.App.Controls;
using Missum.Core.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Documents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Missum.App.Pages;

/// <summary>Native XAML presentation of the existing Missum coordinator protocol. No browser is created.</summary>
public sealed partial class NativeAssistantPage : Page, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AssistantCoordinator _coordinator;
    private readonly AssistantHost _assistantHost;
    private readonly IDisposable _hostSubscription;
    private readonly SettingsCoordinator _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _draftTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _renderTimer;
    private readonly Dictionary<string, (string Signature, Border View)> _messageViews = new(StringComparer.Ordinal);
    private bool _messagesDirty;
    private string _reasoningModel = "";
    private ComposerReasoningOptions? _reasoning;
    private double _contextUsed, _contextLimit;
    private readonly Dictionary<string, JsonElement> _messages = new(StringComparer.Ordinal);
    private JsonElement _snapshot;
    private Guid _session;
    private string _mode = "general";
    private bool _initialised, _rendering, _running, _disposed;
    private bool _speaking;
    private bool _sendPending;

    private string? _dictationPrefix;
    private Guid? _dictationSession;
    private string? _selectedAction;
    private string? _persistentAction;
    private readonly HashSet<string> _failedRequests = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _finishedSessions = [];
    private readonly NativeNavigationState _navigationState = new();
    private Task _navigationTail = Task.CompletedTask;
    private readonly MicrophoneTranscriptionService _microphone;

    private NativeConversationSelection? _conversationSelection;
    private bool _selectionInputSmokeActive;

    public NativeAssistantPage()
    {
        InitializeComponent();
        NativeNotice.Attach(ErrorBar);
        _conversationSelection = new NativeConversationSelection(ConversationContent, MessagesPanel, ConversationScroll)
        {
            ReadFromMenuFactory = CreateReadFromMenu,
            UiProjectionCallback = (phase, projection) => RunUiCallback(phase, projection),
            SelectionChangedCallback = () =>
            {
                _messagesDirty = true;
                if (_conversationSelection?.IsSelecting == true && CanFollowConversationTail)
                    OnToolDisclosureInteraction();
            },
            CopyFailed = message => ShowError(message),
        };
        ResetChangesSummary();
        ComposerChipsScroll.SizeChanged += (_, _) => RunUiCallback("ComposerChips.SizeChanged", () =>
        {
            var width = Math.Clamp(ComposerChipsScroll.ActualWidth, 28, 220);
            SelectedToolChip.MaxWidth = CaptionChip.MaxWidth = width;
        });
        ConfigureSidebarHoverActions(ProjectsHeader, AddProjectButton);
        Composer.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnComposerKeyDown), true);
        NavigationCacheMode = NavigationCacheMode.Required;
        // Math/images and incremental message blocks can finish layout after the
        // first render. Keep session entry pinned until an explicit reading gesture.
        ConversationContent.SizeChanged += (_, _) =>
        {
            if (_scrollNewSessionToLatest) QueueSessionScrollToLatest();
        };
        // Observe wheel intent before the outer ScrollViewer's default handler.
        // Hover, copying, and tiny touchpad fragments do not leave follow mode.
        ConversationContent.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnConversationPointerWheelChanged), true);
        ConversationScroll.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnConversationScrollSurfaceWheelChanged), true);
        ConversationScroll.AddHandler(UIElement.PreviewKeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(OnConversationScrollKeyDown), true);
        OuterChatScrollBar.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => OnToolDisclosureInteraction()), true);
        _coordinator = App.Current.GetService<AssistantCoordinator>();
        _assistantHost = App.Current.GetService<AssistantHost>();
        _hostSubscription = _assistantHost.Subscribe("desktop", (type, payload, id) => EmitAsync(type, payload, id));
        _settings = App.Current.GetService<SettingsCoordinator>();
        _inspectorRequested = _settings.Current.AssistantOutputsVisible;
        _microphone = App.Current.GetService<MicrophoneTranscriptionService>();
        _microphone.TurnChanged += OnTranscript;
        _draftTimer = DispatcherQueue.CreateTimer();
        _draftTimer.Interval = TimeSpan.FromMilliseconds(500);
        _draftTimer.IsRepeating = false;
        _draftTimer.Tick += async (_, _) => { try { await FlushDraftAsync(); } catch (Exception ex) { ShowError(ex.Message); } };
        _renderTimer = DispatcherQueue.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(80);
        _renderTimer.Tick += (_, _) => OnNativeRenderTimerTick();
        _renderTimer.Start();
        SizeChanged += (_, _) => RunUiCallback("Page.SizeChanged", UpdateResponsiveLayout);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialised) { if (!_selectionInputSmokeActive || !IsMessageFooterSmoke) await RefreshForExternalActivationAsync(); return; }
        _initialised = true;
        await CommandAsync("app.ready", new { });
        var navigationSmokeVisits = 0;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
        {
            try { navigationSmokeVisits = await VerifyMessageNavigationSmokeAsync(); }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-ui-failure.json"),
                    JsonSerializer.Serialize(new { error = exception.ToString() }));
                throw;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-ui-ready.json"), JsonSerializer.Serialize(new
        {
            renderer = "WinUI3", page = GetType().Name, sessionId = _session, ready = _session != Guid.Empty, navigationSmokeVisits,
            loadedMessageCount = _messages.Count, historyHasMore = _historyHasMore,
            deferredToolCount = _messages.Values.SelectMany(message => MessageSteps(message))
                .Count(step => step.TryGetProperty("detailsDeferred", out var value) && value.ValueKind == JsonValueKind.True),
        }), _lifetime.Token);
    }

    private static string S(JsonElement value, string name, string fallback = "") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? field.ValueKind == JsonValueKind.String ? field.GetString() ?? fallback : field.ToString() : fallback;
    private static JsonElement[] Items(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
    private static SolidColorBrush Brush(byte gray) => new(Color.FromArgb(255, gray, gray, gray));

    private static SolidColorBrush ThemeBrush(string key, byte fallback)
        => NativeThemeBrushes.Resource(key, fallback);

    private async Task<bool> CommandAsync(string type, object payload, Action<string>? onError = null)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var navigation = NativeNavigationState.IsNavigationCommand(type);
        var generation = navigation ? _navigationState.BeginNavigation() : _navigationState.Generation;
        var draftSession = _session;
        var draft = !_rendering ? (ActiveSubagent is null ? Composer.Text : _parentComposerDraft) : null;
        if (navigation) { _draftTimer.Stop(); UpdateComposerNavigationState(); }
        var arguments = JsonSerializer.SerializeToElement(payload, JsonOptions);
        Guid? requestSession = Guid.TryParse(S(arguments, "sessionId"), out var parsedSession) ? parsedSession : null;
        TaskCompletionSource? navigationCompletion = null;
        var precedingNavigation = _navigationTail;
        if (navigation)
        {
            navigationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _navigationTail = navigationCompletion.Task;
        }
        try
        {
            if (navigation)
            {
                // Only navigation is serialized. Long model/research operations remain independent.
                await precedingNavigation.WaitAsync(_lifetime.Token);
                if (!_navigationState.IsCurrent(generation)) return false;
                draft = await FinishDictationForNavigationAsync(draftSession, draft);
                if (draftSession != Guid.Empty && draft is not null)
                    await _assistantHost.SaveDraftAsync("desktop", draftSession, draft);
                if (!_navigationState.IsCurrent(generation)) return false;
            }
            // SQLite's async operations can complete synchronously. In particular,
            // publication snapshots must not execute their reads on the XAML thread.
            // EmitAsync remains the ordered dispatcher boundary for every reply.
            await Task.Run(() => _assistantHost.HandleAsync("desktop", new(2, type, requestId, arguments),
                (eventType, data, id) => EmitAsync(eventType, data, id, generation, onError, requestSession), _lifetime.Token), _lifetime.Token);
            if (navigation && !_navigationState.IsCurrent(generation)) return false;
            return !_failedRequests.Remove(requestId);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (onError is not null) onError(ex.Message);
            else if (_navigationState.IsCurrent(generation)) ShowError(ex.Message);
        }
        finally
        {
            _failedRequests.Remove(requestId);
            if (navigation)
            {
                _navigationState.CompleteNavigation(generation);
                UpdateComposerNavigationState();
                navigationCompletion!.TrySetResult();
            }
        }
        return false;
    }

    private Task<bool> NavigateAsync(string type, object payload) => CommandAsync(type, payload);

    private Task EmitAsync(string type, object payload, string? requestId, long? snapshotGeneration = null,
        Action<string>? onError = null, Guid? requestSession = null)
    {
        var json = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var generation = snapshotGeneration ?? _navigationState.Generation;
        // The unavailable-publication response has no session field; retain the request's owner.
        if (type == "publication.snapshot" && S(json, "sessionId").Length == 0 && requestSession.HasValue)
        {
            var scoped = json.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            scoped["sessionId"] = JsonSerializer.SerializeToElement(requestSession.Value);
            json = JsonSerializer.SerializeToElement(scoped, JsonOptions);
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed || !DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_disposed) return;
                if (type is "conversation.page" or "conversation.promptPage" or "conversation.blocks.page" or "subagent.blocks.page" or "tool.details" && !_navigationState.IsCurrent(generation)) return;
                if (NativeNavigationState.IsFullSnapshot(type)
                    && (!_navigationState.CanApplySnapshot(generation,
                        Guid.TryParse(S(json, "activeSessionId"), out var activeSession) ? activeSession : Guid.Empty,
                        _settings.Current.ActiveSessionId))) return;
                if (type == "host.error")
                {
                    if (requestId is not null) _failedRequests.Add(requestId);
                    if (onError is not null) onError(S(json, "message"));
                    else if (_navigationState.IsCurrent(generation)) ShowError(S(json, "message"));
                    return;
                }
                _lastUiEventType = type;
                _lastUiEventRevision = S(json, "revision", S(json, "conversationRevision"));
                ApplyEvent(type, json);
            }
            catch (Exception ex) { if (onError is not null) onError(ex.Message); else ShowError(ex.Message); }
            finally { completion.TrySetResult(); }
        })) completion.TrySetResult();
        return completion.Task;
    }

    private void ApplyEvent(string type, JsonElement data)
    {
        if (type == "tool.details") ObserveToolTabDetails(data);
        if (ApplyPagedHistoryEvent(type, data) || ApplySourcesEvent(type, data)) return;
        if (ApplySubagentEvent(type, data)) { RefreshOpenToolTabs(); return; }
        if (ApplyResearchEvent(type, data)) return;
        ObserveContinuationAdmission(type, data);
        if (type == "chat.started" && S(data, "sessionId") == _session.ToString()
            && data.TryGetProperty("userMessage", out var userMessage) && userMessage.ValueKind == JsonValueKind.Object)
        {
            _messages[S(userMessage, "id")] = userMessage;
            ReconcileLiveCaptionMessage();
            RenderMessages();
        }
        if (type == "host.error") { ShowError(S(data, "message")); return; }
        if (type == "queue.changed") { UpdateSidebarActivity(data); ObserveContinuationQueue(data); return; }
        if (type == "session.grouped")
        {
            // A peer's project/session edit updates the shared sidebar, while
            // the current conversation and its unsaved composer remain local.
            if (_snapshot.ValueKind != JsonValueKind.Object || S(data, "chatMode", _mode) != _mode) return;
            var merged = _snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            foreach (var field in new[] { "sessions", "sessionGroups" })
                if (data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array)
                    merged[field] = value.Clone();
            _snapshot = JsonSerializer.SerializeToElement(merged);
            RenderSidebar();
            SyncSessionTabs();
            return;
        }
        if (type == "speech.status") { ApplyMessageSpeechStatus(data); return; }
        if (type == "speech.progress") { ApplyMessageSpeechProgress(data); return; }
        if (type == "coding.changes") RefreshChangesReview(data);
        if (type is "state.snapshot" or "session.changed" or "document.changed")
        {
            if (data.TryGetProperty("activeSessionId", out var active))
            {
                var next = active.GetGuid();
                if (!ObserveChangesConversation(next, data)) return;
                var changed = next != _session;
                _snapshot = data;
                _session = next;
                ObserveWorkingPlan(data, changed);
                if (changed) { ResetConversationViews(); ResetHistoryPaging(); _scrollNewSessionToLatest = true; ClearToolSelection(); _contextUsed = 0; _contextLimit = 0; ResetChangesSummary(); }
                var newMode = S(data, "chatMode", "general");
                if (_mode != newMode) { ResetConversationViews(); ClearToolSelection(); }
                _mode = newMode;
                _running = _changeReceiptState.IsPromptPending(_session)
                    || (S(data, "isRunning") == "True" && !_finishedSessions.Contains(_session));
                ModeLabel.Text = _mode switch { "coding" => "Codex", "claudescience" => "Claude Science", _ => "ChatGPT" };
                ChatStatus = _running && !S(data, "runStatus").StartsWith("Modell generiert", StringComparison.Ordinal) ? S(data, "runStatus") + "  " + S(data, "runDetail") : "";
                _persistentAction = S(data, "selectedExtensionActionId");
                if (_persistentAction == BuiltInActionIds.DeepResearch) _persistentAction = null;
                if (!string.IsNullOrEmpty(_persistentAction) && string.IsNullOrEmpty(_selectedAction))
                {
                    var action = Items(data, "actionDescriptors").FirstOrDefault(a => S(a, "actionId") == _persistentAction);
                    _selectedAction = _persistentAction; UpdateSelectedToolChip(S(action, "displayName", _persistentAction));
                }
                if (ActiveSubagent is null) Composer.PlaceholderText = _mode switch { "coding" => "Leg einfach los", "claudescience" => "Stelle eine Forschungsfrage", _ => "Frag etwas" };
                _rendering = true;
                if (changed) Composer.Text = S(data, "draft");
                _rendering = false;
                ApplyMessageSnapshot(data, preserveLive: !changed);
                ReconcileLiveCaptionMessage();
                SyncSubagents(data, changed);
                ObserveThinkingProgress(data);
                UpdateCaptionChip();
                RenderSidebar();
                SyncSessionTabs();
                RenderMessages();
                RenderSources(data);
                var workspace = S(data, "workspacePath");
                WorkspaceText.Text = string.IsNullOrEmpty(workspace) ? "Kein Projekt ausgewählt" : Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                ToolTipService.SetToolTip(InspectorProjectRow, workspace);
                var modelId = _settings.Current.SelectedModel;
                UpdateContext(data);
                if (_reasoningModel != ModelRole + ":" + modelId) { _reasoningModel = ModelRole + ":" + modelId; _reasoning = null; _ = LoadReasoningAsync(); }
                RefreshComposerModelDisplay();
                if (data.TryGetProperty("changesSummary", out var changes)) RenderChanges(changes);
                SetRunning();
                SyncResearchSession();
                // Closed tool details and older pages intentionally omit URLs.
                // Load only the compact source index, independently of the tab.
                if (!_selectionInputSmokeActive && (changed || type == "state.snapshot"))
                    _ = LoadSourceIndexAsync(_session, ReadConversationRevision(data, "conversationRevision"));
            }
            return;
        }
        var eventSession = S(data, "sessionId", S(data, "activeSessionId"));
        if (eventSession.Length > 0 && !string.Equals(eventSession, _session.ToString(), StringComparison.OrdinalIgnoreCase)) return;
        if (type == "workingPlan.changed") { ObserveWorkingPlan(data); return; }
        if (type == "conversation.toolStepChanged") { ApplyNativeMessageDelta(data); ObserveThinkingProgress(data); return; }
        UpdateContext(data);
        ObserveThinkingProgress(data);
        if (type == "reasoning.snapshot" && S(data, "role") + ":" + S(data, "modelId") == _reasoningModel) { _reasoning = data.Deserialize<ComposerReasoningOptions>(JsonOptions); RefreshComposerModelDisplay(); }
        if (type == "conversation.snapshot")
        {
            if (!ObserveChangesConversation(_session, data)) return;
            ObserveWorkingPlan(data);
            ApplyMessageSnapshot(data, preserveLive: true);
            ReconcileLiveCaptionMessage();
            SyncSubagents(data, sessionChanged: false);
            RenderSessionTabs();
            RenderMessages();
            if (data.TryGetProperty("changesSummary", out var conversationChanges)) RenderChanges(conversationChanges);
        }
        if (type == "conversation.messageCommitted" || type is "chat.started" or "chat.completed" or "chat.cancelled" or "chat.failed")
        {
            if (type == "chat.started" && data.TryGetProperty("message", out var startedMessage)
                && Guid.TryParse(S(startedMessage, "id"), out var startedId))
            {
                var startedRun = Guid.TryParse(S(data, "changesRunId"), out var parsedRun) ? parsedRun : Guid.Empty;
                if (!_changeReceiptState.ObserveStarted(_session, startedId, ReadConversationRevision(data), startedRun)) return;
                ResetChangesSummary();
            }
            if (data.TryGetProperty("message", out var message))
            {
                var id = S(message, "id");
                _messages[id] = PreserveNewerLiveMessage(message);
                if (type == "chat.started" && data.TryGetProperty("streamGeneration", out var generation))
                    ProjectionFor(_messages[id]).BeginGeneration(generation.GetString() ?? "");
                RenderMessages();
            }
        }
        if (type == "chat.delta") ApplyNativeMessageDelta(data);
        if (type is "chat.started" or "chat.queued") { _finishedSessions.Remove(_session); _running = true; SetRunning(); }
        if (type is "chat.completed" or "chat.cancelled" or "chat.failed")
        {
            _finishedSessions.Add(_session); _running = false; SetRunning(); ChatStatus = "";
            ClearConversationThinkingState(); RefreshThinkingIndicators();
            _ = RefreshForExternalActivationAsync();
            if (type == "chat.failed") ShowError(S(data, "error", "Die Anfrage ist fehlgeschlagen."));
        }
        if (type == "status.changed")
        {
            var status = S(data, "runStatus");
            var terminal = status is "Fertig" or "Abgebrochen" or "Fehlgeschlagen";
            if (terminal) { _finishedSessions.Add(_session); _running = false; SetRunning(); ClearConversationThinkingState(); RefreshThinkingIndicators(); }
            ChatStatus = status.StartsWith("Modell generiert", StringComparison.Ordinal) ? "" : terminal ? "" : status + "  " + S(data, "runDetail");
            var id = S(data, "messageId");
            if (data.TryGetProperty("toolStep", out var step) && step.ValueKind == JsonValueKind.Object && _messages.TryGetValue(id, out var current))
            {
                ApplyNativeMessageDelta(data);
            }
        }
        if (type == "coding.changes") RenderChanges(data);
        RefreshResearchAfterRun(type);
        UpdateResearchRuntimeChrome();
    }

    private static DateTimeOffset MessageCreatedAt(JsonElement message) => DateTimeOffset.TryParse(S(message, "createdAt"), out var time) ? time : DateTimeOffset.MinValue;

    private void ApplyMessageSnapshot(JsonElement data, bool preserveLive)
    {
        // A snapshot read can precede an already delivered live delta. Retain
        // its newer text while still accepting snapshot metadata and artifacts.
        ObserveToolStepWindows(data);
        var incoming = Items(data, "messages");
        ObserveEmptyPromptTimelineSnapshot(data);
        if (preserveLive) incoming = incoming.Select(PreserveNewerLiveMessage).ToArray();
        var hasPage = data.TryGetProperty("historyPage", out var page);
        var keepOlderPages = preserveLive && hasPage && page.TryGetProperty("hasMore", out var more)
            && more.ValueKind == JsonValueKind.True;
        if (!keepOlderPages) _messages.Clear();
        foreach (var message in incoming) _messages[S(message, "id")] = message;
        if (hasPage && (!keepOlderPages || !_historyPagingInitialized))
            ApplyHistoryPageCursor(page);
    }

    private JsonElement PreserveNewerLiveMessage(JsonElement incoming)
    {
        if (!_messages.TryGetValue(S(incoming, "id"), out var current)
            || S(current, "sessionId") != S(incoming, "sessionId")) return incoming;
        var currentRevision = ReadConversationRevision(current, "revision");
        var incomingRevision = ReadConversationRevision(incoming, "revision");
        var merge = NativeMessageSnapshotPolicy.Compare(current, incoming, MessageUpdatedAt(current));
        if (!merge.PreservePayload) return incoming;
        var merged = incoming.Deserialize<Dictionary<string, JsonElement>>()!;
        foreach (var field in new[] { "content", "toolSteps", "updatedAt", "contextSummary" })
            if (current.TryGetProperty(field, out var value)) merged[field] = value;
        if (merge.PreserveLifecycle)
            foreach (var field in new[] { "status", "error" })
                if (current.TryGetProperty(field, out var value)) merged[field] = value;
        merged["content"] = JsonSerializer.SerializeToElement(MessageContent(current));
        merged["toolSteps"] = JsonSerializer.SerializeToElement(MessageSteps(current));
        merged["updatedAt"] = JsonSerializer.SerializeToElement(MessageUpdatedAt(current));
        var liveProjection = ProjectionFor(current);
        merged["toolStepWindow"] = JsonSerializer.SerializeToElement(new { totalSteps = liveProjection.TotalSteps,
            hasMore = liveProjection.FirstOrdinal > 0, beforeOrdinal = liveProjection.FirstOrdinal });
        merged["revision"] = JsonSerializer.SerializeToElement(Math.Max(currentRevision, incomingRevision));
        // Artifact ingestion can finish independently of a text snapshot. Keep
        // new snapshot artifacts without discarding already delivered originals.
        if (current.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
            merged["artifacts"] = JsonSerializer.SerializeToElement(Items(incoming, "artifacts")
                .Concat(artifacts.EnumerateArray()).GroupBy(artifact => S(artifact, "id"), StringComparer.Ordinal)
                .Select(group => group.Last()).ToArray());
        return JsonSerializer.SerializeToElement(merged);
    }

    private void ReconcileLiveCaptionMessage()
    {
        if (_captionSessionId == _session && _captionMessageId is Guid captionId && _captionMessage.ValueKind == JsonValueKind.Object)
            _messages[captionId.ToString()] = _captionMessage;
    }

    private static StackPanel MessageBody(Border bubble) => bubble.Child is StackPanel panel ? panel : (StackPanel)((Grid)bubble.Child).Children[1];

    private void RenderMessages()
    {
        RefreshOpenToolTabs();
        _messagesDirty = true;
    }
    // JsonElement equality is document/position identity. Stable messages need
    // neither JSON re-serialization nor native footer/tooltip updates on a delta.
    private readonly Dictionary<string, JsonElement> _renderedMessageSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (JsonElement Source, string Signature)> _sourceMessageSignatures = new(StringComparer.Ordinal);
    private void ResetConversationViews()
    {
        _targetedPromptWindows.Clear();
        _promptTailRestoreRequests.Clear();
        ClearToolTabs();
        CancelPromptTimelineNavigation();
        _autoScrollGesture.Reset();
        _pendingConversationViewportAnchor = null;
        _readingToolConversation = null;
        _conversationScrollIntent++;
        _toolDetailGeneration++;
        _conversationSelection?.Clear();
        // WinUI elements have exactly one parent. Footer controls and context
        // menus belong to the same visual lifetime as their message bubbles.
        MessagesPanel.Children.Clear();
        _messageViews.Clear();
        _messageStepStarts.Clear();
        _lastConversationOffset = 0;
        _renderedMessageSources.Clear();
        _sourceMessageSignatures.Clear();
        _liveMessageProjections.Clear();
        _renderedMessageVersions.Clear();
        _toolStepWindows.Clear();
        _blockPagesLoading.Clear();
        _messageAuxiliarySteps.Clear();
        foreach (var state in _virtualMessages.Values) state.Generation++;
        _virtualMessages.Clear();
        _messageBlocks.Clear();
        _thinkingIndicators.Clear();
        _messageActionViews.Clear();
    }

    private bool _scrollNewSessionToLatest;
    private bool _sessionScrollQueued;

    private void QueueSessionScrollToLatest()
    {
        if (_sessionScrollQueued || !_scrollNewSessionToLatest || !CanFollowConversationTail || _disposed || !IsConversationActiveView) return;
        _sessionScrollQueued = true;
        var session = _session;
        var owner = ConversationViewKey;
        var scrollIntent = _conversationScrollIntent;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RunUiCallback("Session.ScrollLatest", () =>
        {
            _sessionScrollQueued = false;
            if (_disposed || session != _session || owner != ConversationViewKey
                || scrollIntent != _conversationScrollIntent || !_scrollNewSessionToLatest || !CanFollowConversationTail || !IsConversationActiveView) return;
            ScrollConversationToLatest();
        }));
    }

    private void RenderMessagesNow(bool deferLayout = false)
    {
        RefreshOpenToolTabs();
        if (!IsConversationActiveView) { _messagesDirty = true; return; }
        ReconcileLiveCaptionMessage();
        if (_conversationSelection?.IsSelecting == true)
        {
            // Keep every paragraph and its viewport position stable throughout
            // a character selection, including a drag across several messages.
            // The model still streams into _messages and copy/audio footers stay current.
            foreach (var message in DisplayMessages.Values) UpdateMessageActions(S(message, "id"), message);
            return;
        }
        RenderSources(_snapshot);
        var follow = ShouldFollowConversationTail;
        if (follow) _scrollNewSessionToLatest = true;
        var viewportAnchor = follow ? null : CaptureConversationViewportAnchor();
        var index = 0;
        foreach (var stale in _messageViews.Keys.Where(id => !IsKnownConversationMessage(id)).ToArray())
        { MessagesPanel.Children.Remove(_messageViews[stale].View); _messageViews.Remove(stale); _renderedMessageSources.Remove(stale); _messageBlocks.Remove(stale); _messageActionViews.Remove(stale); _thinkingIndicators.Remove(stale); _thinkingStates.Remove(stale); }
        foreach (var hidden in _messageViews.Where(pair => !DisplayMessages.ContainsKey(pair.Key)).Select(pair => pair.Value.View).ToArray())
            MessagesPanel.Children.Remove(hidden);
        WelcomePanel.Visibility = DisplayMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var message in OrderedDisplayMessages())
        {
            var id = S(message, "id");
            if (_renderedMessageSources.TryGetValue(id, out var renderedSource) && renderedSource.Equals(message)
                && _renderedMessageVersions.GetValueOrDefault(id) == MessageVersion(message)
                && _messageViews.TryGetValue(id, out var unchanged))
            {
                if (MessagesPanel.Children.IndexOf(unchanged.View) != index)
                { MessagesPanel.Children.Remove(unchanged.View); MessagesPanel.Children.Insert(index, unchanged.View); }
                index++; continue;
            }
            UpdateMessageActions(id, message);
            var signature = MessageVersion(message).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_messageViews.TryGetValue(id, out var cached) && cached.Signature == signature)
            {
                _renderedMessageSources[id] = message;
                _renderedMessageVersions[id] = MessageVersion(message);
                if (MessagesPanel.Children.IndexOf(cached.View) != index)
                { MessagesPanel.Children.Remove(cached.View); MessagesPanel.Children.Insert(index, cached.View); }
                index++; continue;
            }
            // Mutating a selected Run makes WinUI expand/shift character ranges.
            // Retain this bubble until the user clears its range; incoming content
            // and footer controls continue to update in the committed snapshot.
            if (cached.View is not null && _conversationSelection?.PreserveSelectionWithin(cached.View) == true)
            { index++; continue; }
            var bubble = cached.View;
            if (bubble is null)
            {
                var user = S(message, "role") == "user";
                bubble = new Border { Child = new StackPanel { Spacing = 12 }, HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                    Padding = user ? new Thickness(16, 12, 16, 12) : new Thickness(0),
                    Background = user ? ThemeBrush("MissumLayerStrongBrush", 45) : new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new(user ? 18 : 0) };
                if (!user)
                {
                    var messageBody = (StackPanel)bubble.Child;
                    var layout = new Grid { ColumnSpacing = 12 };
                    layout.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
                    layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                    var childAgent = ActiveSubagent;
                    layout.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new(14), VerticalAlignment = VerticalAlignment.Top,
                        Background = childAgent is null || childAgent.IsDeepResearch ? ThemeBrush("MissumAccentSubtleBrush", 42) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Child = childAgent is null || childAgent.IsDeepResearch
                            ? new FontIcon { Glyph = childAgent?.IsDeepResearch == true ? "\uE721" : "\uE99A", FontSize = 15,
                                Foreground = NativeIconPalette.BrushFor(childAgent?.IsDeepResearch == true ? "research" : "navigation") }
                            : SubagentIcon(childAgent.AgentId, 26, childAgent.PlanetIndex) });
                    bubble.Child = null;
                    Grid.SetColumn(messageBody, 1); layout.Children.Add(messageBody); bubble.Child = layout;
                }
                bubble.ContextFlyout = MessageContextMenuFor(id, message);
                MessagesPanel.Children.Insert(index, bubble);
            }
            if (MessagesPanel.Children.IndexOf(bubble) != index)
            { MessagesPanel.Children.Remove(bubble); MessagesPanel.Children.Insert(index, bubble); }
            UpdateMessageBlocks(id, message, MessageBody(bubble));
            _conversationSelection?.TrackSelectionWithin(bubble);
            _messageViews[id] = (signature, bubble); index++;
            _renderedMessageSources[id] = message;
            _renderedMessageVersions[id] = MessageVersion(message);
        }
        if (deferLayout) QueueConversationLayoutProjection(follow, viewportAnchor);
        else
        {
            ConversationContent.UpdateLayout();
            RenderPromptTimeline(layoutCurrent: true);
            if (follow) ScrollConversationToLatest();
            else RestoreConversationViewportAnchor(viewportAnchor);
            QueueSessionScrollToLatest();
            SyncOuterScroll();
        }
    }

    private bool _conversationLayoutProjectionQueued;
    private ConversationViewportAnchor? _pendingConversationViewportAnchor;
    private void QueueConversationLayoutProjection(bool follow, ConversationViewportAnchor? viewportAnchor = null)
    {
        if (!IsConversationActiveView) return;
        if (follow) _scrollNewSessionToLatest = true;
        _pendingConversationViewportAnchor ??= viewportAnchor;
        if (_conversationLayoutProjectionQueued) return;
        _conversationLayoutProjectionQueued = true;
        var owner = ConversationViewKey;
        var scrollIntent = _conversationScrollIntent;
        // Streaming must not synchronously force measure/arrange of the entire
        // scientific conversation. Use the completed framework layout instead.
        void Project(object? sender, object args)
        {
            ConversationContent.LayoutUpdated -= Project;
            _conversationLayoutProjectionQueued = false;
            var anchor = _pendingConversationViewportAnchor;
            _pendingConversationViewportAnchor = null;
            if (_disposed || owner != ConversationViewKey || !IsConversationActiveView) return;
            RunUiCallback("Conversation.LayoutProjection", () =>
            {
                RefreshVirtualConversationViewport();
                RenderPromptTimeline(layoutCurrent: true);
                if (ShouldFollowConversationTail && scrollIntent == _conversationScrollIntent
                    && !_messageWindowLoading && _conversationSelection?.IsSelecting != true)
                    ScrollConversationToLatest();
                else RestoreConversationViewportAnchor(anchor);
                QueueSessionScrollToLatest();
                SyncOuterScroll();
            });
        }
        ConversationContent.LayoutUpdated += Project;
    }
    private void RenderSources(JsonElement data)
    {
        var signature = ConversationViewKey + "|"
            // Reasoning/token updates cannot change source links. Including those
            // steps here rebuilt every source button for each streamed fragment.
            + string.Join("|", OwnerSourceMessages().Select(SourceMessageSignature))
            + string.Join("|", Items(data, "documents").Concat(Items(data, "attachments")).Select(item => S(item, "id")));
        if (_sourcesSignature == signature) return;
        _sourcesSignature = signature;
        RenderWebSources();
    }

    private string SourceMessageSignature(JsonElement message)
    {
        var id = S(message, "id");
        if (_sourceMessageSignatures.TryGetValue(id, out var cached) && cached.Source.Equals(message)) return cached.Signature;
        var signature = string.Join("|", MessageSteps(message).Where(IsSourceToolStep)
            .Select(step => S(step, "id") + S(step, "updatedAt")
                + S(step, "inputJson").GetHashCode(StringComparison.Ordinal) + S(step, "outputJson").GetHashCode(StringComparison.Ordinal)));
        _sourceMessageSignatures[id] = (message, signature);
        return signature;
    }

    // File receipts update the compact output entry and any open review tab.
    // The full file list belongs exclusively to the review, never after the cursor.
    private void RenderChanges(JsonElement summary) => UpdateChangesSummary(summary);

    public async Task FlushDraftAsync()
    {
        _draftTimer.Stop();
        if (_session != Guid.Empty && !_rendering) await _assistantHost.SaveDraftAsync("desktop", _session, ActiveSubagent is null ? Composer.Text : _parentComposerDraft ?? "");
    }
    public async Task RefreshForExternalActivationAsync()
    {
        // Showing the previously hidden QA window raises Loaded again. Keep its
        // synthetic gesture fixture rather than reloading the empty test repository.
        if (_selectionInputSmokeActive && IsMessageFooterSmoke) return;
        var externalNavigation = !_navigationState.IsNavigating && _settings.Current.ActiveSessionId is { } active && active != _session;
        var generation = externalNavigation ? _navigationState.BeginNavigation() : _navigationState.Generation;
        if (externalNavigation) UpdateComposerNavigationState();
        try
        {
            if (externalNavigation)
            {
                await FinishDictationForNavigationAsync(_session, Composer.Text);
                await FlushDraftAsync();
            }
            await EmitAsync("state.snapshot", await _coordinator.BuildSnapshotAsync(_lifetime.Token), null, generation);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { if (externalNavigation) { _navigationState.CompleteNavigation(generation); UpdateComposerNavigationState(); } }
    }
    private async void OnModeClick(object sender, RoutedEventArgs e) { await NavigateAsync("mode.switch", new { chatMode = ((MenuFlyoutItem)sender).Tag.ToString() }); }
    private void OnSearchClick(object sender, RoutedEventArgs e) { SearchBox.Visibility = SearchBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; SearchBox.Focus(FocusState.Programmatic); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (_initialised) RenderSidebar(); }
    private void OnDraftChanged(object sender, TextChangedEventArgs e) { if (_initialised) SetRunning(); if (!_rendering && _initialised) { _draftTimer.Stop(); _draftTimer.Start(); } }
    private void OnComposerGotFocus(object sender, RoutedEventArgs e)
    {
        _conversationSelection?.Clear();
        ComposerSurface.BorderBrush = ThemeBrush("MissumAccentBrush", 0xB0);
    }
    private void OnComposerLostFocus(object sender, RoutedEventArgs e) =>
        ComposerSurface.BorderBrush = ThemeBrush("MissumStrokeBrush", 0x3D);
    public void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        Sidebar.Visibility = Sidebar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new(Sidebar.Visibility == Visibility.Visible ? 288 : 0);
        ApplyJoinedFrameCorners();
    }
    private void ApplyJoinedFrameCorners()
    {
        Sidebar.CornerRadius = new CornerRadius(0);
        Sidebar.BorderThickness = new Thickness(0, 1, 1, 0);
        AssistantFrame.CornerRadius = new CornerRadius(0);
        AssistantFrame.BorderThickness = new Thickness(0, 1, 0, 0);
    }
    public async void OnToggleInspector(object sender, RoutedEventArgs e)
    {
        try { await SetInspectorVisibleAsync(Inspector.Visibility != Visibility.Visible); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
    }

    private async Task SetInspectorVisibleAsync(bool visible)
    {
        _inspectorRequested = visible;
        UpdateResponsiveLayout();
        await _settings.UpdateAsync(settings => settings with { AssistantOutputsVisible = visible });
    }
    private bool _syncingScroll;
    private void OnConversationSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RunUiCallback("Conversation.SizeChanged", () =>
        {
            if (_conversationLayoutSyncing || BodyGrid.Visibility != Visibility.Visible || e.NewSize.Width <= 0) return;
            _conversationLayoutSyncing = true;
            try
            {
                var width = Math.Max(0, e.NewSize.Width - ConversationScroll.Padding.Left - ConversationScroll.Padding.Right);
                if (double.IsNaN(ConversationContent.Width) || Math.Abs(ConversationContent.Width - width) > .5) ConversationContent.Width = width;
                SyncOuterScroll();
            }
            finally { _conversationLayoutSyncing = false; }
        });
    }
    private void OnConversationViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        RunUiCallback("Conversation.ViewChanged", () =>
        {
            SyncOuterScroll();
            RefreshVirtualConversationViewport();
            UpdatePromptTimelineSelection();
            if (_resumeTailOnManualScroll && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset <= 2
                && _conversationSelection?.IsSelecting != true)
            { _readingToolConversation = null; _scrollNewSessionToLatest = true; _resumeTailOnManualScroll = false; }
            if (!e.IsIntermediate) _resumeTailOnManualScroll = false;
            if (ShouldFollowConversationTail && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset > 1)
                QueueSessionScrollToLatest();
            if (!e.IsIntermediate && !LoadMessageBlocksOnUpwardScroll() && ConversationScroll.VerticalOffset < 240 && !_scrollNewSessionToLatest)
                _ = LoadEarlierMessagesAsync();
        });
    }
    private void SyncOuterScroll()
    {
        if (_disposed || _syncingScroll || OuterChatScrollBar is null) return;
        _syncingScroll = true;
        try
        {
            OuterChatScrollBar.Maximum = ConversationScroll.ScrollableHeight;
            OuterChatScrollBar.ViewportSize = ConversationScroll.ViewportHeight;
            OuterChatScrollBar.LargeChange = ConversationScroll.ViewportHeight;
            OuterChatScrollBar.Value = ConversationScroll.VerticalOffset;
            OuterChatScrollBar.Visibility = ConversationScroll.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScrollToBottomButton.Visibility = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset > 90 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncingScroll = false; }
    }
    private void OnOuterScrollChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScroll) return;
        OnToolDisclosureInteraction();
        if (e.NewValue > e.OldValue && e.NewValue >= ConversationScroll.ScrollableHeight - 1)
        { _readingToolConversation = null; _scrollNewSessionToLatest = true; }
        ConversationScroll.ChangeView(null, e.NewValue, null, true);
    }
    private void OnScrollToBottom(object sender, RoutedEventArgs e)
    {
        CancelPromptTimelineNavigation();
        _readingToolConversation = null;
        _scrollNewSessionToLatest = true;
        _autoScrollGesture.Reset();
        _resumeTailOnManualScroll = false;
        _conversationScrollIntent++;
        var latest = OrderedDisplayMessages().LastOrDefault();
        var latestId = S(latest, "id");
        if (ActiveSubagent is null && _targetedPromptWindows.Remove(latestId))
            _ = RestorePromptTimelineTailAsync(latestId, ConversationViewKey, _conversationScrollIntent);
        ConversationContent.UpdateLayout();
        ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
        SyncOuterScroll();
    }
    private void OnAssistantPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_activeResearchSessionId == _session || _activeSourcesSession == _session
            || _activeSubagentOverviewSession == _session || _activeWorkingPlanSession == _session) return;
        // Only unhandled wheel input reaches this frame. Nested text/diff viewers
        // consume their own scrolling. The independent outputs sidebar never
        // forwards a wheel gesture into the conversation at its scroll limits.
        var point = e.GetCurrentPoint(AssistantFrame);
        if (point.Properties.IsHorizontalMouseWheel || point.Properties.MouseWheelDelta == 0) return;
        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control) || modifiers.HasFlag(VirtualKeyModifiers.Shift)) return;
        var scroll = ReviewScroll.Visibility == Visibility.Visible ? ReviewScroll : ConversationScroll;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != AssistantFrame; source = VisualTreeHelper.GetParent(source))
            if (source == scroll || source == Composer || source == OuterChatScrollBar || source == ComposerChipsScroll || source == Inspector) return;
        if (scroll == ConversationScroll && TryHandleFollowingWheel(point.Properties.MouseWheelDelta * 1.2))
        { e.Handled = true; return; }
        if (scroll == ConversationScroll)
        { OnToolDisclosureInteraction(); _resumeTailOnManualScroll = point.Properties.MouseWheelDelta < 0; }
        var offset = Math.Clamp(scroll.VerticalOffset - point.Properties.MouseWheelDelta * 1.2, 0, scroll.ScrollableHeight);
        if (Math.Abs(offset - scroll.VerticalOffset) < .1) return;
        scroll.ChangeView(null, offset, null, true);
        e.Handled = true;
    }
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => RunUiCallback("Body.SizeChanged", UpdateResponsiveLayout);
    private bool _inspectorRequested;
    private void UpdateResponsiveLayout()
    {
        if (Inspector is null || InspectorColumn is null || AssistantFrame is null || InspectorToggle is null) return;
        var available = NativeInspectorLayout.HasRoom(AssistantFrame.ActualWidth);
        var visible = available && _inspectorRequested;
        var width = visible ? NativeInspectorLayout.SidebarWidth : 0;
        if (InspectorColumn.Width.Value != width) InspectorColumn.Width = new(width);
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (Inspector.Visibility != visibility) Inspector.Visibility = visibility;
        InspectorToggle.IsEnabled = available;
        var label = !available ? "Ausgaben bei größerem Fenster verfügbar" : visible ? "Ausgaben schließen" : "Ausgaben öffnen";
        ToolTipService.SetToolTip(InspectorToggle, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(InspectorToggle, label);
        RefreshInspectorWorkingPlan();
        RefreshInspectorContext();
    }
    private void SetRunning()
    {
        RefreshWorkingPlanTab();
        var hasText = !string.IsNullOrWhiteSpace(Composer.Text);
        var captionRunning = _captionActive && _captionSessionId == _session;
        var preparingContinuation = IsContinuationPreparing;
        var stop = preparingContinuation || (!hasText && (_running || _speaking || captionRunning));

        ModelButton.IsEnabled = ActiveSubagent is null;
        SendIcon.Glyph = stop ? "\uE71A" : "\uE74A";
        var label = ActiveSubagent?.IsDeepResearch == true ? "Deep Research · Steuerung im Hauptchat"
            : preparingContinuation ? "Vorbereitung stoppen" : stop ? "Aktivität stoppen" : _running ? "Antwort umlenken" : "Nachricht senden";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SendButton, label);
        ToolTipService.SetToolTip(SendButton, label);
        UpdateSidebarActivity();
        RefreshContinuationSteps();
    }
    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();
    private async Task SendAsync()
    {
        if (!_navigationState.CanEditComposer || ActiveSubagent is not null || _disposed || _sendPending) return;
        if (_dictationCapture is not null)
        {
            var dictationOwner = _session;
            _sendPending = true;
            try { await StopNativeDictationAsync(); }
            finally { _sendPending = false; }
            _dictationSession = null;
            if (_disposed || !_navigationState.CanEditComposer || _session != dictationOwner) return;
        }
        if (IsContinuationPreparing)
        {
            await CommandAsync("chat.cancel", new { sessionId = _session });
            return;
        }
        var prompt = Composer.Text.Trim();
        if (prompt.Length == 0 && _captionActive && _captionSessionId == _session) { OnStopCaptions(this, new RoutedEventArgs()); return; }
        if (_running)
        {
            if (prompt.Length == 0) { await CommandAsync("chat.cancel", new { sessionId = _session }); return; }
            var steeringSession = _session;
            var steeringDraft = Composer.Text;
            var inputId = Guid.NewGuid().ToString("N");
            _sendPending = true;
            _draftTimer.Stop(); Composer.Text = "";
            OnScrollToBottom(this, new RoutedEventArgs());
            try
            {
                if (!await CommandAsync("chat.steer", new { sessionId = steeringSession, prompt, inputId }))
                {
                    if (_session == steeringSession && Composer.Text.Length == 0) Composer.Text = steeringDraft;
                }
                else await RefreshForExternalActivationAsync();
            }
            finally { _sendPending = false; SetRunning(); }
            return;
        }
        if (_speaking && prompt.Length == 0) { await StopMessageSpeechAsync(); return; }
        if (prompt.Length == 0) return;
        var draft = Composer.Text;
        var session = _session;
        var selectedAction = _selectedAction == BuiltInActionIds.DeepResearch ? null : _selectedAction;
        var mode = _mode;
        _sendPending = true;
        try
        {
            if (!await EnsureRequiredMediaCaptureAsync(session, prompt, selectedAction)) return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowError(exception.Message);
            return;
        }
        finally { _sendPending = false; }
        // A native picker is modal, but the active session can still change through
        // an external activation. Never clear or send a draft owned by the new session.
        if (_disposed || _session != session || !string.Equals(Composer.Text, draft, StringComparison.Ordinal)) return;

        if (!string.IsNullOrEmpty(_composerInstruction)) prompt = _composerInstruction + "\n\n" + prompt;
        OnScrollToBottom(this, new RoutedEventArgs());
        var promptEpoch = _changeReceiptState.BeginPrompt(session);
        ResetChangesSummary();
        _draftTimer.Stop(); _rendering = true; Composer.Text = ""; _rendering = false;
        _finishedSessions.Remove(session); _running = true; SetRunning(); _chatErrors.Remove(session); RefreshChatNotices(); ChatStatus = "Anfrage wird vorbereitet …";
        var deepResearch = mode == "claudescience" && string.IsNullOrEmpty(selectedAction);
        if (!await CommandAsync("chat.send", new { sessionId = session, prompt, extensionActionId = deepResearch ? null : selectedAction, deepResearch, deepResearchProfile = deepResearch ? "auto" : null }))
        {
            _changeReceiptState.CancelPrompt(session, promptEpoch);
            if (_session == session)
            {
                _running = false; SetRunning();
                if (Composer.Text.Length == 0) { Composer.Text = draft; await FlushDraftAsync(); }
                _ = RefreshForExternalActivationAsync();
            }
        }
    }
    private void UpdateComposerNavigationState()
    {
        if (_disposed) return;
        RefreshWorkingPlanTab();
        ComposerInteractionHost.IsEnabled = _navigationState.CanEditComposer && ActiveSubagent is null;
        Composer.IsReadOnly = !_navigationState.CanEditComposer || ActiveSubagent is not null;
    }
    private async Task<string?> FinishDictationForNavigationAsync(Guid sessionId, string? draft)
    {
        if (_dictationSession != sessionId) return draft;
        await StopNativeDictationAsync();
        var transcript = _dictationFinalText ?? _microphone.Current.PartialTranscript;
        if (!string.IsNullOrWhiteSpace(transcript)) draft = (_dictationPrefix + " " + transcript).Trim();
        _dictationSession = null;
        // Publish the final captured text before awaiting Stop, so a newer queued
        // navigation captures this same draft instead of overwriting it with an older one.
        if (_session == sessionId && draft is not null)
        {
            _rendering = true; Composer.Text = draft; _rendering = false;
        }
        return draft;
    }
    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { e.Handled = true; if (!string.IsNullOrWhiteSpace(Composer.Text)) await SendAsync(); }
    }
    private async void OnAddProject(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            if (!Directory.Exists(folder.Path)) { ShowError("Der Projektordner existiert nicht."); return; }
            await NavigateAsync("session.projectCreate", new { workspacePath = folder.Path, chatMode = _mode });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void ClearToolSelection() { _selectedAction = null; _composerInstruction = null; _persistentAction = null; SelectedToolChip.Visibility = Visibility.Collapsed; }
    private async void OnClearTool(object sender, RoutedEventArgs e)
    {
        if (_composerToolSelectionBusy) return;
        var session = _session;
        _composerToolSelectionBusy = true;
        SelectedToolChip.IsEnabled = false;
        try
        {
            if (!string.IsNullOrEmpty(_persistentAction) && !await CommandAsync("action.invoke", new { actionId = _persistentAction, sessionId = session, enabled = false })) return;
            if (_session != session || _disposed) return;
            ClearToolSelection(); await RefreshForExternalActivationAsync();
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { _composerToolSelectionBusy = false; SelectedToolChip.IsEnabled = true; }
    }
    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        ShowComposerTools();
    }
    private async void OnAttach(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
            var files = await picker.PickMultipleFilesAsync();
            foreach (var file in files)
            {
                using var stream = await file.OpenStreamForReadAsync();
                if (_coordinator.SupportedDocumentExtensions.Contains(Path.GetExtension(file.Name)))
                    await _coordinator.ImportDocumentAsync(_session, file.Name, stream, _lifetime.Token);
                else await _coordinator.ImportAttachmentAsync(_session, file.Name, file.ContentType, stream, _lifetime.Token);
            }
            await SetInspectorVisibleAsync(true);
            await RefreshForExternalActivationAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private static string EffortLabel(string? effort) => effort switch
    {
        "none" => "Ohne", "on" => "An", "minimal" => "Minimal", "low" => "Niedrig", "medium" => "Mittel",
        "high" => "Hoch", "xhigh" => "Sehr hoch", "max" => "Maximum", "ultra" => "Ultra", _ => effort ?? "Automatisch"
    };
    private void UpdateContext(JsonElement data)
    {
        var changed = false;
        if (double.TryParse(S(data, "contextUsed"), out var used)) { _contextUsed = used; changed = true; }
        if (double.TryParse(S(data, "contextLimit"), out var limit)) { _contextLimit = limit; changed = true; }
        if (!changed) return;
        if (!_running) RefreshContextDisplay();
    }

    private void RefreshContextDisplay()
    {
        var used = DisplayContextUsed;
        var limit = DisplayContextLimit;
        var value = limit > 0 ? Math.Clamp(used / limit * 100, 0, 100) : 0;
        if (!ContextRing.Value.Equals(value)) ContextRing.Value = value;
        var description = limit > 0 ? $"Kontext: {used:N0} / {limit:N0} Token ({value:0.0} %)" : "Kontext noch nicht verfügbar";
        if (!Equals(ContextToolTip.Content, description)) ContextToolTip.Content = description;
        if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(ContextIndicator) != description)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ContextIndicator, description);
        RefreshInspectorContext();
    }
    private void OnContextPointerEntered(object sender, PointerRoutedEventArgs e) => ContextToolTip.IsOpen = true;
    private void OnContextPointerExited(object sender, PointerRoutedEventArgs e) => ContextToolTip.IsOpen = false;
    private void OnContextClicked(object sender, RoutedEventArgs e) => ContextToolTip.IsOpen = true;
    private async Task LoadReasoningAsync()
    {
        var role = ModelRole;
        var modelId = role == "coding" ? _settings.Current.SelectedModel : _settings.Current.SelectedModel;
        if (string.IsNullOrEmpty(modelId)) return;
        try
        {
            var options = await App.Current.GetService<MissumAiAssistantService>().GetReasoningOptionsAsync(modelId, role, _lifetime.Token);
            if (_disposed || _reasoningModel != role + ":" + modelId) return;
            // A peer can save a shared choice while this status request is in
            // flight. Never repaint an older selection over that notification.
            var savedEffort = MissumAiAssistantService.StoredReasoning(_settings.Current, modelId, role);
            if (savedEffort is not null && options.Levels.Contains(savedEffort)) options = options with { Selected = savedEffort };
            if (!_running) UpdateContext(JsonSerializer.SerializeToElement(new { contextLimit = options.SelectedContextTokens }));
            _reasoning = options;
            RefreshComposerModelDisplay();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_disposed && _reasoningModel == role + ":" + modelId)
            { _reasoning = null; RefreshComposerModelDisplay(); }
        }
    }
    private async void OnMicrophone(object sender, RoutedEventArgs e)
    {
        if (!_navigationState.CanEditComposer || _disposed) return;
        try
        {
            if (_microphone.Current.IsRecording) { await StopNativeDictationAsync(); _dictationSession = null; ChatStatus = ""; }
            else { _dictationSession = _session; _dictationPrefix = Composer.Text; await StartNativeDictationAsync(); ChatStatus = "Diktieren …"; }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void OnTranscript(object? sender, MicrophoneTurnSnapshot turn)
    {
        var owner = _dictationSession;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && owner.HasValue && owner == _dictationSession && owner == _session && _navigationState.CanEditComposer)
                Composer.Text = (_dictationPrefix + " " + turn.Text).Trim();
        });
    }
    private void ShowError(string message)
    {
        if (_disposed) return;
        var owner = _session;
        DispatcherQueue.TryEnqueue(() => RunUiCallback("ShowError", () =>
        {
            if (_disposed) return;
            if (_activeResearchSessionId == owner) ShowResearchError(owner, message);
            else { _chatErrors[owner] = message; RefreshChatNotices(); }
        }));
    }
    public void FocusComposer() => Composer.Focus(FocusState.Programmatic);
    public async Task CloseSessionToolsAsync()
    {
        if (_disposed) return;
        await CancelNativeMediaCaptureAsync();
        try
        {
            var captions = App.Current.GetService<SystemAudioCaptionService>();
            if (captions.IsRunning) await captions.StopAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
    public async Task DisposeAsync()
    {
        var publications = _publicationPool.Values.Concat(_publicationViews.Values).Distinct().ToArray();
        Dispose();
        // Keep the window/dispatcher alive until pending WebView2 operations
        // have settled and every retained browser has closed on its UI thread.
        await Task.WhenAll(publications.Select(view => view.DisposeAsync().AsTask()));
    }
    public void Dispose()
    {
        CancelPromptTimelineNavigation();
        if (_disposed) return;
        if (_captionSubscribed) App.Current.GetService<SystemAudioCaptionService>().Changed -= OnNativeCaptionChanged;
        _ = StopNativeDictationAsync();
        DisposeMessageActions();
        ClearToolTabs(returnToChat: false);
        DisposeScienceViews();
        _researchRefreshTimer?.Stop();
        _ = CancelNativeMediaCaptureAsync();
        _conversationSelection?.Clear();
        _hostSubscription.Dispose();
        _reasoningFlyout?.Hide(); _modelSelectionFlyout?.Hide();
        HideWorkingPlanView();
        _disposed = true; _draftTimer.Stop(); _renderTimer.Stop(); _microphone.TurnChanged -= OnTranscript; _lifetime.Cancel(); _lifetime.Dispose();
    }
}

















