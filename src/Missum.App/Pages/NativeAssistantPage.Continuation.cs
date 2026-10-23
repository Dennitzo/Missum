using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private bool _continuationRequestPending;
    private string? _continuationMessageId;
    private Guid? _continuationSessionId;
    private bool _continuationServerStarted;
    private bool IsContinuationPreparing => _continuationRequestPending && _continuationSessionId == _session
        && !_continuationServerStarted && ActiveSubagent is null;

    private void ObserveContinuationAdmission(string type, JsonElement data)
    {
        if (!_continuationRequestPending || _continuationServerStarted || _continuationSessionId is not { } owner) return;
        if (type == "chat.started" && S(data, "sessionId") == owner.ToString()
            && data.TryGetProperty("message", out var message) && S(message, "id") == _continuationMessageId)
            _continuationServerStarted = true;
        if ((type is "state.snapshot" or "session.changed" or "document.changed")
            && S(data, "activeSessionId") == owner.ToString() && S(data, "activeRunId").Length > 0
            && S(data, "runMessageId") == _continuationMessageId)
            _continuationServerStarted = true;
    }

    internal static bool IsResumableAssistantStatus(JsonElement message)
    {
        if (S(message, "role") != "assistant") return false;
        if (MissumAiAssistantService.HasOnlyManualCompactionReceipts(
            Items(message, "toolSteps").Select(step => (S(step, "id"), S(step, "tool"))))) return false;
        var status = S(message, "status").ToLowerInvariant();
        if (status is "cancelled" or "interrupted") return true;
        if (status != "failed") return false;
        var content = S(message, "content").Trim();
        return Items(message, "toolSteps").Any(step => S(step, "tool") is not ("assistant.continuation" or "assistant.progress"))
            || content.Length > 0
                && !string.Equals(content, S(message, "error").Trim(), StringComparison.Ordinal)
                && content != "Der Missum-AI-Auftrag konnte nicht abgeschlossen werden."
                && !content.StartsWith("**Fehler", StringComparison.Ordinal)
                && !content.StartsWith("Der AI-Lauf ist fehlgeschlagen", StringComparison.Ordinal);
    }

    private bool IsNewestAssistantContinuation(string messageId, JsonElement message)
    {
        if (ActiveSubagent is not null || !IsResumableAssistantStatus(message)) return false;
        var conversation = _messages.Values.Where(item => S(item, "role") is "user" or "assistant")
            .OrderBy(MessageCreatedAt).ToArray();
        var user = conversation.TakeWhile(item => S(item, "id") != messageId)
            .LastOrDefault(item => S(item, "role") == "user");
        // A large answer may be the only message in the initial history page.
        // The service validates the original prompt against durable history on click.
        var hasPrompt = S(user, "content").Trim().Length > 0
            || user.ValueKind != JsonValueKind.Object && _historyHasMore;
        return S(conversation.LastOrDefault(), "id") == messageId && hasPrompt
            && (!Guid.TryParse(S(message, "sessionId"), out var owner) || owner == _session);
    }

    private bool HasActiveModelOperation()
    {
        if (_running || _sendPending) return true;
        if (S(_snapshot, "isAiBusy") == "True"
            && (!Guid.TryParse(S(_snapshot, "activeRunSessionId"), out var owner) || !_finishedSessions.Contains(owner))) return true;
        return _snapshot.ValueKind == JsonValueKind.Object
            && _snapshot.TryGetProperty("runQueue", out var queue)
            && queue.ValueKind == JsonValueKind.Object
            && queue.TryGetProperty("active", out var active)
            && S(active, "state") is "running" or "cancelling";
    }

    private void ObserveContinuationQueue(JsonElement data)
    {
        if (_snapshot.ValueKind == JsonValueKind.Object && data.TryGetProperty("runQueue", out var queue))
        {
            var snapshot = _snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            snapshot["runQueue"] = queue.Clone();
            _snapshot = JsonSerializer.SerializeToElement(snapshot);
        }
        RefreshContinuationSteps();
    }

    private void RefreshContinuationStep(string messageId, JsonElement message)
    {
        if (_messageBlocks.TryGetValue(messageId, out var blocks)
            && blocks.TryGetValue("continuation", out var element)
            && element is ContinuationToolStepView step)
            UpdateContinuationStep(messageId, message, step);
    }

    private void RefreshContinuationSteps()
    {
        foreach (var (id, message) in _messages) RefreshContinuationStep(id, message);
    }

    private void UpdateContinuationStep(string messageId, JsonElement message, ContinuationToolStepView step)
    {
        var pending = _continuationRequestPending && _continuationMessageId == messageId;
        step.Visibility = IsNewestAssistantContinuation(messageId, message) && (!_running || pending)
            ? Visibility.Visible : Visibility.Collapsed;
        var enabled = !_disposed && _navigationState.CanEditComposer && !_continuationRequestPending
            && !HasActiveModelOperation() && Guid.TryParse(messageId, out _);
        step.SetState(enabled, pending);
    }

    private async Task ContinueAssistantMessageAsync(string messageId)
    {
        if (_disposed || !_navigationState.CanEditComposer || _continuationRequestPending || HasActiveModelOperation()
            || !_messages.TryGetValue(messageId, out var message)
            || !IsNewestAssistantContinuation(messageId, message)
            || !Guid.TryParse(messageId, out var parsedMessageId)) return;
        PrepareContinuationInteraction();
        var session = _session;
        _continuationRequestPending = true;
        _continuationMessageId = messageId;
        _continuationSessionId = session;
        _continuationServerStarted = false;
        _chatErrors.Remove(session); RefreshChatNotices();
        RefreshContinuationSteps();
        SetRunning();
        try
        {
            // The coordinator resumes the existing assistant anchor. Neither the
            // composer draft nor the user-message history is changed here.
            if (await CommandAsync("chat.resume", new { sessionId = session, messageId = parsedMessageId }))
                await RefreshForExternalActivationAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_disposed && _session == session) ShowError(exception.Message);
        }
        finally
        {
            _continuationRequestPending = false;
            _continuationMessageId = null;
            _continuationSessionId = null;
            _continuationServerStarted = false;
            RefreshContinuationSteps();
            SetRunning();
        }
    }

    private void PrepareContinuationInteraction()
    {
        DismissPromptTimelinePreviews();
        // Move focus before disabling/removing the clicked button. Otherwise
        // WinUI can select the first focusable control in the chat: its timeline.
        Composer.Focus(FocusState.Programmatic);
    }

    internal sealed class ContinuationToolStepView : Grid
    {
        private readonly TextBlock _title = new()
        {
            Text = "Fortsetzen", FontSize = 14, Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        internal Button ContinueButton { get; }
        private (bool Enabled, bool Pending)? _renderState;

        public ContinuationToolStepView()
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE768", FontSize = 14, Foreground = NativeIconPalette.BrushFor("add"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(_title, 1); row.Children.Add(_title);
            ContinueButton = new Button
            {
                Content = row, HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left, Padding = new(6, 5, 6, 5),
                BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsTabStop = true,
            };
            AutomationProperties.SetName(ContinueButton, "AI-Antwort fortsetzen");
            ToolTipService.SetToolTip(ContinueButton, "Unterbrochene AI-Antwort fortsetzen");
            Children.Add(ContinueButton);
        }

        internal void SetState(bool enabled, bool pending)
        {
            if (_renderState == (enabled, pending)) return;
            _renderState = (enabled, pending);
            ContinueButton.IsEnabled = enabled;
            _title.Text = pending ? "Wird fortgesetzt …" : "Fortsetzen";
            ToolTipService.SetToolTip(ContinueButton, pending ? "Fortsetzung wird vorbereitet"
                : enabled ? "Unterbrochene AI-Antwort fortsetzen" : "Nach Abschluss des aktiven AI-Laufs fortsetzen");
        }
    }
}
