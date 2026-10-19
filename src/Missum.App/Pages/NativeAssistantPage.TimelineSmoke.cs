using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyPromptTimelineLazyNavigationSmokeAsync(JsonElement original)
    {
        var isolated = Environment.GetEnvironmentVariable("ASSISTANT_DATA_ROOT");
        if (!IsMessageFooterSmoke || string.IsNullOrWhiteSpace(isolated)
            || !string.Equals(Path.GetFullPath(isolated), Path.GetFullPath(App.Current.DataDirectory), StringComparison.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Zeitachsenprüfung benötigt das isolierte Profil ohne Modellstart.");
        var chats = App.Current.GetService<IChatRepository>();
        var snapshots = App.Current.GetService<IConversationSnapshotRepository>();
        var originalSession = _session;
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        try
        {
            var fixture = await Task.Run(async () =>
            {
                var session = await chats.CreateSessionAsync("Lazy-Zeitachse", ChatMode.General, _lifetime.Token);
                var messages = new List<ChatMessage>();
                for (var index = 0; index < 80; index++)
                {
                    var text = index == 13 ? string.Join("\n\n", Enumerable.Range(0, 250).Select(number =>
                        $"Abschnitt {number}: Die virtuelle Antwort behält beim Nachladen ihren ursprünglichen Quelltext und ihre mathematischen Voraussetzungen."))
                        : $"Nachricht {index}: Dieser Quelltext gehört unverändert zum gespeicherten Gespräch.";
                    messages.Add(await chats.AddMessageAsync(session.Id, index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                        text, MessageStatus.Completed, cancellationToken: _lifetime.Token));
                }
                return (session.Id, Messages: messages.ToArray(), Revision: (await chats.GetSessionAsync(session.Id, _lifetime.Token))!.ConversationRevision);
            }, _lifetime.Token);
            await CommandAsync("session.open", new { sessionId = fixture.Id });
            ShowChatView(); _conversationSelection?.Clear(); RenderMessagesNow();
            if (_messages.Count != 24) throw new InvalidOperationException("Die Zeitachse hat bereits vor ihrem Index den vollständigen Verlauf geladen.");
            await LoadPromptTimelineIndexAsync();
            try { await WaitForPagingSmokeAsync(() => _promptTimelineMarkers.Count == 40, "Der kompakte Index enthält nicht alle Prompts."); }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException("Der kompakte Promptindex konnte nicht gerendert werden: " + JsonSerializer.Serialize(new
                {
                    session = _session, expectedSession = fixture.Id, owner = ConversationViewKey, _promptTimelineOwner,
                    _promptIndexLoadedOwner, indexLoading = _promptIndicesLoading.Contains(_session),
                    indexed = _promptTimelineEntries.Count, markers = _promptTimelineMarkers.Count, messages = _messages.Count,
                    lastEvent = _lastUiEventType, lastRevision = _lastUiEventRevision, activeSubagent = _activeSubagentId,
                    incomingAllowed = AssistantProtocol.IsIncomingTypeAllowed("conversation.prompts"),
                    outgoingAllowed = AssistantProtocol.IsOutgoingTypeAllowed("conversation.prompts"),
                    clientScoped = AssistantProtocol.IsClientScopedOutgoingType("conversation.prompts"),
                }, JsonOptions), exception);
            }
            var first = fixture.Messages[12].Id.ToString();
            if (_messages.ContainsKey(first) || _messageViews.ContainsKey(first))
                throw new InvalidOperationException("Das ältere Zeitachsenziel ist entgegen der Prüfvoraussetzung bereits geladen.");
            QueueConversationLayoutProjection(follow: true);
            var firstButton = _promptTimelineMarkers[first].HitTarget;
            if (new ButtonAutomationPeer(firstButton).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der Zeitachsenmarker ist nicht nativ bedienbar.");
            invoke.Invoke();
            await WaitForPagingSmokeAsync(() => AtTarget(first), "Der Klick hat den älteren Prompt nicht gezielt nachgeladen und angesprungen.");
            if (_messages.Count >= 80 || _scrollNewSessionToLatest || CanFollowConversationTail)
                throw new InvalidOperationException("Der Promptsprung lädt die Vollhistorie oder folgt wieder dem Laufende.");

            var later = fixture.Messages[18].Id.ToString();
            ScrollToPrompt(later);
            await WaitForPagingSmokeAsync(() => AtTarget(later), "Ein bereits geladenes Ziel wurde nicht exakt angesteuert.");
            var preceding = _messageViews[fixture.Messages[13].Id.ToString()].View;
            preceding.Margin = new Thickness(0, 0, 0, 240);
            ConversationContent.UpdateLayout();
            await WaitForPagingSmokeAsync(() => AtTarget(later), "Verspätetes Layout hat den gewählten Prompt verschoben.");
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = fixture.Id,
                messageId = fixture.Messages[^1].Id, contentAppend = "\n\nWeitere gestreamte Antwortteile.", updatedAt = DateTimeOffset.UtcNow }));
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => AtTarget(later), "Ein weiterer Antwortteil zieht die Zeitachse wieder zum Ende.");

            var gap = _promptHistoryGaps.Values.Single();
            await LoadPromptTimelineGapAsync(gap);
            await WaitForPagingSmokeAsync(() => AtTarget(later), "Bedarfsgesteuertes Füllen der Seitenlücke verschiebt das gewählte Ziel.");
            if (!_messages.ContainsKey(fixture.Messages[40].Id.ToString()) || _promptHistoryGaps.ContainsKey(gap.Key))
                throw new InvalidOperationException("Die Lücke zwischen Zielseite und zuletzt geladenem Verlauf wurde nicht geschlossen.");

            var old = fixture.Messages[2].Id.ToString();
            var winner = fixture.Messages[4].Id.ToString();
            ScrollToPrompt(old);
            var obsolete = _promptNavigation!;
            ScrollToPrompt(winner);
            await WaitForPagingSmokeAsync(() => AtTarget(winner) && _promptTimelineNavigationTask?.IsCompleted == true,
                "Bei schnellen aufeinanderfolgenden Sprüngen gewinnt nicht das zuletzt gewählte Ziel.");
            var staleMessage = Guid.NewGuid().ToString();
            ApplyEvent("conversation.promptPage", JsonSerializer.SerializeToElement(new { sessionId = fixture.Id,
                promptId = old, navigationIntent = obsolete.Intent, messages = new[] {
                    new { id = staleMessage, sessionId = fixture.Id, role = "user", content = "Veraltete Antwort", createdAt = DateTimeOffset.UtcNow } } }));
            if (_messages.ContainsKey(staleMessage) || !AtTarget(winner))
                throw new InvalidOperationException("Die Antwort eines veralteten Promptklicks verändert das aktuelle Ziel.");
            if ((await chats.GetSessionAsync(fixture.Id, _lifetime.Token))!.ConversationRevision != fixture.Revision
                || (await snapshots.GetPromptsAsync(fixture.Id, _lifetime.Token)).Count != 40)
                throw new InvalidOperationException("Zeitachsennavigation hat den gespeicherten Verlauf oder Modellkontext verändert.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-prompt-timeline-validation.json"),
                JsonSerializer.Serialize(new { passed = true, initialMessages = 24, indexedPrompts = 40,
                    unloadedTargetLoadedDirectly = true, fullHistoryNotLoaded = true, nativeMarkerInvoked = true,
                    lateLayoutKeepsTarget = true, streamingKeepsTarget = true, latestClickWins = true,
                    gapFilledOnDemand = true,
                    stalePageRejected = true, durableHistoryUnchanged = true, modelRequests = 0 }), _lifetime.Token);
        }
        finally
        {
            CancelPromptTimelineNavigation();
            if (!_disposed && originalSession != Guid.Empty) await CommandAsync("session.open", new { sessionId = originalSession });
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }

        bool AtTarget(string id) => _promptNavigation?.Id == id && HasPromptTimelineTarget
            && _messageViews.TryGetValue(id, out var view) && MessagesPanel.Children.Contains(view.View)
            && Math.Abs(ConversationScroll.VerticalOffset - Math.Clamp(MessageOffset(view.View), 0, ConversationScroll.ScrollableHeight)) <= 2;
    }
}
