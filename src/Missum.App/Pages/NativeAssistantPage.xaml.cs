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
    private readonly SettingsCoordinator _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _draftTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _renderTimer;
    private readonly Dictionary<string, (string Signature, Border View)> _messageViews = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedSteps = new(StringComparer.Ordinal);
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
    private readonly Dictionary<string, JsonElement> _pendingUserMessages = new(StringComparer.Ordinal);
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

    public NativeAssistantPage()
    {
        InitializeComponent();
        _conversationSelection = new NativeConversationSelection(ConversationContent, MessagesPanel, ConversationScroll) { ReadFromMenuFactory = CreateReadFromMenu };
        ResetChangesSummary();
        ComposerChipsScroll.SizeChanged += (_, _) =>
        {
            var width = Math.Clamp(ComposerChipsScroll.ActualWidth, 28, 220);
            SelectedToolChip.MaxWidth = CaptionChip.MaxWidth = width;
        };
        ConfigureSidebarHoverActions(ProjectsHeader, AddProjectButton);
        Composer.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnComposerKeyDown), true);
        NavigationCacheMode = NavigationCacheMode.Required;
        _coordinator = App.Current.GetService<AssistantCoordinator>();
        _settings = App.Current.GetService<SettingsCoordinator>();
        Inspector.Visibility = _settings.Current.AssistantOutputsVisible ? Visibility.Visible : Visibility.Collapsed;
        _microphone = App.Current.GetService<MicrophoneTranscriptionService>();
        _microphone.TurnChanged += OnTranscript;
        _draftTimer = DispatcherQueue.CreateTimer();
        _draftTimer.Interval = TimeSpan.FromMilliseconds(500);
        _draftTimer.IsRepeating = false;
        _draftTimer.Tick += async (_, _) => { try { await FlushDraftAsync(); } catch (Exception ex) { ShowError(ex.Message); } };
        _renderTimer = DispatcherQueue.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(80);
        _renderTimer.Tick += (_, _) => { UpdateVoiceVisual(); UpdateRunDurations(); if (_messagesDirty) { _messagesDirty = false; RenderMessagesNow(); } };
        _renderTimer.Start();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialised) { await RefreshForExternalActivationAsync(); return; }
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
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-ui-ready.json"), JsonSerializer.Serialize(new { renderer = "WinUI3", page = GetType().Name, sessionId = _session, ready = _session != Guid.Empty, navigationSmokeVisits }), _lifetime.Token);
    }

    private static string S(JsonElement value, string name, string fallback = "") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? field.ValueKind == JsonValueKind.String ? field.GetString() ?? fallback : field.ToString() : fallback;
    private static JsonElement[] Items(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
    private static SolidColorBrush Brush(byte gray) => new(Color.FromArgb(255, gray, gray, gray));

    private static SolidColorBrush ThemeBrush(string key, byte fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            return brush;
        return Brush(fallback);
    }

    private async Task<bool> CommandAsync(string type, object payload, Action<string>? onError = null)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var navigation = NativeNavigationState.IsNavigationCommand(type);
        var generation = navigation ? _navigationState.BeginNavigation() : _navigationState.Generation;
        var draftSession = _session;
        var draft = !_rendering ? Composer.Text : null;
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
                    await _coordinator.SaveDraftAsync(draftSession, draft);
                if (!_navigationState.IsCurrent(generation)) return false;
            }
            await _coordinator.HandleAsync(new(1, type, requestId, arguments),
                (eventType, data, id) => EmitAsync(eventType, data, id, generation, onError, requestSession), _lifetime.Token);
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
        // The unavailable-research response has no session field; retain the request's owner.
        if (type is ("research.snapshot" or "research.exported") && S(json, "sessionId").Length == 0 && requestSession.HasValue)
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
                ApplyEvent(type, json);
            }
            catch (Exception ex) { if (onError is not null) onError(ex.Message); else ShowError(ex.Message); }
            finally { completion.TrySetResult(); }
        })) completion.TrySetResult();
        return completion.Task;
    }

    private void ApplyEvent(string type, JsonElement data)
    {
        if (ApplyResearchEvent(type, data)) return;
        if (type == "chat.started" && S(data, "sessionId") == _session.ToString()
            && data.TryGetProperty("userMessage", out var userMessage) && userMessage.ValueKind == JsonValueKind.Object)
        {
            _messages[S(userMessage, "id")] = userMessage;
            ReconcilePendingMessages();
            RenderMessages();
        }
        if (type == "host.error") { ShowError(S(data, "message")); return; }
        if (type == "queue.changed") { UpdateSidebarActivity(data); return; }
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
                if (changed) { ResetConversationViews(); _expandedSteps.Clear(); ClearToolSelection(); _contextUsed = 0; _contextLimit = 0; ResetChangesSummary(); }
                var newMode = S(data, "chatMode", "general");
                if (_mode != newMode) ClearToolSelection();
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
                Composer.PlaceholderText = _mode switch { "coding" => "Leg einfach los", "claudescience" => "Stelle eine Forschungsfrage", _ => "Frag etwas" };
                _rendering = true;
                if (changed) Composer.Text = S(data, "draft");
                _rendering = false;
                _messages.Clear();
                foreach (var message in Items(data, "messages")) _messages[S(message, "id")] = message;
                ReconcilePendingMessages();
                UpdateCaptionChip();
                RenderSidebar();
                SyncSessionTabs();
                RenderMessages();
                RenderSources(data);
                var workspace = S(data, "workspacePath");
                WorkspaceText.Text = string.IsNullOrEmpty(workspace) ? "Kein Projekt ausgewählt" : Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                ToolTipService.SetToolTip(InspectorProjectRow, workspace);
                var modelId = (_mode == "coding" ? _settings.Current.SelectedModel : _settings.Current.SelectedModel); ModelLabel.Text = modelId is null ? "Modell auswählen" : modelId.Split('/').Last().Split('~')[0];
                UpdateContext(data);
                if (_reasoningModel != ModelRole + ":" + modelId) { _reasoningModel = ModelRole + ":" + modelId; _reasoning = null; ReasoningLabel.Text = ""; _ = LoadReasoningAsync(); }
                if (data.TryGetProperty("changesSummary", out var changes)) RenderChanges(changes);
                SetRunning();
                SyncResearchSession();
            }
            return;
        }
        var eventSession = S(data, "sessionId", S(data, "activeSessionId"));
        if (eventSession.Length > 0 && !string.Equals(eventSession, _session.ToString(), StringComparison.OrdinalIgnoreCase)) return;
        UpdateContext(data);
        if (type == "reasoning.snapshot" && S(data, "role") + ":" + S(data, "modelId") == _reasoningModel) { _reasoning = data.Deserialize<ComposerReasoningOptions>(JsonOptions); ReasoningLabel.Text = EffortLabel(_reasoning?.Selected); }
        if (type == "conversation.snapshot")
        {
            if (!ObserveChangesConversation(_session, data)) return;
            _messages.Clear();
            foreach (var message in Items(data, "messages")) _messages[S(message, "id")] = message;
            ReconcilePendingMessages();
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
            if (data.TryGetProperty("message", out var message)) { _messages[S(message, "id")] = message; RenderMessages(); }
        }
        if (type == "chat.delta")
        {
            var id = S(data, "messageId");
            if (_messages.TryGetValue(id, out var previous))
            {
                var merged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(previous.GetRawText())!;
                merged["content"] = data.GetProperty("content");
                if (data.TryGetProperty("toolSteps", out var steps)) merged["toolSteps"] = steps;
                _messages[id] = JsonSerializer.SerializeToElement(merged);
                RenderMessages();
            }
        }
        if (type is "chat.started" or "chat.queued") { _finishedSessions.Remove(_session); _running = true; SetRunning(); }
        if (type is "chat.completed" or "chat.cancelled" or "chat.failed")
        {
            _finishedSessions.Add(_session); _running = false; SetRunning(); ChatStatus = "";
            _ = RefreshForExternalActivationAsync();
            if (type == "chat.failed") ShowError(S(data, "error", "Die Anfrage ist fehlgeschlagen."));
        }
        if (type == "status.changed")
        {
            var status = S(data, "runStatus");
            var terminal = status is "Fertig" or "Abgebrochen" or "Fehlgeschlagen";
            if (terminal) { _finishedSessions.Add(_session); _running = false; SetRunning(); }
            ChatStatus = status.StartsWith("Modell generiert", StringComparison.Ordinal) ? "" : terminal ? "" : status + "  " + S(data, "runDetail");
            var id = S(data, "messageId");
            if (data.TryGetProperty("toolStep", out var step) && step.ValueKind == JsonValueKind.Object && _messages.TryGetValue(id, out var current))
            {
                var steps = Items(current, "toolSteps").ToList();
                var position = steps.FindIndex(s => S(s, "id") == S(step, "id"));
                if (position >= 0) steps[position] = step; else steps.Add(step);
                var merged = current.Deserialize<Dictionary<string, JsonElement>>()!; merged["toolSteps"] = JsonSerializer.SerializeToElement(steps);
                _messages[id] = JsonSerializer.SerializeToElement(merged); RenderMessages();
            }
        }
        if (type == "coding.changes") RenderChanges(data);
        RefreshResearchAfterRun(type);
        UpdateResearchRuntimeChrome();
    }

    private static DateTimeOffset MessageCreatedAt(JsonElement message) => DateTimeOffset.TryParse(S(message, "createdAt"), out var time) ? time : DateTimeOffset.MinValue;

    private void ReconcilePendingMessages()
    {
        if (_captionSessionId == _session && _captionMessageId is Guid captionId && _captionMessage.ValueKind == JsonValueKind.Object)
            _messages[captionId.ToString()] = _captionMessage;
        foreach (var (id, pending) in _pendingUserMessages.ToArray())
        {
            if (S(pending, "sessionId") != _session.ToString()) continue;
            if ((S(pending, "steeringInputId") is { Length: > 0 } inputId && _messages.Values.Any(message => Items(message, "toolSteps").Any(step => S(step, "id") == "steering-" + inputId)))
                || _messages.Values.Any(message => !S(message, "id").StartsWith("pending:", StringComparison.Ordinal) && S(message, "role") == "user"
                && S(message, "sessionId") == S(pending, "sessionId")
                && Missum.Core.Chat.ChatContentSanitizer.Sanitize(S(message, "content")) == Missum.Core.Chat.ChatContentSanitizer.Sanitize(S(pending, "content"))
                && MessageCreatedAt(message) >= MessageCreatedAt(pending)))
            { _pendingUserMessages.Remove(id); _messages.Remove(id); }
            else _messages[id] = pending;
        }
    }

    private string AddPendingUserMessage(Guid session, string prompt, string? steeringInputId = null)
    {
        var id = "pending:" + Guid.NewGuid().ToString("N");
        var pending = JsonSerializer.SerializeToElement(new { id, sessionId = session, role = "user", content = Missum.Core.Chat.ChatContentSanitizer.Sanitize(prompt.Trim()),
            status = "pending", steeringInputId, createdAt = DateTimeOffset.UtcNow.ToString("O") }, JsonOptions);
        _pendingUserMessages[id] = pending;
        _messages[id] = pending;
        RenderMessagesNow();
        OnScrollToBottom(this, new RoutedEventArgs());
        return id;
    }

    private static StackPanel MessageBody(Border bubble) => bubble.Child is StackPanel panel ? panel : (StackPanel)((Grid)bubble.Child).Children[1];

    private void RenderMessages() => _messagesDirty = true;
    private void ResetConversationViews()
    {
        _conversationSelection?.Clear();
        // WinUI elements have exactly one parent. Footer controls and context
        // menus belong to the same visual lifetime as their message bubbles.
        MessagesPanel.Children.Clear();
        _messageViews.Clear();
        _messageBlocks.Clear();
        _messageActionViews.Clear();
    }

    private void RenderMessagesNow()
    {
        ReconcilePendingMessages();
        RenderSources(_snapshot);
        var follow = _conversationSelection?.HasSelection != true && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 90;
        var index = 0;
        foreach (var stale in _messageViews.Keys.Where(id => !_messages.ContainsKey(id)).ToArray())
        { MessagesPanel.Children.Remove(_messageViews[stale].View); _messageViews.Remove(stale); _messageBlocks.Remove(stale); _messageActionViews.Remove(stale); }
        WelcomePanel.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var message in _messages.Values.OrderBy(MessageCreatedAt))
        {
            var id = S(message, "id");
            UpdateMessageActions(id, message);
            var signature = S(message, "content") + S(message, "toolSteps") + S(message, "error") + S(message, "artifacts") + S(message, "status") + S(message, "liveStatus");
            if (_messageViews.TryGetValue(id, out var cached) && cached.Signature == signature)
            {
                if (MessagesPanel.Children.IndexOf(cached.View) != index)
                { MessagesPanel.Children.Remove(cached.View); MessagesPanel.Children.Insert(index, cached.View); }
                index++; continue;
            }
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
                    layout.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new(14), VerticalAlignment = VerticalAlignment.Top,
                        Background = ThemeBrush("MissumAccentSubtleBrush", 42),
                        Child = new FontIcon { Glyph = "\uE99A", FontSize = 15, Foreground = ThemeBrush("MissumAccentBrush", 176) } });
                    bubble.Child = null;
                    Grid.SetColumn(messageBody, 1); layout.Children.Add(messageBody); bubble.Child = layout;
                }
                bubble.ContextFlyout = MessageContextMenuFor(id, message);
                MessagesPanel.Children.Insert(index, bubble);
            }
            if (MessagesPanel.Children.IndexOf(bubble) != index)
            { MessagesPanel.Children.Remove(bubble); MessagesPanel.Children.Insert(index, bubble); }
            UpdateMessageBlocks(id, message, MessageBody(bubble));
            _messageViews[id] = (signature, bubble); index++;
        }
        ConversationContent.UpdateLayout();
        RenderPromptTimeline();
        if (follow) ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
        SyncOuterScroll();
    }
    private void RenderSources(JsonElement data)
    {
        var signature = _session + "|" + ResearchState(_session).Revision + "|"
            + string.Join("|", _messages.Values.SelectMany(message => Items(message, "toolSteps")).Select(step => S(step, "id") + S(step, "updatedAt") + S(step, "outputJson").GetHashCode(StringComparison.Ordinal)))
            + string.Join("|", Items(data, "documents").Concat(Items(data, "attachments")).Select(item => S(item, "id")));
        if (_sourcesSignature == signature) return;
        _sourcesSignature = signature;
        SourcesPanel.Children.Clear();
        RenderWebSources();
        SourcesEmpty.Visibility = SourcesPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SourcesPanel.Visibility = SourcesPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // File receipts update the compact output entry and any open review tab.
    // The full file list belongs exclusively to the review, never after the cursor.
    private void RenderChanges(JsonElement summary) => UpdateChangesSummary(summary);

    public async Task FlushDraftAsync()
    {
        _draftTimer.Stop();
        if (_session != Guid.Empty && !_rendering) await _coordinator.SaveDraftAsync(_session, Composer.Text);
    }
    public async Task RefreshForExternalActivationAsync()
    {
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
    private void OnComposerGotFocus(object sender, RoutedEventArgs e) =>
        ComposerSurface.BorderBrush = ThemeBrush("MissumAccentBrush", 0xB0);
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
        Inspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveLayout();
        await _settings.UpdateAsync(settings => settings with { AssistantOutputsVisible = visible });
    }
    private bool _syncingScroll;
    private void OnConversationSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BodyGrid.Visibility != Visibility.Visible || e.NewSize.Width <= 0) return;
        var width = Math.Max(0, e.NewSize.Width - ConversationScroll.Padding.Left - ConversationScroll.Padding.Right);
        if (double.IsNaN(ConversationContent.Width) || Math.Abs(ConversationContent.Width - width) > .5) ConversationContent.Width = width;
        SyncOuterScroll();
    }
    private void OnConversationViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        SyncOuterScroll();
        UpdatePromptTimelineSelection();
    }
    private void SyncOuterScroll()
    {
        if (OuterChatScrollBar is null) return;
        _syncingScroll = true;
        OuterChatScrollBar.Maximum = ConversationScroll.ScrollableHeight;
        OuterChatScrollBar.ViewportSize = ConversationScroll.ViewportHeight;
        OuterChatScrollBar.LargeChange = ConversationScroll.ViewportHeight;
        OuterChatScrollBar.Value = ConversationScroll.VerticalOffset;
        OuterChatScrollBar.Visibility = ConversationScroll.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollToBottomButton.Visibility = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset > 90 ? Visibility.Visible : Visibility.Collapsed;
        _syncingScroll = false;
    }
    private void OnOuterScrollChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    { if (!_syncingScroll) ConversationScroll.ChangeView(null, e.NewValue, null, true); }
    private void OnScrollToBottom(object sender, RoutedEventArgs e)
    {
        ConversationContent.UpdateLayout();
        ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
        SyncOuterScroll();
    }
    private void OnAssistantPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_activeResearchSessionId == _session) return;
        // Only unhandled wheel input reaches this frame. Nested text/diff viewers
        // consume their own scrolling; blank inspector/header surfaces scroll chat.
        var point = e.GetCurrentPoint(AssistantFrame);
        if (point.Properties.IsHorizontalMouseWheel || point.Properties.MouseWheelDelta == 0) return;
        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control) || modifiers.HasFlag(VirtualKeyModifiers.Shift)) return;
        var scroll = ReviewScroll.Visibility == Visibility.Visible ? ReviewScroll : ConversationScroll;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != AssistantFrame; source = VisualTreeHelper.GetParent(source))
            if (source == scroll || source == Composer || source == OuterChatScrollBar || source == ComposerChipsScroll) return;
        var offset = Math.Clamp(scroll.VerticalOffset - point.Properties.MouseWheelDelta * 1.2, 0, scroll.ScrollableHeight);
        if (Math.Abs(offset - scroll.VerticalOffset) < .1) return;
        scroll.ChangeView(null, offset, null, true);
        e.Handled = true;
    }
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();
    private void UpdateResponsiveLayout()
    {
        if (Inspector is null || BodyGrid is null || AssistantFrame is null || InspectorToggle is null) return;
        Inspector.Width = Math.Min(304, Math.Max(180, AssistantFrame.ActualWidth - 44));
        Inspector.MaxHeight = Math.Max(80, AssistantFrame.ActualHeight - 80);
        var label = Inspector.Visibility == Visibility.Visible ? "Ausgaben schließen" : "Ausgaben öffnen";
        ToolTipService.SetToolTip(InspectorToggle, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(InspectorToggle, label);
    }
    private void SetRunning()
    {
        var hasText = !string.IsNullOrWhiteSpace(Composer.Text);
        var captionRunning = _captionActive && _captionSessionId == _session;
        var stop = !hasText && (_running || _speaking || captionRunning);

        ModelButton.IsEnabled = true;
        SendIcon.Glyph = stop ? "\uE71A" : "\uE74A";
        var label = stop ? "Aktivität stoppen" : _running ? "Antwort umlenken" : "Nachricht senden";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SendButton, label);
        ToolTipService.SetToolTip(SendButton, label);
        UpdateSidebarActivity();
    }
    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();
    private async Task SendAsync()
    {
        if (!_navigationState.CanEditComposer || _disposed || _sendPending) return;
        if (_dictationCapture is not null)
        {
            var dictationOwner = _session;
            _sendPending = true;
            try { await StopNativeDictationAsync(); }
            finally { _sendPending = false; }
            _dictationSession = null;
            if (_disposed || !_navigationState.CanEditComposer || _session != dictationOwner) return;
        }
        var prompt = Composer.Text.Trim();
        if (prompt.Length == 0 && _captionActive && _captionSessionId == _session) { OnStopCaptions(this, new RoutedEventArgs()); return; }
        if (_running)
        {
            if (prompt.Length == 0) { await CommandAsync("chat.cancel", new { sessionId = _session }); return; }
            var steeringSession = _session;
            var steeringDraft = Composer.Text;
            var inputId = Guid.NewGuid().ToString("N");
            var pending = AddPendingUserMessage(steeringSession, prompt, inputId);
            _sendPending = true;
            _draftTimer.Stop(); Composer.Text = "";
            try
            {
                if (!await CommandAsync("chat.steer", new { sessionId = steeringSession, prompt, inputId }))
                {
                    _pendingUserMessages.Remove(pending); _messages.Remove(pending); RenderMessages();
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
        var pendingUserId = AddPendingUserMessage(session, prompt);
        var promptEpoch = _changeReceiptState.BeginPrompt(session);
        ResetChangesSummary();
        _draftTimer.Stop(); _rendering = true; Composer.Text = ""; _rendering = false;
        _finishedSessions.Remove(session); _running = true; SetRunning(); _chatErrors.Remove(session); RefreshChatNotices(); ChatStatus = "Anfrage wird vorbereitet …";
        var deepResearch = mode == "claudescience" && string.IsNullOrEmpty(selectedAction);
        if (!await CommandAsync("chat.send", new { sessionId = session, prompt, extensionActionId = deepResearch ? null : selectedAction, deepResearch, deepResearchProfile = deepResearch ? "auto" : null }))
        {
            _pendingUserMessages.Remove(pendingUserId); _messages.Remove(pendingUserId); RenderMessages();
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
        ComposerInteractionHost.IsEnabled = _navigationState.CanEditComposer;
        Composer.IsReadOnly = !_navigationState.CanEditComposer;
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
        ContextRing.Value = _contextLimit > 0 ? Math.Clamp(_contextUsed / _contextLimit * 100, 0, 100) : 0;
        var description = _contextLimit > 0 ? $"Kontext: {_contextUsed:N0} / {_contextLimit:N0} Token ({ContextRing.Value:0.0} %)" : "Kontext noch nicht verfügbar";
        ContextToolTip.Content = description;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ContextIndicator, description);
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
            if (!_running) UpdateContext(JsonSerializer.SerializeToElement(new { contextLimit = Missum.Ai.Contracts.ModelContextProfiles.ResolveMaximum(modelId, role) }));
            _reasoning = options;
            ReasoningLabel.Text = options.Available ? EffortLabel(options.Selected) : "";
        }
        catch (OperationCanceledException) { }
        catch { _reasoning = null; }
    }
    private async void OnReasoningClick(object sender, RoutedEventArgs e)
    {
        await LoadReasoningAsync();
        if (_disposed) return;
        var options = _reasoning;
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top, AreOpenCloseAnimationsEnabled = true };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, Brush(43)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(18)));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 6)));
        presenter.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(62)));
        flyout.FlyoutPresenterStyle = presenter;
        var panel = new StackPanel { Width = 232, Spacing = 0 };
        var heading = new Grid();
        var caption = new TextBlock { Text = options?.Available == true ? "Reasoning: " + EffortLabel(options.Selected) : "Reasoning: nicht verfügbar", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeBrush("MissumAccentBrush", 0x8B) };
        heading.Children.Add(caption);
        panel.Children.Add(heading);
        var chooseModel = new Button { Content = ModelLabel.Text + " ›", BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 13, Foreground = Brush(185), Padding = new(6, 0, 6, 4), MaxWidth = 228 };
        chooseModel.Click += (_, _) => { flyout.Hide(); OnSelectModel(sender, e); };
        panel.Children.Add(chooseModel);
        if (options?.Available == true)
        {
            string[] order = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "on"];
            var levels = options.Levels.OrderBy(level => { var i = Array.IndexOf(order, level); return i < 0 ? int.MaxValue : i; }).ToArray();
            var selected = options.Selected;
            var slider = new Slider { Minimum = 0, Maximum = Math.Max(1, levels.Length - 1), StepFrequency = 1, TickFrequency = 1,
                TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.None, IsThumbToolTipEnabled = false,
                IsEnabled = levels.Length > 1, Value = Math.Max(0, Array.IndexOf(levels, selected)), Margin = new(0, 2, 0, 0) };
            var accent = ThemeBrush("MissumAccentBrush", 0x8B);
            slider.CornerRadius = new(12); slider.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent); slider.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            slider.Resources["SliderTrackThemeHeight"] = 24d;
            slider.Resources["SliderHorizontalThumbWidth"] = 24d;
            slider.Resources["SliderHorizontalThumbHeight"] = 24d;
            slider.Resources["SliderThumbCornerRadius"] = new CornerRadius(14);
            slider.Resources["SliderThumbBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            slider.Resources["SliderOuterThumbBackground"] = new SolidColorBrush(Microsoft.UI.Colors.White);
            foreach (var suffix in new[] { "", "PointerOver", "Pressed" })
            {
                slider.Resources["SliderTrackValueFill" + suffix] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                slider.Resources["SliderTrackFill" + suffix] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                slider.Resources["SliderThumbBackground" + suffix] = new SolidColorBrush(Microsoft.UI.Colors.White);
            }
            // Use the same thumb-centre geometry for the fill and discrete stops.
            // The native Slider remains responsible for keyboard, pointer and accessibility input.
            var sliderContainer = new Grid();
            var track = new Border { Height = 24, CornerRadius = new(12), Background = Brush(69), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 2, 0, 0), IsHitTestVisible = false };
            var color = (accent as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.MediumPurple;
            var light = Windows.UI.Color.FromArgb(255, (byte)((color.R + 255) / 2), (byte)((color.G + 255) / 2), (byte)((color.B + 255) / 2));
            var gradient = new LinearGradientBrush { StartPoint = new(0, .5), EndPoint = new(1, .5) };
            gradient.GradientStops.Add(new GradientStop { Color = light, Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = color, Offset = 1 });
            var fill = new Border { Height = 24, CornerRadius = new(12, 0, 0, 12), Background = gradient, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 2, 0, 0), IsHitTestVisible = false };
            sliderContainer.Children.Add(track); sliderContainer.Children.Add(fill); sliderContainer.Children.Add(slider);
            var dots = new Canvas { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 2, 0, 0), IsHitTestVisible = false };
            for (var i = 0; i < levels.Length; i++)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brush(140), Visibility = i == (int)slider.Value ? Visibility.Collapsed : Visibility.Visible };
                Canvas.SetLeft(dot, 10 + 208d * i / Math.Max(1, levels.Length - 1)); dots.Children.Add(dot);
            }
            sliderContainer.Children.Add(dots);
            void UpdateTrack()
            {
                var width = sliderContainer.ActualWidth;
                if (width <= 0) return;
                fill.Width = 12 + Math.Max(0, width - 24) * slider.Value / slider.Maximum;
                for (var i = 0; i < dots.Children.Count; i++)
                    Canvas.SetLeft(dots.Children[i], 10 + Math.Max(0, width - 24) * i / Math.Max(1, levels.Length - 1));
            }
            sliderContainer.SizeChanged += (_, _) => UpdateTrack();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "Reasoning-Aufwand");
            slider.ValueChanged += (_, args) =>
            {
                selected = levels[Math.Clamp((int)Math.Round(args.NewValue), 0, levels.Length - 1)];
                caption.Text = "Reasoning: " + EffortLabel(selected); ReasoningLabel.Text = EffortLabel(selected);
                UpdateTrack();
                for (var i = 0; i < dots.Children.Count; i++) dots.Children[i].Visibility = i == (int)slider.Value ? Visibility.Collapsed : Visibility.Visible;
                var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = .45, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(160)) };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, caption);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard(); storyboard.Children.Add(animation); storyboard.Begin();
                ToolTipService.SetToolTip(slider, caption.Text);
            };
            panel.Children.Add(sliderContainer);
            flyout.Closed += async (_, _) => { if (selected != options.Selected && selected is not null) await CommandAsync("reasoning.set", new { modelId = options.ModelId, role = options.Role, effort = selected }); };
        }
        else { panel.Children.Add(new TextBlock { Text = options?.Detail ?? "Modell auswählen", FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush(150) }); }
        flyout.Content = panel; NativeDropdownChevron.Bind(flyout, ModelButton); flyout.ShowAt(ModelButton);
    }
    private async void OnSelectModel(object sender, RoutedEventArgs e)
    {
        try
        {
            var role = ModelRole;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = await App.Current.GetService<MissumAiConnectionService>().CreateClientAsync(timeout.Token);
            var catalog = await client.GetModelStatusAsync(timeout.Token);
            var json = JsonSerializer.SerializeToElement(catalog, JsonOptions);
            var list = new ComboBox { MinWidth = 360, PlaceholderText = "Modell auswählen", Header = "Modell" };
            var levels = new ComboBox { MinWidth = 360, Header = "Denkaufwand", PlaceholderText = "Automatisch" };
            var models = Items(json, "models").Where(m => S(m, "role") is "general" or "coding" && S(m, "downloaded") == "True").GroupBy(m => S(m, "id")).Select(group => group.FirstOrDefault(m => S(m, "role") == role) is { ValueKind: JsonValueKind.Object } matching ? matching : group.First()).ToArray();
            var currentId = _settings.Current.SelectedModel;
            foreach (var model in models) list.Items.Add(new ComboBoxItem { Content = S(model, "displayName", S(model, "id")), Tag = S(model, "id") });
            list.SelectionChanged += (_, _) =>
            {
                levels.Items.Clear();
                if (list.SelectedItem is not ComboBoxItem selectedModel) return;
                var id = selectedModel.Tag.ToString();
                var model = models.First(m => S(m, "id") == id);
                foreach (var level in Items(model, "reasoningEfforts"))
                {
                    var raw = level.GetString()!;
                    levels.Items.Add(new ComboBoxItem { Content = EffortLabel(raw), Tag = raw });
                }
                var key = MissumAiAssistantService.ReasoningKey(id!, role);
                var selectedEffort = _settings.Current.ReasoningEffortsByModel.GetValueOrDefault(key) ?? S(model, "defaultReasoningEffort");
                levels.SelectedItem = levels.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedEffort, StringComparison.OrdinalIgnoreCase));
                levels.Visibility = levels.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            list.SelectedItem = list.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag.ToString() == currentId);
            var content = new StackPanel { Spacing = 16, Children = { list, levels } };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Lokales Modell", Content = content, PrimaryButtonText = "Übernehmen", CloseButtonText = "Abbrechen" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is ComboBoxItem selected)
            {
                var id = selected.Tag.ToString()!;
                await _settings.UpdateAsync(s =>
                {
                    var efforts = new Dictionary<string, string>(s.ReasoningEffortsByModel, StringComparer.OrdinalIgnoreCase);
                    if (levels.SelectedItem is ComboBoxItem selectedEffort && selectedEffort.Tag is string effort)
                        efforts[MissumAiAssistantService.ReasoningKey(id, role)] = effort;
                    return s with { SelectedModel = id, SelectedCodingModel = id, ReasoningEffortsByModel = efforts };
                });
                _reasoningModel = "";
                await App.Current.GetService<MissumAiAssistantService>().RequestLiveModelSelectionAsync(_lifetime.Token);
                await RefreshForExternalActivationAsync();
            }
        }        catch (Exception ex) { ShowError(ex.Message); }
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
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (_activeResearchSessionId == owner) ShowResearchError(owner, message);
            else { _chatErrors[owner] = message; RefreshChatNotices(); }
        });
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
    public void Dispose()
    {
        if (_disposed) return;
        if (_captionSubscribed) App.Current.GetService<SystemAudioCaptionService>().Changed -= OnNativeCaptionChanged;
        _ = StopNativeDictationAsync();
        DisposeMessageActions();
        _researchRefreshTimer?.Stop();
        _ = CancelNativeMediaCaptureAsync();
        _conversationSelection?.Clear();
        _disposed = true; _draftTimer.Stop(); _renderTimer.Stop(); _microphone.TurnChanged -= OnTranscript; _lifetime.Cancel(); _lifetime.Dispose();
    }
}

















