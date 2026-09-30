using System.Text.Json;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<string, MessageActionView> _messageActionViews = new(StringComparer.Ordinal);
    private CancellationTokenSource? _messageSpeechCancellation;
    private Task? _messageSpeechTask;
    private Guid? _messageSpeechSessionId, _messageSpeechMessageId;
    private string _messageSpeechStatus = "";
    private long _messageSpeechRequest;
    private bool _messageActionsSubscribed, _messageActionsDisposed, _messageSpeechActionBusy, _messageSpeechStopRequested;

    private StackPanel MessageActionsFor(string messageId, JsonElement message)
    {
        UpdateMessageActions(messageId, message);
        return _messageActionViews[messageId].Panel;
    }

    private MenuFlyout MessageContextMenuFor(string messageId, JsonElement message)
    {
        UpdateMessageActions(messageId, message);
        return _messageActionViews[messageId].Menu;
    }

    /// <summary>Call even when the content signature is unchanged: terminal status enables read-aloud.</summary>
    private void UpdateMessageActions(string messageId, JsonElement message)
    {
        if (!_messageActionsSubscribed && !_messageActionsDisposed)
        {
            _microphone.Changed += OnMessageSpeechMicrophoneChanged;
            _messageActionsSubscribed = true;
        }
        if (!_messageActionViews.TryGetValue(messageId, out var view))
            _messageActionViews[messageId] = view = CreateMessageActionView(messageId);
        view.SessionId = Guid.TryParse(S(message, "sessionId"), out var sessionId) ? sessionId : _session;
        view.Text = S(message, "content");
        view.IsAssistant = string.Equals(S(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase);
        view.CanRead = (view.IsAssistant || S(message, "role") == "user") && !string.IsNullOrWhiteSpace(view.Text)
            && S(message, "status").ToLowerInvariant() is "completed" or "cancelled" or "interrupted" or "failed";
        RefreshMessageActionView(view);
    }

    private MessageActionView CreateMessageActionView(string messageId)
    {
        var view = new MessageActionView(messageId);
        view.Copy.Click += (_, _) => CopyMessageText(view);
        view.Read.Click += async (_, _) =>
        {
            if (IsMessageSpeechSource(view) && _speaking) await StopMessageSpeechAsync();
            else await StartMessageSpeechAsync(view);
        };
        view.Pause.Click += async (_, _) => await ToggleMessageSpeechPauseAsync();
        view.CopyMenu.Click += (_, _) => CopyMessageText(view);
        view.ReadMenu.Click += async (_, _) => await StartMessageSpeechAsync(view);
        view.PauseMenu.Click += async (_, _) => await ToggleMessageSpeechPauseAsync();
        view.StopMenu.Click += async (_, _) => await StopMessageSpeechAsync();
        view.Panel.Children.Add(view.Copy);
        view.Panel.Children.Add(view.Read);
        view.Panel.Children.Add(view.Pause);
        view.Panel.Children.Add(view.Status);
        view.Menu.Items.Add(view.CopyMenu);
        view.Menu.Items.Add(view.ReadMenu);
        view.Menu.Items.Add(view.PauseMenu);
        view.Menu.Items.Add(view.StopMenu);
        return view;
    }

    private MenuFlyout? CreateReadFromMenu(FrameworkElement target, Windows.Foundation.Point point)
    {
        foreach (var (id, entry) in _messageViews)
        {
            if (!_messageActionViews.TryGetValue(id, out var view) || !view.CanRead || view.SessionId != _session) continue;
            var blocks = MessageBody(entry.View).Children.OfType<Missum.App.Controls.NativeStreamingMarkdown>().Cast<FrameworkElement>().ToArray();
            var excerpt = Missum.App.Controls.NativeConversationSelection.ReadableSuffix(blocks, target);
            if (excerpt.Length == 0) continue;
            if (!_messages.TryGetValue(id, out var message) || !DateTimeOffset.TryParse(S(message, "updatedAt"), out var updatedAt)) return null;
            var session = _session;
            var menu = new MenuFlyout();
            var read = new MenuFlyoutItem { Text = "Ab hier vorlesen", Icon = new FontIcon { Glyph = "\uE767" } };
            read.Click += async (_, _) => { if (_session == session) await StartMessageSpeechAsync(view, excerpt, updatedAt); };
            menu.Items.Add(read);
            var copy = new MenuFlyoutItem { Text = "Nachricht kopieren" };
            copy.Click += (_, _) => CopyMessageText(view); menu.Items.Add(copy);
            return menu;
        }
        return null;
    }

    private async void CopyMessageText(MessageActionView view)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(view.Text);
            Clipboard.SetContent(package);
            var version = ++view.CopyVersion;
            if (view.Copy.Content is FontIcon icon) icon.Glyph = "\uE73E";
            ToolTipService.SetToolTip(view.Copy, "Kopiert");
            AutomationProperties.SetName(view.Copy, "Kopiert");
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            if (version == view.CopyVersion && !_disposed)
            {
                if (view.Copy.Content is FontIcon restored) restored.Glyph = "\uE8C8";
                ToolTipService.SetToolTip(view.Copy, "Nachricht kopieren");
                AutomationProperties.SetName(view.Copy, "Nachricht kopieren");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMessageActionError(exception.Message); }
    }

    private async Task StartMessageSpeechAsync(MessageActionView view, string? excerpt = null, DateTimeOffset? expectedUpdatedAt = null)
    {
        if (_disposed || _messageActionsDisposed || _messageSpeechActionBusy || !view.CanRead) return;
        if (!Guid.TryParse(view.MessageId, out var messageId)) { ShowMessageActionError("Die gespeicherte Nachricht hat keine gültige ID."); return; }
        _messageSpeechActionBusy = true;
        _messageSpeechStopRequested = false;
        RefreshAllMessageActions();
        try
        {
            // Read-aloud is independent of chat generation. Replace only the preceding audio operation.
            _messageSpeechCancellation?.Cancel();
            var service = App.Current.GetService<MissumAiAssistantService>();
            if (service.IsSpeaking || _microphone.Current.IsSpeaking) await service.CancelSpeechAsync(CancellationToken.None);
            if (_messageSpeechTask is { IsCompleted: false }) await _messageSpeechTask;
            if (_messageSpeechStopRequested || _disposed || _messageActionsDisposed) return;

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _messageSpeechCancellation = cancellation;
            _messageSpeechSessionId = view.SessionId;
            _messageSpeechMessageId = messageId;
            _messageSpeechStatus = "Vorlesen wird vorbereitet";
            _speaking = true;
            var request = ++_messageSpeechRequest;
            RefreshAllMessageActions();
            _messageSpeechTask = ReadPersistedMessageAsync(service, view.SessionId, messageId, request, cancellation, excerpt, expectedUpdatedAt);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _messageSpeechStopRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMessageActionError(exception.Message); }
        finally
        {
            _messageSpeechActionBusy = false;
            RefreshAllMessageActions();
        }
    }

    private async Task ReadPersistedMessageAsync(MissumAiAssistantService service, Guid sessionId, Guid messageId,
        long request, CancellationTokenSource cancellation, string? excerpt = null, DateTimeOffset? expectedUpdatedAt = null)
    {
        try
        {
            // Supplying the stored message ID makes the service validate and load its persisted text.
            await service.SpeakAsync(sessionId, explicitText: null, sourceMessageId: messageId,
                update => DispatchMessageActionAsync(() =>
                {
                    if (request != _messageSpeechRequest) return;
                    _speaking = update.IsActive;
                    _messageSpeechStatus = update.Status;
                    if (!string.IsNullOrWhiteSpace(update.Error)) ShowMessageActionError(update.Error);
                    RefreshAllMessageActions();
                }),
                progress => DispatchMessageActionAsync(() =>
                {
                    if (request != _messageSpeechRequest) return;
                    _messageSpeechSessionId = progress.SessionId;
                    _messageSpeechMessageId = progress.SourceMessageId;
                    if (progress.State == SpeechPlaybackState.Paused) _messageSpeechStatus = "Vorlesen pausiert";
                    RefreshAllMessageActions();
                }),
                cancellationToken: cancellation.Token, messageExcerpt: excerpt, expectedMessageUpdatedAt: expectedUpdatedAt);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await DispatchMessageActionAsync(() => ShowMessageActionError(exception.Message));
        }
        finally
        {
            await DispatchMessageActionAsync(() =>
            {
                if (request != _messageSpeechRequest) return;
                _speaking = false;
                RefreshAllMessageActions();
            });
            if (ReferenceEquals(_messageSpeechCancellation, cancellation)) _messageSpeechCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task StopMessageSpeechAsync()
    {
        if (_messageActionsDisposed) return;
        _messageSpeechStopRequested = true;
        try
        {
            _messageSpeechCancellation?.Cancel();
            await App.Current.GetService<MissumAiAssistantService>().CancelSpeechAsync(CancellationToken.None);
            if (_messageSpeechTask is { IsCompleted: false }) await _messageSpeechTask;
            _speaking = false;
            _messageSpeechStatus = "Vorlesen beendet";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMessageActionError(exception.Message); }
        finally { RefreshAllMessageActions(); }
    }

    private async Task ToggleMessageSpeechPauseAsync()
    {
        if (_disposed || !_microphone.Current.CanPauseSpeech) return;
        try
        {
            var snapshot = await _microphone.ToggleSpeechPauseAsync(_lifetime.Token);
            _messageSpeechStatus = snapshot.IsSpeechPaused ? "Vorlesen pausiert" : snapshot.Status;
            RefreshAllMessageActions();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMessageActionError(exception.Message); }
    }

    /// <summary>Handle global coordinator speech events before filtering events by the visible chat.</summary>
    private void ApplyMessageSpeechStatus(JsonElement data)
    {
        // A composer request resolved to speech rather than a generated chat turn.
        // Only that request releases the pending chat UI; independent audio never does.
        if (S(data, "isPromptRequest") == "True" && Guid.TryParse(S(data, "sessionId"), out var sessionId) && sessionId == _session)
            _running = false;
        _speaking = S(data, "active") == "True";
        _messageSpeechStatus = S(data, "status");
        var error = S(data, "error");
        if (error.Length > 0) ShowMessageActionError(error);
        RefreshAllMessageActions();
    }

    private void ApplyMessageSpeechProgress(JsonElement data)
    {
        if (Guid.TryParse(S(data, "sessionId"), out var sessionId)) _messageSpeechSessionId = sessionId;
        _messageSpeechMessageId = Guid.TryParse(S(data, "sourceMessageId"), out var messageId) ? messageId : null;
        if (S(data, "state") == "paused") _messageSpeechStatus = "Vorlesen pausiert";
        RefreshAllMessageActions();
    }

    private void OnMessageSpeechMicrophoneChanged(object? sender, MicrophoneSnapshot snapshot)
    {
        _ = DispatchMessageActionAsync(() =>
        {
            if (snapshot.IsSpeaking) _messageSpeechStatus = snapshot.Status;
            RefreshAllMessageActions();
        });
    }

    private Task DispatchMessageActionAsync(Action action)
    {
        if (_disposed || _messageActionsDisposed) return Task.CompletedTask;
        if (DispatcherQueue.HasThreadAccess) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try { if (!_disposed && !_messageActionsDisposed) action(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMessageActionError(exception.Message); }
            finally { completion.TrySetResult(); }
        })) completion.TrySetResult();
        return completion.Task;
    }

    private void RefreshAllMessageActions()
    {
        if (_disposed || _messageActionsDisposed) return;
        foreach (var stale in _messageActionViews.Where(pair => pair.Value.SessionId != _session || !_messages.ContainsKey(pair.Key)).Select(pair => pair.Key).ToArray())
            _messageActionViews.Remove(stale);
        foreach (var view in _messageActionViews.Values) RefreshMessageActionView(view);
        SetRunning();
    }

    private bool IsMessageSpeechSource(MessageActionView view) => _messageSpeechSessionId == view.SessionId
        && Guid.TryParse(view.MessageId, out var messageId) && _messageSpeechMessageId == messageId;

    private void RefreshMessageActionView(MessageActionView view)
    {
        var active = _speaking && IsMessageSpeechSource(view);
        var microphone = _microphone.Current;
        view.Panel.Visibility = !string.IsNullOrWhiteSpace(view.Text) ? Visibility.Visible : Visibility.Collapsed;
        view.Copy.IsEnabled = view.CopyMenu.IsEnabled = !string.IsNullOrWhiteSpace(view.Text);
        view.Read.Visibility = Visibility.Visible;
        view.Read.IsEnabled = active || view.CanRead && !_messageSpeechActionBusy;
        ((FontIcon)view.Read.Content).Glyph = active ? "\uE71A" : "\uE767";
        var readLabel = active ? "Vorlesen beenden" : view.CanRead ? "Nachricht vorlesen" : "Nach Abschluss vorlesen";
        ToolTipService.SetToolTip(view.Read, readLabel);
        AutomationProperties.SetName(view.Read, readLabel);
        view.ReadMenu.IsEnabled = view.CanRead && !active && !_messageSpeechActionBusy;
        view.ReadMenu.Visibility = Visibility.Visible;
        view.Pause.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        view.Pause.IsEnabled = active && microphone.CanPauseSpeech;
        view.PauseMenu.IsEnabled = view.Pause.IsEnabled;
        view.PauseMenu.Visibility = view.StopMenu.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var pauseLabel = microphone.IsSpeechPaused ? "Vorlesen fortsetzen" : "Vorlesen pausieren";
        ((FontIcon)view.Pause.Content).Glyph = microphone.IsSpeechPaused ? "\uE768" : "\uE769";
        ToolTipService.SetToolTip(view.Pause, pauseLabel);
        AutomationProperties.SetName(view.Pause, pauseLabel);
        view.PauseMenu.Text = pauseLabel;
        view.Status.Text = active ? _messageSpeechStatus : "";
        ToolTipService.SetToolTip(view.Status, view.Status.Text);
    }

    private void ShowMessageActionError(string message)
    {
        if (_disposed || _messageActionsDisposed) return;
        ShowError(message);
    }

    private void DisposeMessageActions()
    {
        if (_messageActionsDisposed) return;
        _messageActionsDisposed = true;
        if (_messageActionsSubscribed) _microphone.Changed -= OnMessageSpeechMicrophoneChanged;
        _messageSpeechCancellation?.Cancel();
        _messageActionViews.Clear();
    }

    private static Button MessageActionButton(string glyph, string label)
    {
        var button = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Foreground = Brush(154),
            Content = new FontIcon { Glyph = glyph, FontSize = 15 },
        };
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        return button;
    }

    private sealed class MessageActionView(string messageId)
    {
        public string MessageId { get; } = messageId;
        public Guid SessionId { get; set; }
        public string Text { get; set; } = "";
        public bool IsAssistant { get; set; }
        public bool CanRead { get; set; }
        public long CopyVersion { get; set; }
        public StackPanel Panel { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 3, MinHeight = 28 };
        public Button Copy { get; } = MessageActionButton("\uE8C8", "Nachricht kopieren");
        public Button Read { get; } = MessageActionButton("\uE767", "Nachricht vorlesen");
        public Button Pause { get; } = MessageActionButton("\uE769", "Vorlesen pausieren");
        public TextBlock Status { get; } = new() { FontSize = 13, Foreground = Brush(145), MaxWidth = 310, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        public MenuFlyout Menu { get; } = new();
        public MenuFlyoutItem CopyMenu { get; } = new() { Text = "Nachricht kopieren", Icon = new FontIcon { Glyph = "\uE8C8" } };
        public MenuFlyoutItem ReadMenu { get; } = new() { Text = "Nachricht vorlesen", Icon = new FontIcon { Glyph = "\uE767" } };
        public MenuFlyoutItem PauseMenu { get; } = new() { Text = "Vorlesen pausieren", Icon = new FontIcon { Glyph = "\uE769" } };
        public MenuFlyoutItem StopMenu { get; } = new() { Text = "Vorlesen beenden", Icon = new FontIcon { Glyph = "\uE71A" } };
    }
}
