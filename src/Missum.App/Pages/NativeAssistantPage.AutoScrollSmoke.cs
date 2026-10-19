using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyConversationAutoScrollSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke) throw new InvalidOperationException("Scrollprüfung benötigt ein isoliertes Profil.");
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["activeRunSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(id);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("general");
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new {
            id, sessionId = owner, role = "assistant", status = "streaming", createdAt = now, updatedAt = now,
            content = string.Join("\n\n", Enumerable.Range(0, 55).Select(index => $"Abschnitt {index}: Diese Nachricht bleibt während der fortlaufenden Ausgabe stabil lesbar.")),
        } });
        var oldFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        bool AtTail() => ConversationScroll.ScrollableHeight > 250 && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset <= 2;
        void Append(string text)
        {
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                contentAppend = text, updatedAt = DateTimeOffset.UtcNow }));
            RenderMessagesNow(deferLayout: true);
        }
        try
        {
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(AtTail, "Neue Sitzung folgt ihrem neuesten Inhalt nicht.");
            // A framework anchor can move the offset even with no user input.
            // The next completed layout must still follow the real tail.
            ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight - 8, null, true);
            Append("\n\nEin weiterer Absatz nach einer Layoutkorrektur.");
            await WaitForPagingSmokeAsync(AtTail, "Eine fremde Layoutkorrektur hat AutoScroll ausgeschaltet.");
            if (!TryHandleFollowingWheel(20) || !TryHandleFollowingWheel(20) || !_scrollNewSessionToLatest || !CanFollowConversationTail)
                throw new InvalidOperationException("Kleine Radbewegungen deaktivieren AutoScroll vor drei Zeilen.");
            Append("\n\nAuch kleine Touchpadbewegungen unterbrechen die Ausgabe nicht.");
            await WaitForPagingSmokeAsync(AtTail, "Kleine Radbewegungen verlieren die letzte Ausgabe.");
            if (!TryHandleFollowingWheel(30) || _scrollNewSessionToLatest || CanFollowConversationTail)
                throw new InvalidOperationException("Drei Zeilen bewussten Hochscrollens pausieren AutoScroll nicht.");
            await Task.Delay(100, _lifetime.Token);
            var readingOffset = ConversationScroll.VerticalOffset;
            Append("\n\nDer Nutzer liest jetzt einen früheren Bereich; diese neue Ausgabe darf ihn nicht verschieben.");
            await Task.Delay(160, _lifetime.Token);
            if (Math.Abs(ConversationScroll.VerticalOffset - readingOffset) > 2)
                throw new InvalidOperationException("Streaming verschiebt die explizite Leseposition.");
            OnScrollToBottom(this, new RoutedEventArgs());
            await WaitForPagingSmokeAsync(AtTail, "Zum neuesten Beitrag reaktiviert AutoScroll nicht.");
            var footer = _messageActionViews[id].Panel;
            var footerParent = footer.Parent;
            var footerY = footer.TransformToVisual(ConversationScroll).TransformPoint(new()).Y;
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                generationState = "promptProcessing", totalPromptTokens = 100, processedPromptTokens = 80,
                generationUpdatedAt = now.AddSeconds(2) }));
            RenderMessagesNow(); RefreshThinkingIndicators();
            await WaitForPagingSmokeAsync(AtTail, "Kontextverarbeitung löst die Verankerung am Ende.");
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                generationState = "tokenProgress", generatedTokens = 10, generationUpdatedAt = now.AddSeconds(3) }));
            Append("\n\nDie Antwort wird nach der Kontextverarbeitung weitergeschrieben.");
            await WaitForPagingSmokeAsync(AtTail, "Das Ende der Kontextverarbeitung verschiebt die letzte Ausgabe.");
            if (!ReferenceEquals(footer, _messageActionViews[id].Panel) || !ReferenceEquals(footerParent, footer.Parent)
                || Math.Abs(footer.TransformToVisual(ConversationScroll).TransformPoint(new()).Y - footerY) > 3)
                throw new InvalidOperationException("Der feste Nachrichtenfooter springt nach dem Kontextstatus.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-scroll-validation.json"),
                JsonSerializer.Serialize(new { passed = true, minimumManualLines = 3, incidentalWheelIgnored = true,
                    frameworkOffsetDoesNotCancelFollow = true, manualReadingStable = true, returnToLatestRestoresFollow = true,
                    contextPhaseKeepsTailAndFooter = true, modelRequests = 0, userDataModified = false }), _lifetime.Token);
        }
        finally
        {
            _selectionInputSmokeActive = oldFixture;
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
        }
    }
}
