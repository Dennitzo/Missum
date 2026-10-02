using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal static string[] ScienceIntroductionParts(string title)
    {
        // This is a UI introduction to the research workflow, not a claim that
        // sources were already inspected or that the model found an answer.
        var heading = title.ReplaceLineEndings(" ").Replace("*", "").Replace("#", "").Trim();
        return [$"**{heading}**\n\n",
            "Ich untersuche deine Frage schrittweise: Zuerst kläre ich die Teilfragen, dann suche und prüfe ich passende Quellen. "
            + "Die Ergebnisse und ihre Grenzen fließen fortlaufend in die wissenschaftliche Publikation im Tab Publikation ein. "
            + "Im Tab Simulation werden die dazugehörigen Python-Auswertungen und Abbildungen sichtbar, sobald sie vorliegen.\n\n"];
    }

    private async Task<ChatMessage> StreamScienceIntroductionAsync(ChatSession session, ChatMessage assistant,
        Func<MissumAiAssistantUpdate, Task> update, CancellationToken cancellationToken)
    {
        var text = "";
        var parts = ScienceIntroductionParts(session.Title);
        for (var index = 0; index < parts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text += parts[index];
            var now = DateTimeOffset.UtcNow;
            var step = new AssistantToolStep("science-introduction:" + assistant.Id.ToString("N"),
                "assistant.narration", index == parts.Length - 1 ? "completed" : "running", text,
                ContentOffset: 0, StartedAt: assistant.CreatedAt, CompletedAt: index == parts.Length - 1 ? now : null, UpdatedAt: now);
            var steps = (assistant.ToolSteps ?? []).Where(existing => existing.Id != step.Id).Prepend(step).ToArray();
            await chats.UpdateMessageWithToolStepsAsync(assistant.Id, assistant.Content, MessageStatus.Streaming, steps, cancellationToken).ConfigureAwait(false);
            assistant = assistant with { ToolSteps = steps, UpdatedAt = now };
            await update(new(MissumAiAssistantUpdateKind.Delta, assistant)).ConfigureAwait(false);
        }
        return assistant;
    }
}
