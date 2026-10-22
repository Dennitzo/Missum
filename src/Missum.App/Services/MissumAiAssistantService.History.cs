using Missum.Ai.Contracts;
using Missum.Ai.Client;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task<(SessionRunContext Context, ChatMessage Message)> PrepareSessionHistoryAsync(
        MissumAiClient client, Guid sessionId, IReadOnlyList<ChatMessage> history, string prompt, string model,
        SessionContextProfile profile, int? contextLength, int? historyBudget, ChatMessage assistant,
        Func<MissumAiAssistantUpdate, Task> update, CancellationToken token)
    {
        AssistantToolStep? receipt = null;
        async Task SaveReceiptAsync(string status, string detail, CancellationToken saveToken)
        {
            var now = DateTimeOffset.UtcNow;
            var next = new AssistantToolStep("history-compaction:" + assistant.Id.ToString("N"), "context.compact", status,
                detail, ContentOffset: assistant.Content.Length, StartedAt: receipt?.StartedAt ?? now,
                CompletedAt: status == "running" ? null : now, UpdatedAt: now);
            receipt = await chats.SaveToolStepDeltaAsync(assistant.Id, next, saveToken).ConfigureAwait(false);
            assistant = assistant with { ToolSteps = (assistant.ToolSteps ?? []).Where(item => item.Id != receipt.Id).Append(receipt).ToArray() };
            await update(new(MissumAiAssistantUpdateKind.Status, assistant, ToolStep: receipt,
                Status: status == "completed" ? "Kontext komprimiert" : status == "running" ? "Sitzungskontext wird verdichtet"
                    : status == "failed" ? "Kontextkomprimierung fehlgeschlagen" : "Kontextkomprimierung abgebrochen",
                Model: model)).ConfigureAwait(false);
        }
        try
        {
            var prepared = await sessionContexts.PrepareAsync(client, sessionId, history, prompt, model, profile,
                contextLength, historyBudget, async progress =>
                {
                    if (progress.Percent is > 0 and < 100)
                        await SaveReceiptAsync("running", progress.Detail, token).ConfigureAwait(false);
                    await update(new(MissumAiAssistantUpdateKind.Status, assistant, Status: progress.Status,
                        Detail: progress.Detail, Model: model, ContextLimit: contextLength)).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            if (receipt is not null || prepared.Descriptor.PreparedByAi && !prepared.CacheHit)
                await SaveReceiptAsync("completed", "Älterer Sitzungsverlauf wurde als fortsetzbarer Arbeitsstand verdichtet.", token).ConfigureAwait(false);
            return (prepared, assistant);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (receipt is not null)
                await SaveReceiptAsync(exception is OperationCanceledException ? "cancelled" : "failed",
                    "Die Sitzungskomprimierung wurde nicht übernommen; der bisherige Verlauf bleibt erhalten.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> LoadModelHistoryAsync(Guid sessionId, CancellationToken token)
    {
        var pages = new List<IReadOnlyList<ChatMessage>>();
        ConversationCursor? before = null;
        while (true)
        {
            var page = await chats.ListContextMessagesAsync(sessionId, 512, before, token).ConfigureAwait(false);
            if (page.Count == 0) break;
            pages.Add(page);
            if (page.Count < 512) break;
            var first = page[0];
            before = new(first.CreatedAt, first.Id);
        }
        pages.Reverse();
        return pages.SelectMany(page => page).ToArray();
    }
}
