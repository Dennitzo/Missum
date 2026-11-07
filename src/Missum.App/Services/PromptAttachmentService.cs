using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Transfers a frozen composer selection to its durable prompt owner.</summary>
internal sealed class PromptAttachmentService(
    IAssistantAttachmentRepository attachments, IChatArtifactRepository artifacts, IBinaryObjectStore blobs)
{
    internal async Task<IReadOnlyList<StoredDocument>> PendingDocumentsAsync(
        Guid sessionId, IReadOnlyList<StoredDocument> documents, CancellationToken token)
    {
        var submitted = await artifacts.ListSubmittedDocumentIdsAsync(sessionId, token).ConfigureAwait(false);
        var selections = await artifacts.ListDocumentSelectionsAsync(sessionId, token).ConfigureAwait(false);
        return documents.Where(document => !submitted.Contains(document.Id))
            .Select(document => document with { ComposerSelectionId = selections.GetValueOrDefault(document.Id) }).ToArray();
    }

    internal async Task<IReadOnlyList<AssistantAttachment>> BindAsync(ChatMessage owner,
        IReadOnlyList<AssistantAttachment> files, IReadOnlyList<StoredDocument> documents,
        string? stepId, CancellationToken token)
    {
        if (files.Any(file => file.SessionId != owner.SessionId) || documents.Any(document => document.SessionId != owner.SessionId))
            throw new InvalidOperationException("Die Anhänge gehören nicht zur Sitzung dieser Nachricht.");
        var bound = new List<AssistantAttachment>();
        foreach (var file in files)
        {
            await using var source = await blobs.OpenReadAsync(file.BlobId, token).ConfigureAwait(false);
            var artifact = await artifacts.ImportAsync(owner.Id, $"prompt-{stepId}-{file.Id:N}", file.FileName,
                file.ContentType, file.Sha256, file.Length, "prompt-attachment", stepId,
                new Dictionary<string, string> { ["role"] = "original", ["sourceAttachmentId"] = file.Id.ToString("D") },
                source, token).ConfigureAwait(false);
            // Only consume the exact frozen selection, never files imported while a submission is in progress.
            await attachments.RemoveAsync(file.Id, token).ConfigureAwait(false);
            bound.Add(file with { BlobId = artifact.BlobId });
        }
        foreach (var document in documents)
        {
            await using var source = await blobs.OpenReadAsync(document.BlobId, token).ConfigureAwait(false);
            await artifacts.ImportAsync(owner.Id, $"prompt-{stepId}-{document.Id:N}", document.FileName,
                document.ContentType, document.Sha256, document.Length, "prompt-attachment", stepId,
                new Dictionary<string, string> { ["role"] = "original", ["sourceDocumentId"] = document.Id.ToString("D"),
                    ["sourceDocumentSelectionId"] = document.ComposerSelectionId ?? document.Id.ToString("D") },
                source, token).ConfigureAwait(false);
            // Keep parsed pages and document IDs for document.read and follow-up context.
        }
        return bound;
    }
}
