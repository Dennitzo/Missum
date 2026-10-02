using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyContinuationSmokeAsync(JsonElement original)
    {
        var session = Guid.NewGuid();
        var previousAssistant = Guid.NewGuid();
        var currentAssistant = Guid.NewGuid();
        var currentUser = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        object Message(Guid id, string role, string content, string status, int order) => new
        {
            id, sessionId = session, role, content, status,
            createdAt = now.AddSeconds(order), updatedAt = now.AddSeconds(order),
        };
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(session);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("coding");
        snapshot["isAiBusy"] = JsonSerializer.SerializeToElement(false);
        snapshot["runQueue"] = JsonSerializer.SerializeToElement(new { active = (object?)null });
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[]
        {
            Message(Guid.NewGuid(), "user", "Erster Auftrag", "completed", -4),
            Message(previousAssistant, "assistant", "Bisheriger Stand des ersten Auftrags.", "cancelled", -3),
            Message(currentUser, "user", "Analysiere das Projekt und verbessere den nächsten Schritt.", "completed", -2),
            Message(currentAssistant, "assistant", "Die Analyse wurde begonnen. Der bisherige Text bleibt erhalten.", "interrupted", -1),
        });
        var key = currentAssistant.ToString();
        try
        {
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow();
            var step = (ContinuationToolStepView)_messageBlocks[key]["continuation"];
            if (step.Visibility != Visibility.Visible || !step.ContinueButton.IsEnabled || !step.ContinueButton.IsTabStop
                || AutomationProperties.GetName(step.ContinueButton) != "AI-Antwort fortsetzen")
                throw new InvalidOperationException("The latest interrupted answer lacks an accessible continuation tool step.");
            var previous = (ContinuationToolStepView)_messageBlocks[previousAssistant.ToString()]["continuation"];
            if (previous.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("An older stopped answer must not offer continuation after a newer prompt.");
            var icon = ((Grid)step.ContinueButton.Content).Children.OfType<FontIcon>().Single();
            if (icon.Foreground is not SolidColorBrush brush || brush.Color != NativeIconPalette.ColorFor("success"))
                throw new InvalidOperationException("The continuation icon is not visibly colored.");
            UpdateLayout();
            await SaveMathPreviewAsync(ConversationScroll, "native-continuation-preview.png");

            _continuationRequestPending = true;
            _continuationMessageId = key;
            RefreshContinuationSteps();
            if (step.ContinueButton.IsEnabled)
                throw new InvalidOperationException("A repeated continuation click is enabled while preparing.");
            _continuationRequestPending = false;
            _continuationMessageId = null;
            _running = true;
            RefreshContinuationSteps();
            if (step.Visibility != Visibility.Collapsed || step.ContinueButton.IsEnabled)
                throw new InvalidOperationException("Continuation remains enabled during an active model run.");
            _running = false;
            ApplyEvent("conversation.messageCommitted", JsonSerializer.SerializeToElement(new
            {
                sessionId = session, message = Message(currentAssistant, "assistant", "Der Auftrag ist nun abgeschlossen.", "completed", 0),
            }));
            RenderMessagesNow();
            if (_messageBlocks[key].ContainsKey("continuation") || _messages.Values.Count(item => S(item, "role") == "user") != 2
                || !_messages.ContainsKey(currentUser.ToString()))
                throw new InvalidOperationException("Completion kept a stale continuation step or changed the user prompt history.");

            var failed = JsonSerializer.SerializeToElement(new { role = "assistant", status = "failed", content = "**Fehler:** Verbindung fehlgeschlagen." });
            if (IsResumableAssistantStatus(failed))
                throw new InvalidOperationException("An error placeholder incorrectly offers continuation.");
        }
        finally
        {
            _continuationRequestPending = false;
            _continuationMessageId = null;
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
        }
    }
}
