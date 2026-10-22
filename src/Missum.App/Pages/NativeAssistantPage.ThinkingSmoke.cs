using System.Text.Json;
using Missum.App.Controls;
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
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        try { await VerifyThinkingIndicatorFixtureAsync(original); }
        finally { _selectionInputSmokeActive = previousFixture; }
    }

    private async Task VerifyThinkingIndicatorFixtureAsync(JsonElement original)
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(id);
        var modes = new[] { "general", "coding", "claudescience" };
        try
        {
            foreach (var mode in modes)
            {
                id = Guid.NewGuid().ToString();
                at = DateTimeOffset.UtcNow;
                snapshot["runMessageId"] = JsonSerializer.SerializeToElement(id);
                snapshot["chatMode"] = JsonSerializer.SerializeToElement(mode);
                snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
                {
                    id, sessionId = owner, role = "assistant", content = "", status = "streaming", createdAt = at, updatedAt = at,
                } });
                ApplyEvent("session.changed", JsonSerializer.SerializeToElement(snapshot));
                RenderMessagesNow(); UpdateLayout();
                var slot = (ActivitySlotView)_messageBlocks[id]["slot:activity:start"];
                var view = (ToolStepView)slot.Content;
                var height = slot.ActualHeight;
                if (view.HasActivityDisclosure || view.ActivityLabel != "Denke nach" || view.IsExpanded)
                    throw new InvalidOperationException("Thinking must be static text without disclosure.");
                foreach (var tool in new[] { "assistant.reasoning", "context.compact", "assistant.progress" })
                {
                    ApplyEvent("conversation.toolStepChanged", JsonSerializer.SerializeToElement(new
                    {
                        sessionId = owner, messageId = id,
                        toolStep = new { id = tool, tool, status = "running", detail = "PRIVATE REASONING", contentOffset = 0, updatedAt = at },
                    }));
                    RenderMessagesNow(); UpdateLayout();
                    if (!ReferenceEquals(slot, _messageBlocks[id]["slot:activity:start"]) || Math.Abs(slot.ActualHeight - height) > 1)
                        throw new InvalidOperationException("Prefill/reasoning/compaction changed the activity slot.");
                }
                if (Descendants(_messageViews[id].View).OfType<TextBlock>().Any(text => text.Text.Contains("PRIVATE REASONING", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Reasoning leaked into the chat.");
                ApplyEvent("conversation.toolStepChanged", JsonSerializer.SerializeToElement(new
                {
                    sessionId = owner, messageId = id,
                    toolStep = new { id = "read", tool = "coding.read", status = "running", contentOffset = 0,
                        inputJson = "{\"path\":\"example.py\"}", updatedAt = at },
                }));
                RenderMessagesNow(); UpdateLayout();
                if (!ReferenceEquals(slot, _messageBlocks[id]["slot:tool:read"])
                    || slot.Content is not ToolStepView { IsActivity: false, IsExpanded: false })
                    throw new InvalidOperationException("The next tool did not replace the existing activity row.");
                void Step(string stepId, string tool, string detail = "")
                {
                    ApplyEvent("conversation.toolStepChanged", JsonSerializer.SerializeToElement(new
                    {
                        sessionId = owner, messageId = id,
                        toolStep = new { id = stepId, tool, status = "running", detail, contentOffset = 0, updatedAt = DateTimeOffset.UtcNow },
                    }));
                    RenderMessagesNow(); UpdateLayout();
                }
                Step("after-read", "assistant.reasoning", "Interne Überlegung.");
                var messageSlot = (ActivitySlotView)_messageBlocks[id]["slot:activity:after:read"];
                Step("message", "assistant.narration", "Ich prüfe jetzt den nächsten Zusammenhang.");
                if (!ReferenceEquals(messageSlot, _messageBlocks[id]["slot:narration:message"])
                    || messageSlot.Content is not NativeStreamingMarkdown)
                    throw new InvalidOperationException("Eine AI-Meldung hat nicht denselben Denkplatz übernommen.");
                Step("answer-thinking", "assistant.reasoning", "Weitere interne Überlegung.");
                var answerSlot = (ActivitySlotView)_messageBlocks[id]["slot:activity:after:message"];
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentAppend = "Das Ergebnis folgt aus der überprüften Gleichung.", updatedAt = DateTimeOffset.UtcNow }));
                RenderMessagesNow(); UpdateLayout();
                if (!ReferenceEquals(answerSlot, _messageBlocks[id]["slot:source:0"])
                    || answerSlot.Content is not NativeStreamingMarkdown
                    || Descendants(_messageViews[id].View).OfType<ToolStepView>().Any(tool => tool.IsActivity))
                    throw new InvalidOperationException("Antworttext muss den Denkplatz übernehmen; ältere Denkzeilen dürfen nicht stehen bleiben.");
            }
            await SaveMathPreviewAsync(LayoutRoot, "native-static-thinking-preview.png");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-static-thinking-validation.json"),
                JsonSerializer.Serialize(new { passed = true, modes,
                    stableRow = true, hiddenReasoning = true, nextToolReusesRow = true,
                    nextMessageReusesRow = true, answerReusesRow = true, noHistoricalThinkingRows = true }));
        }
        finally { ApplyEvent("state.snapshot", original); RenderMessagesNow(); }
    }
}
