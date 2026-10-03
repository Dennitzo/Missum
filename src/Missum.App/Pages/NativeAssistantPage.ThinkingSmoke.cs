using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyThinkingIndicatorSmokeAsync(JsonElement original)
    {
        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid().ToString();
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(messageId);
        snapshot["runStatus"] = JsonSerializer.SerializeToElement("Modell generiert");
        snapshot["generationState"] = JsonSerializer.SerializeToElement("tokenProgress");
        snapshot["generatedTokens"] = JsonSerializer.SerializeToElement(25);
        snapshot["contextUsed"] = JsonSerializer.SerializeToElement(1025);
        snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
        {
            id = messageId, sessionId = owner, role = "assistant", content = "", status = "streaming",
            createdAt = DateTimeOffset.UtcNow.AddSeconds(-1), updatedAt = DateTimeOffset.UtcNow,
        } });
        try
        {
            // The same native path must work in all modes and after navigation.
            foreach (var mode in new[] { "general", "coding", "claudescience" })
            {
                snapshot["chatMode"] = JsonSerializer.SerializeToElement(mode);
                snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
                _finishedSessions.Remove(owner);
                _thinkingStates.Remove(messageId);
                ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
                RenderMessagesNow(); RefreshThinkingIndicators();
                if (_thinkingIndicators[messageId].Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Fresh silent token generation must show the thinking row in every mode.");
            }

            var row = _thinkingIndicators[messageId];
            var body = MessageBody(_messageViews[messageId].View);
            var cursor = _messageBlocks[messageId]["streamCursor"];
            if (row.IsHitTestVisible || row.Children.OfType<Button>().Any()
                || body.Children.IndexOf(row) != body.Children.IndexOf(cursor) - 1
                || body.Children.OfType<FrameworkElement>().Last(child => child.Visibility == Visibility.Visible) != cursor
                || row.Label.Text != $"Denke nach · {1025:N0} Token")
                throw new InvalidOperationException("Thinking must be a compact, noninteractive row immediately above the final cursor.");

            void Progress(string phase, int tokens, int contextTokens) => ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, messageId, runStatus = "Modell generiert", generationState = phase,
                generatedTokens = tokens, contextUsed = contextTokens, generationUpdatedAt = DateTimeOffset.UtcNow,
            }));

            var oldLabel = row.Label.Text;
            _messagesDirty = false;
            Progress("tokenProgress", 30, 1030);
            RefreshThinkingIndicators();
            if (_messagesDirty || row.Label.Text != oldLabel || !ReferenceEquals(row, _thinkingIndicators[messageId])
                || !ReferenceEquals(cursor, _messageBlocks[messageId]["streamCursor"]))
                throw new InvalidOperationException("Token progress rebuilt controls or bypassed the throttled label update.");
            var header = _messageBlocks[messageId]["header"];
            UpdateMessageHeader(header, _messages[messageId]);
            RefreshThinkingIndicators(refreshTokens: true);
            var headerText = ((TextBlock)((StackPanel)header).Children[0]).Text;
            if (row.Label.Text != $"Denke nach · {1030:N0} Token"
                || !headerText.StartsWith("In Bearbeitung seit ", StringComparison.Ordinal)
                || headerText.Contains("Token", StringComparison.Ordinal))
                throw new InvalidOperationException("Thinking must display live tokens while the active header displays only elapsed time.");

            BodyGrid.Visibility = Visibility.Collapsed; RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Hidden chat tabs must not display thinking.");
            ShowChatView(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Visible) throw new InvalidOperationException("Returning to chat lost active thinking.");

            foreach (var phase in new[] { "promptProcessing", "codingLoading", "generationStarted", "toolSelected", "providerRetryWaiting" })
            {
                Progress(phase, 30, 1030); RefreshThinkingIndicators();
                if (row.Visibility != Visibility.Visible || !ReferenceEquals(row, _thinkingIndicators[messageId]))
                    throw new InvalidOperationException("Intermediate phases must preserve the visible thinking row without replacing it.");
            }

            var merged = _messages[messageId].Deserialize<Dictionary<string, JsonElement>>()!;
            merged["toolSteps"] = JsonSerializer.SerializeToElement(new[] { new { id = "active-tool", tool = "web.search", status = "running" } });
            _messages[messageId] = JsonSerializer.SerializeToElement(merged);
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Visible) throw new InvalidOperationException("A running tool must not remove already visible thinking.");
            merged["toolSteps"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            _messages[messageId] = JsonSerializer.SerializeToElement(merged);
            RenderMessagesNow();

            // Discard/recreate native controls when visiting another session,
            // while preserving this run's latched state across an expired pulse.
            var paused = new Dictionary<string, JsonElement>(snapshot);
            paused["generationState"] = JsonSerializer.SerializeToElement("providerRetryWaiting");
            paused["generationUpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(paused)); RenderMessagesNow(); RefreshThinkingIndicators();
            row = _thinkingIndicators[messageId];
            cursor = _messageBlocks[messageId]["streamCursor"];
            if (row.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Returning to a session during a token pause must retain latched thinking.");

            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = "\n\n" }));
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Whitespace-only streaming must not remove latched thinking.");

            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = "Ein neuer sichtbarer Absatz." }));
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Collapsed || !ReferenceEquals(cursor, _messageBlocks[messageId]["streamCursor"]))
                throw new InvalidOperationException("Visible text must hide thinking without replacing the cursor.");

            Progress("codingWaiting", 30, 1030);
            await Task.Delay(850);
            RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Old generation eligibility must not reappear after visible answer text.");
            Progress("tokenProgress", 31, 1031); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Fresh generation after answer text must start a new thinking interval.");

            merged = _messages[messageId].Deserialize<Dictionary<string, JsonElement>>()!;
            merged["toolSteps"] = JsonSerializer.SerializeToElement(new[] { new
            {
                id = "science-introduction", tool = "assistant.narration", status = "completed",
                detail = "Ich prüfe nun die wissenschaftlichen Quellen.",
            } });
            _messages[messageId] = JsonSerializer.SerializeToElement(merged);
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A new visible Science narration must consume latched thinking, like answer text.");

            await Task.Delay(850);
            Progress("tokenProgress", 32, 1032); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Fresh silent generation after Science narration must restore thinking.");
            merged["toolSteps"] = JsonSerializer.SerializeToElement(new[] { new
            {
                id = "science-introduction", tool = "assistant.narration", status = "completed",
                detail = "Ich prüfe nun die wissenschaftlichen Quellen.", explanation = "Metadaten aktualisiert",
            } });
            _messages[messageId] = JsonSerializer.SerializeToElement(merged);
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Unchanged visible narration must not remove latched thinking on metadata updates.");

            // Screenshot the restored empty answer using actual WinUI controls.
            // Capture the root: a ScrollViewer subtree can omit cached glyphs.
            ResetConversationViews();
            _thinkingStates.Remove(messageId);
            snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow(); RefreshThinkingIndicators(); UpdateLayout();
            await SaveMathPreviewAsync(LayoutRoot, "native-thinking-preview.png");

            var completed = _messages[messageId].Deserialize<Dictionary<string, JsonElement>>()!;
            completed["status"] = JsonSerializer.SerializeToElement("completed");
            ApplyEvent("chat.completed", JsonSerializer.SerializeToElement(new { sessionId = owner, message = completed }));
            RenderMessagesNow();
            if (_thinkingIndicators.ContainsKey(messageId) || _messageBlocks[messageId].ContainsKey("thinkingIndicator")
                || _messageBlocks[messageId].ContainsKey("streamCursor"))
                throw new InvalidOperationException("Completion retained the transient thinking row or cursor.");

            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            if (_thinkingIndicators.ContainsKey(messageId) || _thinkingStates.ContainsKey(messageId))
                throw new InvalidOperationException("Thinking leaked into another session after navigation.");
        }
        finally
        {
            _finishedSessions.Remove(owner);
            _thinkingStates.Remove(messageId);
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
        }
    }
}
