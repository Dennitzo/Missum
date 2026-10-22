using System.Text.Json;
using Missum.App.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyTextBlockStabilitySmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke) throw new InvalidOperationException("Isoliertes Testprofil erforderlich.");
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var source = string.Join("\n\n", Enumerable.Range(0, 22).Select(index =>
            $"Abschnitt {index}: Dieser längere Absatz bleibt während laufender Denk- und Werkzeugereignisse vollständig erhalten. Die unveränderte Darstellung darf nicht kurzzeitig zusammenfallen."));
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["activeRunSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(id);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
        {
            id, sessionId = owner, role = "assistant", content = source, status = "streaming", createdAt = now, updatedAt = now,
            toolSteps = new[]
            {
                new { id = "narration", tool = "assistant.narration", status = "completed", contentOffset = 0, detail = "Ich prüfe den aktuellen Rechenschritt." },
                new { id = "reasoning", tool = "assistant.reasoning", status = "running", contentOffset = source.Length, detail = "Interner Denktext." },
            },
        } });
        try
        {
            ApplyEvent("session.changed", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow();
            var text = (NativeStreamingMarkdown)_messageBlocks[id]["source:0"];
            var narration = (NativeStreamingMarkdown)_messageBlocks[id]["narration:narration"];
            await WaitForPagingSmokeAsync(() => !text.IsPreparing, "Der Text wurde nicht fertig aufgebaut.");
            ConversationContent.UpdateLayout();
            OnScrollToBottom(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            await Task.Delay(150, _lifetime.Token);
            var revision = text.RenderRevision;
            var footer = _messageActionViews[id].Panel;
            var footerParent = footer.Parent;
            var thinkingSlot = _messageBlocks[id]["slot:activity:after:text:0"];
            var height = ConversationScroll.ScrollableHeight;
            var offset = ConversationScroll.VerticalOffset;
            var maximumHeightDrift = 0d;
            var maximumOffsetDrift = 0d;
            for (var index = 0; index < 40; index++)
            {
                ApplyEvent("conversation.toolStepChanged", JsonSerializer.SerializeToElement(new
                {
                    sessionId = owner, messageId = id,
                    toolStep = new { id = "reasoning", tool = "assistant.reasoning", status = "running", contentOffset = source.Length,
                        detail = "Fortlaufender interner Denktext " + index, updatedAt = now.AddMilliseconds(index + 1) },
                }));
                RenderMessagesNow(deferLayout: true);
                await Task.Delay(25, _lifetime.Token);
                if (!ReferenceEquals(text, _messageBlocks[id]["source:0"])
                    || !ReferenceEquals(narration, _messageBlocks[id]["narration:narration"])
                    || text.RenderRevision != revision || text.IsPreparing)
                    throw new InvalidOperationException("Ein unveränderter Chatabsatz wurde während des Denkprozesses neu erstellt oder geparst.");
                maximumHeightDrift = Math.Max(maximumHeightDrift, Math.Abs(ConversationScroll.ScrollableHeight - height));
                maximumOffsetDrift = Math.Max(maximumOffsetDrift, Math.Abs(ConversationScroll.VerticalOffset - offset));
            }
            if (maximumHeightDrift > 2 || maximumOffsetDrift > 2 || !ReferenceEquals(footer, _messageActionViews[id].Panel)
                || !ReferenceEquals(footerParent, footer.Parent))
                throw new InvalidOperationException($"Chat/Scrollposition sprang während unveränderter Textausgabe: Höhe {maximumHeightDrift}, Position {maximumOffsetDrift}.");
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                contentAppend = "\n\nNeues fachliches Ergebnis.", updatedAt = now.AddSeconds(2) }));
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => _messageBlocks[id].GetValueOrDefault("source:0") is NativeStreamingMarkdown next
                && next.Source.EndsWith("Neues fachliches Ergebnis.", StringComparison.Ordinal) && !next.IsPreparing,
                "Die sichtbare Antwort ersetzt die Denkzeile nicht.");
            if (!ReferenceEquals(text, _messageBlocks[id]["source:0"]))
                throw new InvalidOperationException("Streaming ersetzt statt aktualisiert den vorhandenen Textblock.");
            if (MessageBody(_messageViews[id].View).Children.Contains(thinkingSlot)
                || Descendants(_messageViews[id].View).OfType<ToolStepView>().Any(step => step.IsActivity))
                throw new InvalidOperationException("Nach der veröffentlichten Antwort blieb eine veraltete Denkzeile stehen.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-text-stability-validation.json"),
                JsonSerializer.Serialize(new { passed = true, updates = 40, maximumHeightDrift, maximumOffsetDrift,
                    textAndNarrationRetained = true, unchangedTextNotReparsed = true, streamingRetainsControl = true,
                    footerRetained = true, answerRetainsCanonicalSource = true, obsoleteThinkingTailRemoved = true,
                    modelRequests = 0, userDataModified = false }));
        }
        finally
        {
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }
    }
}
