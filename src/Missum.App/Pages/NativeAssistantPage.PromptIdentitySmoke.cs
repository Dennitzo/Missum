using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyPromptIdentitySmokeAsync(JsonElement original)
    {
        var isolated = Environment.GetEnvironmentVariable("ASSISTANT_DATA_ROOT");
        if (!IsMessageFooterSmoke || string.IsNullOrWhiteSpace(isolated)
            || !string.Equals(Path.GetFullPath(isolated), Path.GetFullPath(App.Current.DataDirectory), StringComparison.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Promptprüfung benötigt ein isoliertes Profil ohne Modellstart.");
        var chats = App.Current.GetService<IChatRepository>();
        var snapshots = App.Current.GetService<IConversationSnapshotRepository>();
        var previousFixture = _selectionInputSmokeActive;
        var sessions = new List<Guid>();
        _selectionInputSmokeActive = true;
        try
        {
            var session = await chats.CreateSessionAsync("Persistierte Promptidentitäten", ChatMode.General, _lifetime.Token);
            var other = await chats.CreateSessionAsync("Leere Vergleichssitzung", ChatMode.General, _lifetime.Token);
            sessions.Add(session.Id); sessions.Add(other.Id);
            await ShowStoredSessionAsync(session.Id);
            AssertPromptIds();
            ApplyEvent("chat.queued", JsonSerializer.SerializeToElement(new { sessionId = session.Id }, JsonOptions));
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = session.Id, runStatus = "Fehlgeschlagen" }, JsonOptions));
            AssertPromptIds();

            const string content = "Prüfe die Gleichung schrittweise.\n\nErhalte die Voraussetzungen.";
            var first = await chats.AddMessageAsync(session.Id, ChatRole.User, content, MessageStatus.Completed, cancellationToken: _lifetime.Token);
            await ShowStoredSessionAsync(session.Id);
            AssertPromptIds(first.Id.ToString("D"));
            var firstDto = _messages[first.Id.ToString("D")];
            for (var replay = 0; replay < 3; replay++)
            {
                ApplyEvent("chat.started", JsonSerializer.SerializeToElement(new { sessionId = session.Id, userMessage = firstDto }, JsonOptions));
                ApplyEvent("conversation.messageCommitted", JsonSerializer.SerializeToElement(new { sessionId = session.Id, message = firstDto }, JsonOptions));
                await ApplyStoredIndexAsync(session.Id);
                AssertPromptIds(first.Id.ToString("D"));
            }
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = session.Id, runStatus = "Abgebrochen" }, JsonOptions));
            AssertPromptIds(first.Id.ToString("D"));

            // Equal text is a legitimate second input, never a deduplication key.
            var second = await chats.AddMessageAsync(session.Id, ChatRole.User, content, MessageStatus.Completed, cancellationToken: _lifetime.Token);
            await ShowStoredSessionAsync(session.Id);
            AssertPromptIds(first.Id.ToString("D"), second.Id.ToString("D"));
            ApplyEvent("conversation.prompts", JsonSerializer.SerializeToElement(new { sessionId = session.Id, prompts = Array.Empty<ConversationPrompt>() }, JsonOptions));
            AssertPromptIds(first.Id.ToString("D"), second.Id.ToString("D"));
            await ApplyStoredIndexAsync(session.Id);
            var indexed = await snapshots.GetPromptsAsync(session.Id, _lifetime.Token);
            var ordered = _promptTimelineMarkers.OrderBy(item => item.Value.Top).ToArray();
            if (!ordered.Select(item => item.Key).SequenceEqual(indexed.Select(prompt => prompt.Id.ToString("D"))))
                throw new InvalidOperationException("Die Promptreihenfolge entspricht nicht dem gespeicherten Index.");
            for (var index = 0; index < ordered.Length; index++)
                if (!AutomationProperties.GetName(ordered[index].Value.HitTarget).StartsWith($"Zu Prompt {index + 1} springen:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Die Zeitachsennummerierung wurde nach dem Indexabgleich nicht aktualisiert.");

            var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Ich prüfe die erste Voraussetzung.",
                MessageStatus.Streaming, cancellationToken: _lifetime.Token);
            await ShowStoredSessionAsync(session.Id);
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = session.Id, messageId = assistant.Id,
                contentAppend = " Die Antwort läuft weiter.", updatedAt = DateTimeOffset.UtcNow }, JsonOptions));
            AssertPromptIds(first.Id.ToString("D"), second.Id.ToString("D"));
            var stepId = "steering-" + Guid.NewGuid().ToString("N");
            const string steering = "Vergleiche zusätzlich den Grenzfall.";
            await chats.SaveToolStepDeltaAsync(assistant.Id, new(stepId, "assistant.steering", "completed", steering,
                ContentOffset: assistant.Content.Length, UpdatedAt: DateTimeOffset.UtcNow), _lifetime.Token);
            var steeringId = MissumAiAssistantService.SteeringPromptMessageId(assistant.Id, stepId).ToString("D");
            await ShowStoredSessionAsync(session.Id);
            await ApplyStoredIndexAsync(session.Id);
            AssertPromptIds(first.Id.ToString("D"), second.Id.ToString("D"), steeringId);
            if (new ButtonAutomationPeer(_promptTimelineMarkers[steeringId].HitTarget).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der gespeicherte Umlenkungsprompt besitzt keinen bedienbaren Zeitachsenmarker.");
            invoke.Invoke();
            await WaitForPagingSmokeAsync(() => _promptTimelineNavigationTask?.IsCompleted == true
                && _promptNavigation?.Id == steeringId && double.IsFinite(_promptTimelineMarkers[steeringId].TargetOffset),
                "Der gespeicherte Umlenkungsprompt lässt sich nicht über seine stabile Identität ansteuern.");
            CancelPromptTimelineNavigation();

            var revision = (await chats.GetSessionAsync(session.Id, _lifetime.Token))!.ConversationRevision;
            await ApplyStoredIndexAsync(other.Id);
            AssertPromptIds(first.Id.ToString("D"), second.Id.ToString("D"), steeringId);
            await ShowStoredSessionAsync(other.Id);
            AssertPromptIds();
            await ShowStoredSessionAsync(session.Id);
            AssertPromptIds(first.Id.ToString("D"), second.Id.ToString("D"), steeringId);

            var agentId = "prompt-identity:" + Guid.NewGuid().ToString("N");
            var childPrompt = Guid.NewGuid().ToString("D");
            var childAnswer = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow;
            ObserveSubagentSnapshot(JsonSerializer.SerializeToElement(new
            {
                sessionId = session.Id, parentMessageId = assistant.Id, agentId, runId = Guid.NewGuid(),
                title = "Unabhängige Prüfung", status = "completed", isRunning = false,
                messages = new object[]
                {
                    new { id = childPrompt, sessionId = session.Id, role = "user", status = "completed", content = "Prüfe den Grenzfall.", createdAt = now, updatedAt = now },
                    new { id = childAnswer, sessionId = session.Id, role = "assistant", status = "completed", content = "Der Grenzfall ist geprüft.", createdAt = now.AddTicks(1), updatedAt = now.AddTicks(1) },
                },
            }, JsonOptions));
            await ActivateSubagentTabAsync(_subagents[agentId]);
            AssertPromptIds(childPrompt);
            await ApplyStoredIndexAsync(session.Id);
            AssertPromptIds(childPrompt);
            ShowParentConversation();
            AssertPromptIds(first.Id.ToString("D"), second.Id.ToString("D"), steeringId);
            if ((await chats.GetSessionAsync(session.Id, _lifetime.Token))!.ConversationRevision != revision)
                throw new InvalidOperationException("Die Zeitachsenprüfung hat beim Navigieren den gespeicherten Verlauf verändert.");

            // An accepted authoritative empty snapshot clears the same owner's
            // retained index. An old, empty index response above must not do so.
            var cleared = _snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            cleared["messages"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            cleared["auxiliaryMessages"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            cleared["subagents"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            cleared["conversationRevision"] = JsonSerializer.SerializeToElement(revision + 1);
            cleared["historyPage"] = JsonSerializer.SerializeToElement(new { hasMore = false, totalMessages = 0 });
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(cleared));
            AssertPromptIds();
            await ShowStoredSessionAsync(other.Id);
            AssertPromptIds();

            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-prompt-identity-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, renderer = "WinUI3", firstStoredPromptHasOneMarker = true, noProvisionalPrompts = true,
                    repeatedDatabaseEventsIdempotent = true, identicalTextRetainsDistinctIds = true,
                    staleEmptyIndexKeepsNewPrompts = true, chronologicalNumbering = true,
                    answerStreamingKeepsPromptCount = true, storedSteeringHasStableMarker = true,
                    steeringMarkerNavigates = true, foreignIndexIgnored = true, sessionIsolation = true,
                    subagentIsolation = true, authoritativeEmptySnapshotClearsMarkers = true,
                    navigationLeavesDurableRevisionUnchanged = true, modelRequests = 0, userDataModified = false,
                }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            CancelPromptTimelineNavigation();
            ShowParentConversation();
            ApplyEvent("state.snapshot", original);
            ShowChatView(); RenderMessagesNow();
            foreach (var session in sessions) await chats.DeleteSessionAsync(session, _lifetime.Token);
            _selectionInputSmokeActive = previousFixture;
        }

        async Task ShowStoredSessionAsync(Guid sessionId)
        {
            ShowParentConversation();
            var stored = JsonSerializer.SerializeToElement(await _coordinator.BuildConversationForHostAsync(sessionId, _lifetime.Token), JsonOptions);
            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            foreach (var field in stored.EnumerateObject()) snapshot[field.Name] = field.Value.Clone();
            snapshot["isRunning"] = JsonSerializer.SerializeToElement(false);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            ShowChatView(); RenderMessagesNow(); UpdateLayout();
            await WaitForNativePreviewFrameAsync();
        }

        async Task ApplyStoredIndexAsync(Guid sessionId)
        {
            var prompts = await snapshots.GetPromptsAsync(sessionId, _lifetime.Token);
            ApplyEvent("conversation.prompts", JsonSerializer.SerializeToElement(new { sessionId, prompts }, JsonOptions));
            RenderMessagesNow();
        }

        void AssertPromptIds(params string[] expected)
        {
            RenderMessagesNow(); RenderPromptTimeline();
            if (_promptTimelineMarkers.Count != expected.Length || !_promptTimelineMarkers.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected)
                || _messages.Keys.Any(id => id.StartsWith("pending:", StringComparison.Ordinal))
                || _promptTimelineMarkers.Keys.Any(id => id.StartsWith("pending:", StringComparison.Ordinal)))
                throw new InvalidOperationException("Die Zeitachse zeigt keine eindeutigen gespeicherten Promptidentitäten: "
                    + JsonSerializer.Serialize(new { expected, actual = _promptTimelineMarkers.Keys, messages = _messages.Count }, JsonOptions));
            if ((PromptTimeline.Visibility == Visibility.Visible) != (expected.Length > 0))
                throw new InvalidOperationException("Eine leere oder belegte Zeitachse hat die falsche Sichtbarkeit.");
        }
    }
}
