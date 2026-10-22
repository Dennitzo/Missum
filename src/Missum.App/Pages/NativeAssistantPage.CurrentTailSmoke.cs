using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyCurrentChatTailSmokeAsync(JsonElement original)
    {
        var fixturePath = Environment.GetEnvironmentVariable("MISSUM_SMOKE_CHAT_TAIL_FIXTURE");
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1"
            || string.IsNullOrWhiteSpace(fixturePath)) throw new InvalidOperationException("Isolierte Chat-Replay-Fixture erforderlich.");
        using var fixtureDoc = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath, _lifetime.Token));
        var fixture = fixtureDoc.RootElement;
        var originalMessage = fixture.GetProperty("message");
        var source = S(originalMessage, "content");
        var continuation = Items(originalMessage, "toolSteps").Last(step => S(step, "tool") == "assistant.continuation");
        var tailStart = continuation.GetProperty("contentOffset").GetInt32();
        var steps = Items(originalMessage, "toolSteps").TakeWhile(step => S(step, "id") != S(continuation, "id"))
            .Append(continuation).ToArray();
        var id = Guid.NewGuid().ToString();
        var owner = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        var current = source[..tailStart] + "\n\n";
        JsonElement Envelope(string status) => JsonSerializer.SerializeToElement(new
        {
            id, sessionId = owner, role = "assistant", content = current, status, createdAt = at, updatedAt = at,
            revision = 6000, toolSteps = steps, toolStepWindow = new { totalSteps = 74, hasMore = true, beforeOrdinal = 13 },
        });
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["activeRunSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(id);
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("claudescience");
        snapshot["generationState"] = JsonSerializer.SerializeToElement("promptProcessing");
        snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(at);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { Envelope("streaming") });
        try
        {
            ApplyEvent("session.changed", JsonSerializer.SerializeToElement(snapshot));
            ShowChatView(); RenderMessagesNow();
            await ReadyAsync();
            foreach (var item in Items(fixture, "eventTextTail").Where(item => S(item, "type") == "text.delta"))
            {
                var delta = S(item.GetProperty("data"), "delta");
                current += delta;
                at = at.AddMilliseconds(200);
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentAppend = delta, updatedAt = at }));
                RenderMessagesNow();
                await ReadyAsync();
            }
            if (MessageContent(_messages[id]) != source) throw new InvalidOperationException("Der echte Text wurde beim Replay verändert.");
            at = at.AddSeconds(1);
            Phase("toolCallGeneration");
            await ReadyAsync();
            var state = _virtualMessages[id];
            if (state.Blocks.Any(block => block.Kind == "text" && string.IsNullOrWhiteSpace(block.Text)))
                throw new InvalidOperationException("Leerzeilen erzeugen weiterhin unsichtbare virtuelle Zeilen.");
            var receiptKey = "tool:" + S(continuation, "id");
            var receipt = _messageBlocks[id][receiptKey];
            var textBlock = state.Blocks.First(block => block.Kind == "text" && block.Start >= tailStart);
            var tail = _messageBlocks[id][textBlock.Key];
            var gap = tail.TransformToVisual(ConversationContent).TransformPoint(new()).Y
                - receipt.TransformToVisual(ConversationContent).TransformPoint(new()).Y - receipt.ActualHeight;
            if (receipt.ActualHeight > 44 || gap > 18 || gap < -1)
                throw new InvalidOperationException($"Fortsetzen hat weiterhin einen Leerraum: Zeile {receipt.ActualHeight}, Abstand {gap}.");
            var markdown = Descendants(tail).OfType<NativeStreamingMarkdown>().Single();
            if (markdown.IsPreparing || !markdown.Source.Contains("als abgeschlossen.", StringComparison.Ordinal)
                || !VisibleText(tail).Contains("als abgeschlossen.", StringComparison.Ordinal))
                throw new InvalidOperationException("Das vollständige Satzende ist während der Werkzeugvorbereitung nicht sichtbar.");
            AssertThinking();
            var revision = markdown.RenderRevision;
            var height = ConversationScroll.ScrollableHeight;
            var offset = ConversationScroll.VerticalOffset;
            var drift = 0d;
            for (var index = 0; index < 40; index++)
            {
                at = at.AddMilliseconds(200);
                Phase(index == 20 ? "reasoningStarted" : "tokenProgress");
                RenderMessagesNow();
                await Task.Delay(20, _lifetime.Token);
                AssertThinking();
                drift = Math.Max(drift, Math.Max(Math.Abs(ConversationScroll.ScrollableHeight - height),
                    Math.Abs(ConversationScroll.VerticalOffset - offset)));
            }
            if (drift > 2 || markdown.RenderRevision != revision)
                throw new InvalidOperationException($"Der virtualisierte Verlauf sprang bei Statusereignissen ({drift} Pixel).");
            await SaveMathPreviewAsync(LayoutRoot, "native-current-tail-preview.png");
            ApplyEvent("conversation.messageCommitted", JsonSerializer.SerializeToElement(new { sessionId = owner, message = Envelope("completed") }));
            RenderMessagesNow(); await ReadyAsync();
            if (_virtualMessages[id].Blocks.Any(block => S(block.Data, "tool") == "assistant.activity"))
                throw new InvalidOperationException("Nach Abschluss blieb eine Denkzeile zurück.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-current-tail-validation.json"),
                JsonSerializer.Serialize(new { passed = true, actualDatabaseReplay = true, virtualized = true,
                    originalSourceLength = source.Length, continuationHeight = receipt.ActualHeight, gapPixels = gap,
                    fullSentenceBeforeTool = true, reasoningWithoutPayloadVisible = true, updates = 40, maximumDrift = drift,
                    noWhitespaceRows = true, sourceUnchanged = true, modelRequests = 0, userDataModified = false }));
        }
        finally
        {
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }

        void Phase(string phase) => ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
        { sessionId = owner, messageId = id, generationState = phase, generationUpdatedAt = at, runStatus = "Modell generiert" }));
        void AssertThinking()
        {
            var last = _virtualMessages[id].Blocks[^1];
            if (S(last.Data, "tool") != "assistant.activity" || !_messageBlocks[id].TryGetValue(last.Key, out var view)
                || !Descendants(view).OfType<ToolStepView>().Any(tool => tool.IsActivity && tool.ActivityLabel == "Denke nach"))
                throw new InvalidOperationException("Während der weiteren Verarbeitung fehlt die Denkzeile am sichtbaren Ende.");
        }
        async Task ReadyAsync()
        {
            await WaitForPagingSmokeAsync(() =>
            {
                RenderMessagesNow(); ConversationContent.UpdateLayout(); OnScrollToBottom(this, new RoutedEventArgs());
                RefreshVirtualConversationViewport();
                return _virtualMessages.TryGetValue(id, out var state) && !state.Preparing
                    && state.Version == MessageVersion(_messages[id]) && state.Panel.RealizedCount > 0;
            }, "Der echte virtualisierte Verlauf wurde nicht vollständig aufgebaut.");
            await Task.Delay(60, _lifetime.Token);
            ConversationContent.UpdateLayout();
        }
        static string VisibleText(DependencyObject root) => string.Concat(Descendants(root).OfType<TextBlock>()
            .SelectMany(text => text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>()).Select(run => run.Text));
    }
}
