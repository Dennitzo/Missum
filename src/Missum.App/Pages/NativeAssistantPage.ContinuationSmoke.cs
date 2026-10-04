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
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(false);
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
            _continuationSessionId = session;
            _continuationServerStarted = false;
            RefreshContinuationSteps();
            SetRunning();
            if (step.ContinueButton.IsEnabled || _running || !IsContinuationPreparing || SendIcon.Glyph != "\uE71A"
                || AutomationProperties.GetName(SendButton) != "Vorbereitung stoppen")
                throw new InvalidOperationException("A repeated continuation click is enabled while preparing.");
            var draft = Composer.Text;
            _rendering = true; Composer.Text = "Mein vorhandener Entwurf bleibt erhalten."; _rendering = false;
            SetRunning();
            if (SendIcon.Glyph != "\uE71A" || !IsContinuationPreparing)
                throw new InvalidOperationException("A pending continuation must remain cancellable without sending a new draft or pretending the server run has started.");
            var preparingSnapshot = new Dictionary<string, JsonElement>(snapshot)
            {
                ["isRunning"] = JsonSerializer.SerializeToElement(true),
                ["isAiBusy"] = JsonSerializer.SerializeToElement(true),
                ["activeRunSessionId"] = JsonSerializer.SerializeToElement(session),
                ["activeRunId"] = JsonSerializer.SerializeToElement((string?)null),
                ["runMessageId"] = JsonSerializer.SerializeToElement(currentAssistant),
                ["runStatus"] = JsonSerializer.SerializeToElement("AI-Modell und Dienste werden vorbereitet"),
                ["draft"] = JsonSerializer.SerializeToElement(Composer.Text),
            };
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(preparingSnapshot)); RenderMessagesNow(); UpdateLayout();
            if (!_running || !IsContinuationPreparing || SendIcon.Glyph != "\uE71A"
                || AutomationProperties.GetName(SendButton) != "Vorbereitung stoppen"
                || Composer.Text != "Mein vorhandener Entwurf bleibt erhalten." || StatusText.Visibility != Visibility.Visible)
                throw new InvalidOperationException("A real busy-client snapshot must keep preparation cancellable until a server run has been admitted.");
            await SaveMathPreviewAsync(AssistantFrame, "native-continuation-preparing-preview.png");
            var foreignSnapshot = new Dictionary<string, JsonElement>(preparingSnapshot)
            {
                ["activeSessionId"] = JsonSerializer.SerializeToElement(Guid.NewGuid()),
                ["isRunning"] = JsonSerializer.SerializeToElement(false),
                ["messages"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
            };
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(foreignSnapshot)); RenderMessagesNow();
            if (IsContinuationPreparing || SendIcon.Glyph == "\uE71A")
                throw new InvalidOperationException("Another session must not inherit the pending continuation's stop state.");
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(preparingSnapshot)); RenderMessagesNow();
            if (!IsContinuationPreparing || SendIcon.Glyph != "\uE71A" || Composer.Text != "Mein vorhandener Entwurf bleibt erhalten.")
                throw new InvalidOperationException("Returning to the preparation session must restore its pending stop state and draft.");
            var acceptedSnapshot = new Dictionary<string, JsonElement>(preparingSnapshot)
            {
                ["activeRunId"] = JsonSerializer.SerializeToElement("run-continuation-smoke"),
                ["messages"] = JsonSerializer.SerializeToElement(new[]
                {
                    Message(Guid.NewGuid(), "user", "Erster Auftrag", "completed", -4),
                    Message(previousAssistant, "assistant", "Bisheriger Stand des ersten Auftrags.", "cancelled", -3),
                    Message(currentUser, "user", "Analysiere das Projekt und verbessere den nächsten Schritt.", "completed", -2),
                    Message(currentAssistant, "assistant", "Die Analyse wurde begonnen. Der bisherige Text bleibt erhalten.", "streaming", -1),
                }),
            };
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(acceptedSnapshot)); RenderMessagesNow();
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
            { sessionId = session, messageId = currentAssistant, runStatus = "Modell wird geladen", runDetail = "Ausgewähltes Modell wird geladen." }));
            UpdateLayout();
            if (IsContinuationPreparing || StatusText.Visibility != Visibility.Visible
                || !StatusText.Text.StartsWith("Modell wird geladen", StringComparison.Ordinal)
                || _messages[currentAssistant.ToString()].GetProperty("status").GetString() != "streaming")
                throw new InvalidOperationException("Model loading must remain visible in chat after acceptance, even with an active answer header.");
            await SaveMathPreviewAsync(AssistantFrame, "native-continuation-loading-preview.png");
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
            { sessionId = session, messageId = currentAssistant, runStatus = "Coding-Modell wird geladen", runDetail = "Ausgewähltes Modell wird geladen." }));
            if (StatusText.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Coding model loading must remain visible in the same chat status.");
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new
            { sessionId = session, messageId = currentAssistant, runStatus = "Denkt nach" }));
            if (StatusText.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Ordinary generation must clear the loading notice without adding a duplicate status row.");
            _rendering = true; Composer.Text = draft; _rendering = false;
            _continuationRequestPending = false;
            _continuationMessageId = null;
            _continuationSessionId = null;
            _continuationServerStarted = false;
            _running = true;
            RefreshContinuationSteps();
            SetRunning();
            if (_messageBlocks[key].TryGetValue("continuation", out var activeContinuation)
                && activeContinuation is ContinuationToolStepView { Visibility: Visibility.Visible })
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

            var receiptView = new ToolStepView(); MessagesPanel.Children.Add(receiptView);
            try
            {
                (string Status, string Title)[] receiptStates =
                [
                    ("running", "Fortsetzung wird vorbereitet"), ("pending", "Fortsetzung wird vorbereitet"),
                    ("failed", "Fortsetzen fehlgeschlagen"), ("cancelled", "Fortsetzung abgebrochen"),
                    ("completed", "Lauf fortgesetzt"),
                ];
                foreach (var receipt in receiptStates)
                {
                    receiptView.Update(JsonSerializer.SerializeToElement(new
                    { id = "continuation-smoke", tool = "assistant.continuation", status = receipt.Status, detail = "Bisheriger Inhalt bleibt erhalten." }));
                    UpdateLayout();
                    var labels = Descendants(receiptView).OfType<TextBlock>().Select(label => label.Text).ToArray();
                    if (!labels.Any(label => label.StartsWith(receipt.Title, StringComparison.Ordinal))
                        || receipt.Status != "completed" && labels.Any(label => label.StartsWith("Lauf fortgesetzt", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Only an accepted continuation receipt may claim that the run was continued.");
                }
            }
            finally { MessagesPanel.Children.Remove(receiptView); }
        }
        finally
        {
            _continuationRequestPending = false;
            _continuationMessageId = null;
            _continuationSessionId = null;
            _continuationServerStarted = false;
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
        }
    }
}
