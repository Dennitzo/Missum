using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // These fixtures contain no literal cursor glyphs. A caller explicitly
    // checking user text with such a glyph can still verify the host removal.
    private void AssertChatCursorAbsent(DependencyObject root, bool allowLiteralCursorGlyphs = false)
    {
        if (_messageBlocks.Values.Any(blocks => blocks.ContainsKey("streamCursor")))
            throw new InvalidOperationException("The chat retained an empty streaming cursor host.");
        void Inspect(DependencyObject node)
        {
            if (!allowLiteralCursorGlyphs && node is TextBlock text
                && (text.Text == "▍" || text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => run.Text == "▍")))
                throw new InvalidOperationException("The chat rendered a decorative streaming cursor.");
            if (!allowLiteralCursorGlyphs && node is RichTextBlock rich
                && rich.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>().SelectMany(paragraph => paragraph.Inlines)
                    .OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => run.Text == "▍"))
                throw new InvalidOperationException("The chat rendered a decorative rich-text streaming cursor.");
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                Inspect(VisualTreeHelper.GetChild(node, index));
        }
        Inspect(root);
    }

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
            AssertChatCursorAbsent(body);
            if (!row.IsHitTestVisible || !row.ToggleButton.IsTabStop || row.IsExpanded
                || body.Children.OfType<FrameworkElement>().Last(child => child.Visibility == Visibility.Visible
                    && !ReferenceEquals(child, _messageActionViews[messageId].Panel)) != row
                || row.Label.Text != $"Denke nach · {1025:N0} Token")
                throw new InvalidOperationException("Thinking must remain a compact, collapsed disclosure before the message footer, without a cursor host.");

            // Prompt evaluation must honestly precede thinking without creating
            // another control, invented reasoning, or generated-token counts.
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, messageId, runStatus = "Kontext wird verarbeitet", generationState = "promptProcessing",
                processedPromptTokens = 70539, totalPromptTokens = 80000, promptProgress = .88,
                generatedTokens = 0, generationUpdatedAt = DateTimeOffset.UtcNow,
            }));
            RefreshThinkingIndicators();
            if (!ReferenceEquals(row, _thinkingIndicators[messageId]) || row.Visibility != Visibility.Visible
                || !row.IsProcessing || !row.Label.Text.StartsWith("Kontext wird verarbeitet", StringComparison.Ordinal)
                || !row.Label.Text.Contains($"{70539:N0} Eingangstoken", StringComparison.Ordinal))
                throw new InvalidOperationException("Prompt processing was mislabeled as generated reasoning.");
            row.SetExpanded(true); await Task.Delay(30);
            if (row.ReasoningView.Visibility != Visibility.Collapsed
                || !row.ExplanationText.Contains("Generierung beginnt", StringComparison.Ordinal))
                throw new InvalidOperationException("Prefill needs an explanatory message instead of invented reasoning.");
            var processingLabel = row.Label.Text;
            _messagesDirty = false;
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, messageId, generationState = "promptProcessing",
                processedPromptTokens = 72000, totalPromptTokens = 80000,
                generatedTokens = 0, generationUpdatedAt = DateTimeOffset.UtcNow,
            }));
            RefreshThinkingIndicators();
            if (_messagesDirty || !ReferenceEquals(row, _thinkingIndicators[messageId]) || row.Label.Text != processingLabel)
                throw new InvalidOperationException("Prompt-processing updates rebuilt controls or bypassed throttling.");
            RefreshThinkingIndicators(refreshTokens: true);
            if (!row.Label.Text.Contains($"{72000:N0} Eingangstoken", StringComparison.Ordinal)
                || !row.Label.Text.Contains(.9.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal))
                throw new InvalidOperationException("Measured prompt progress did not reach the native row.");
            await SaveMathPreviewAsync(LayoutRoot, "native-thinking-processing-preview.png");
            row.SetExpanded(false);

            // Exercise the real native button, then stream reasoning through the
            // same chat.delta path while retaining its open control and receipt.
            var reasoningMessage = _messages[messageId].Deserialize<Dictionary<string, JsonElement>>()!;
            reasoningMessage["toolSteps"] = JsonSerializer.SerializeToElement(new[] { new
            {
                id = "live-reasoning", tool = "assistant.reasoning", status = "running", contentOffset = 0,
                detail = "Ich untersuche die Voraussetzungen.", updatedAt = DateTimeOffset.UtcNow,
            } });
            _messages[messageId] = JsonSerializer.SerializeToElement(reasoningMessage);
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.IsExpanded || _messageBlocks[messageId]["tool:live-reasoning"].Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Live reasoning must start collapsed without a duplicate history row.");
            if (new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(row.ToggleButton)
                .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke) is not Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke)
                throw new InvalidOperationException("Thinking does not expose a native accessible button.");
            invoke.Invoke(); await Task.Delay(30);
            if (!row.IsExpanded) throw new InvalidOperationException("The thinking disclosure did not open through its native button.");
            var reasoningView = row.ReasoningView;
            var firstParagraph = reasoningView.Children.First();
            foreach (var detail in new[] { "Ich untersuche die Voraussetzungen. Danach", "Ich untersuche die Voraussetzungen. Danach prüfe ich die Einheiten." })
            {
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = "", toolSteps = new[] { new
                { id = "live-reasoning", tool = "assistant.reasoning", status = "running", detail, contentOffset = 0, updatedAt = DateTimeOffset.UtcNow } } }));
                RenderMessagesNow(); RefreshThinkingIndicators();
                if (!row.IsExpanded || !ReferenceEquals(reasoningView, row.ReasoningView)
                    || !ReferenceEquals(firstParagraph, reasoningView.Children.First())
                    || string.Concat(((TextBlock)firstParagraph).Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(run => run.Text)) != detail)
                    throw new InvalidOperationException("A reasoning delta rebuilt or failed to incrementally update the opened disclosure.");
                AssertChatCursorAbsent(body);
            }
            await SaveMathPreviewAsync(LayoutRoot, "native-thinking-expanded-preview.png");
            invoke.Invoke(); await Task.Delay(30);
            if (row.IsExpanded) throw new InvalidOperationException("The thinking disclosure did not close through its native button.");

            void Progress(string phase, int tokens, int contextTokens) => ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, messageId, runStatus = "Modell generiert", generationState = phase,
                generatedTokens = tokens, contextUsed = contextTokens, generationUpdatedAt = DateTimeOffset.UtcNow,
            }));

            var oldLabel = row.Label.Text;
            var bodyChildCount = body.Children.Count;
            _messagesDirty = false;
            Progress("tokenProgress", 30, 1030);
            RefreshThinkingIndicators();
            if (_messagesDirty || row.Label.Text != oldLabel || !ReferenceEquals(row, _thinkingIndicators[messageId])
                || body.Children.Count != bodyChildCount)
                throw new InvalidOperationException("Token progress rebuilt controls or bypassed the throttled label update.");
            AssertChatCursorAbsent(body);
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
            body = MessageBody(_messageViews[messageId].View);
            AssertChatCursorAbsent(body);
            if (row.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Returning to a session during a token pause must retain latched thinking.");

            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = "\n\n" }));
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Whitespace-only streaming must not remove latched thinking.");

            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = "Ein neuer sichtbarer Absatz." }));
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (row.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Visible text must hide thinking while the answer continues streaming.");
            AssertChatCursorAbsent(body);

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
            AssertChatCursorAbsent(MessageBody(_messageViews[messageId].View));

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
