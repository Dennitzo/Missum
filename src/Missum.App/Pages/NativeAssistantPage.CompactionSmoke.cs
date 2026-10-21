using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyCompactionReceiptSmokeAsync()
    {
        if (!IsMessageFooterSmoke) throw new InvalidOperationException("Isoliertes Profil erforderlich.");
        var original = _snapshot.Clone();
        _selectionInputSmokeActive = true;
        ShowParentConversation(); ShowChatView(); _conversationSelection?.Clear();
        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(false);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
        {
            id = messageId, sessionId = owner, role = "assistant", content = "Gesicherter Forschungsstand.", status = "completed", createdAt = at,
            toolSteps = new[] { new { id = "normal-compaction", tool = "context.compact", status = "running", detail = "Arbeitskontext wird verdichtet.", contentOffset = 0 } },
        } });
        ApplyEvent("session.changed", JsonSerializer.SerializeToElement(snapshot));
        RenderMessagesNow();
        UpdateLayout();
        if (!_messageBlocks[messageId].TryGetValue("tool:normal-compaction", out var normalControl)) throw new InvalidOperationException("Komprimierungsschritt fehlt.");
        RenderMessagesNow(); UpdateLayout();
        var runningLabels = Descendants(_messageViews[messageId].View).OfType<TextBlock>().Select(text => text.Text).ToArray();
        if (!runningLabels.Any(text => text.StartsWith("Kontext komprimieren", StringComparison.Ordinal)))
            throw new InvalidOperationException("Laufende Komprimierungstitel fehlen.");
        ApplyEvent("conversation.toolStepChanged", JsonSerializer.SerializeToElement(new
        {
            sessionId = _session, messageId,
            toolStep = new { id = "normal-compaction", tool = "context.compact", status = "completed", detail = "Arbeitskontext verdichtet.", contentOffset = 0, updatedAt = at.AddSeconds(1) },
        }));
        RenderMessagesNow(); UpdateLayout();
        var labels = Descendants(_messageViews[messageId].View).OfType<TextBlock>().Select(text => text.Text).ToArray();
        if (_running || !labels.Contains("Kontextkomprimierung abgeschlossen")
            || !ReferenceEquals(normalControl, _messageBlocks[messageId]["tool:normal-compaction"]))
            throw new InvalidOperationException("Hintergrundkomprimierung muss dauerhaft ohne aktiven AI-Lauf angezeigt werden.");
        await SaveMathPreviewAsync(_messageViews[messageId].View, "native-compaction-preview.png");
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-compaction-validation.json"),
            JsonSerializer.Serialize(new { passed = true, completedRunReceipt = true, duplicates = 0, markedRunActive = _running }));
        ApplyEvent("session.changed", original);
        _selectionInputSmokeActive = false;
    }
}
