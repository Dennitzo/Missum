using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyCompactionReceiptSmokeAsync()
    {
        await SettingsPage.VerifyStatusAccentSmokeAsync(LayoutRoot);
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Der Komprimierungstest benötigt ein isoliertes Profil ohne Modellstart.");
        var original = _snapshot.Clone();
        var previousFixture = _selectionInputSmokeActive;
        var chats = App.Current.GetService<IChatRepository>();
        _selectionInputSmokeActive = true;
        try
        {
            var fixture = await Task.Run(async () =>
            {
                var session = await chats.CreateSessionAsync("Statische Kontextkomprimierung", ChatMode.Coding, _lifetime.Token);
                var other = await chats.CreateSessionAsync("Andere Sitzung", ChatMode.Coding, _lifetime.Token);
                await chats.AddMessageAsync(session.Id, ChatRole.User, "Prüfe den gespeicherten Forschungsstand.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var normal = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Gesicherter Forschungsstand.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var step = new AssistantToolStep("normal-compaction", "context.compact", "running",
                    "Arbeitskontext wird verdichtet.", ContentOffset: normal.Content.Length, UpdatedAt: DateTimeOffset.UtcNow);
                await chats.SaveToolStepAsync(normal.Id, step, _lifetime.Token);
                return new { Session = session, Other = other, Message = normal, Step = step };
            }, _lifetime.Token);

            await Open(fixture.Session.Id);
            var toolsMenu = ShowComposerTools();
            try
            {
                await WaitForNativePreviewFrameAsync();
                var compactAction = ((StackPanel)((ScrollViewer)toolsMenu.Content).Content).Children.OfType<Button>()
                    .Single(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Kontext komprimieren");
                if (!compactAction.IsEnabled) throw new InvalidOperationException("Die manuelle Komprimierung fehlt im Plus-Menü der geöffneten Sitzung.");
            }
            finally { toolsMenu.Hide(); }
            var messageId = fixture.Message.Id.ToString();
            var running = await Receipt(messageId, "Kontext wird komprimiert");
            var height = running.ActualHeight;
            var slot = _messageBlocks[messageId]["slot:tool:normal-compaction"];
            var completed = fixture.Step with { Status = "completed", Detail = "Arbeitskontext verdichtet.",
                CompletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            await Publish(fixture.Message, completed);
            var finished = await Receipt(messageId, "Kontextkomprimierung abgeschlossen");
            if (_running || !ReferenceEquals(running, finished) || Math.Abs(height - finished.ActualHeight) > 1
                || !ReferenceEquals(slot, _messageBlocks[messageId]["slot:tool:normal-compaction"]))
                throw new InvalidOperationException("Statuswechsel verändert den festen Komprimierungsplatz oder aktiviert einen AI-Lauf.");

            foreach (var state in new[] { "cancelled", "failed" })
            {
                var step = fixture.Step with { Id = "compaction-" + state };
                await Publish(fixture.Message, step);
                await Publish(fixture.Message, step with { Status = state, CompletedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow });
            }
            await Publish(fixture.Message, fixture.Step with { Id = "compaction-not-required", Status = "completed",
                Detail = MissumAiAssistantService.NoCompactionDetailPrefix + " Der Kontext ist bereits kompakt.",
                Explanation = MissumAiAssistantService.NoCompactionDetailPrefix,
                CompletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Open(fixture.Other.Id);
            await Open(fixture.Session.Id);
            await Receipt(messageId, "Kontextkomprimierung abgeschlossen");
            await Receipt(messageId, "Kontextkomprimierung abgebrochen");
            await Receipt(messageId, "Kontextkomprimierung fehlgeschlagen");
            await Receipt(messageId, "Keine Kontextkomprimierung erforderlich");
            var stored = await chats.GetMessageAsync(fixture.Message.Id, _lifetime.Token);
            if (stored?.ToolSteps?.Count != 4 || stored.ToolSteps.Any(step => step.Status == "running")
                || Descendants(_messageViews[messageId].View).OfType<CompactionReceiptView>().Count() != 4)
                throw new InvalidOperationException("Sitzungswechsel verliert oder dupliziert gespeicherte Komprimierungen.");
            await SaveMathPreviewAsync(_messageViews[messageId].View, "native-compaction-preview.png");

            foreach (var status in new[] { MessageStatus.Failed, MessageStatus.Cancelled, MessageStatus.Interrupted })
            {
                var manual = await chats.AddMessageAsync(fixture.Session.Id, ChatRole.Assistant, "", status,
                    cancellationToken: _lifetime.Token);
                var manualStep = fixture.Step with { Id = "manual-compaction:" + manual.Id.ToString("N"),
                    Status = status.ToString().ToLowerInvariant(), ContentOffset = 0,
                    CompletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                await chats.SaveToolStepAsync(manual.Id, manualStep, _lifetime.Token);
                if (status == MessageStatus.Failed)
                    await chats.UpdateMessageAsync(manual.Id, "", status, "Missum AI Server returned HTTP 404.", _lifetime.Token);
                await Open(fixture.Other.Id);
                await Open(fixture.Session.Id);
                var manualId = manual.Id.ToString();
                await Receipt(manualId, status == MessageStatus.Failed ? "Kontextkomprimierung fehlgeschlagen"
                    : "Kontextkomprimierung abgebrochen");
                if (_messageBlocks[manualId].ContainsKey("continuation")
                    || Descendants(_messageViews[manualId].View).OfType<ContinuationToolStepView>().Any())
                    throw new InvalidOperationException("Eine manuelle Komprimierung bietet nach dem Nachladen fälschlich AI-Fortsetzung an.");
            }

            // A single large message takes the real virtual/lazy-render path.
            var longMessage = await chats.AddMessageAsync(fixture.Other.Id, ChatRole.Assistant,
                string.Concat(Enumerable.Repeat("Ein überprüftes Ergebnis bleibt mit seinem ursprünglichen Quellbereich erhalten. ", 120)),
                MessageStatus.Completed, cancellationToken: _lifetime.Token);
            var longStep = fixture.Step with { Id = "virtual-compaction", ContentOffset = longMessage.Content.Length };
            await chats.SaveToolStepAsync(longMessage.Id, longStep, _lifetime.Token);
            await Open(fixture.Other.Id);
            var virtualId = longMessage.Id.ToString();
            var virtualReceipt = await Receipt(virtualId, "Kontext wird komprimiert");
            if (!_virtualMessages.ContainsKey(virtualId)) throw new InvalidOperationException("Der virtuelle Chatpfad wurde nicht geprüft.");
            await Publish(longMessage, longStep with { Status = "completed", CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow });
            var virtualCompleted = await Receipt(virtualId, "Kontextkomprimierung abgeschlossen");
            if (!ReferenceEquals(virtualReceipt, virtualCompleted))
                throw new InvalidOperationException("Virtuelle Statusaktualisierung ersetzt die feste Zeile.");
            await Open(fixture.Session.Id);
            await Open(fixture.Other.Id);
            await Receipt(virtualId, "Kontextkomprimierung abgeschlossen");
            await SaveMathPreviewAsync(LayoutRoot, "native-compaction-virtual-preview.png");

            await VerifyThinkingIndicatorSmokeAsync(_snapshot.Clone());
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-compaction-validation.json"),
                JsonSerializer.Serialize(new { passed = true, completedReceipt = true, abortedReceipt = true,
                    failedReceipt = true, databaseReload = true, virtualizedReceipt = true,
                    sameRowOnStatusChange = true, noInteraction = true, noDeferredDetails = true,
                    hiddenReasoning = true, manualComposerAction = true, noOpReceipt = true, settingsAccentChecked = true,
                    manualCompactionNeverResumable = true, modelRequests = 0 }));
        }
        finally
        {
            ApplyEvent("session.changed", original);
            _selectionInputSmokeActive = previousFixture;
        }

        async Task Open(Guid sessionId)
        {
            if (!await CommandAsync("session.open", new { sessionId })) throw new InvalidOperationException("Testsitzung konnte nicht geladen werden.");
            ShowParentConversation(); ShowChatView(); _conversationSelection?.Clear();
            RenderMessagesNow(); UpdateLayout();
            await WaitForNativePreviewFrameAsync();
        }

        async Task Publish(ChatMessage message, AssistantToolStep step)
        {
            await chats.SaveToolStepAsync(message.Id, step, _lifetime.Token);
            await _coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message, ToolStep: step),
                (type, data, id) => EmitAsync(type, data, id), "compaction-smoke");
            RenderMessagesNow(); UpdateLayout();
        }

        async Task<CompactionReceiptView> Receipt(string messageId, string label)
        {
            CompactionReceiptView? result = null;
            await WaitForPagingSmokeAsync(() =>
            {
                RenderMessagesNow(); UpdateLayout();
                ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, disableAnimation: true);
                RefreshVirtualConversationViewport();
                result = _messageViews.TryGetValue(messageId, out var message)
                    ? Descendants(message.View).OfType<CompactionReceiptView>().FirstOrDefault(view => view.Label == label) : null;
                return result is not null && result.ActualHeight > 0;
            }, "Statische Komprimierungszeile fehlt: " + label);
            if (result!.IsHitTestVisible || result.Children.Count != 2 || result.ActualHeight > 32
                || Descendants(result).OfType<Button>().Any() || Descendants(result).OfType<ScrollViewer>().Any())
                throw new InvalidOperationException("Komprimierung enthält eine Interaktion, Werkzeugdetails oder einen leeren Detailbereich.");
            return result;
        }
    }
}
