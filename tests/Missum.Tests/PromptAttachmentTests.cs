using System.Text;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class PromptAttachmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmissionRetainsOriginalsAndOnlyConsumesItsFrozenSelection(bool steering)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var attachments = environment.Get<IAssistantAttachmentRepository>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var service = new PromptAttachmentService(attachments, artifacts, blobs);
        var session = await chats.CreateSessionAsync("Anhänge");
        var turn = await chats.AddTurnAsync(session.Id, "Originaldateien prüfen");
        var owner = steering ? turn.AssistantMessage : turn.UserMessage;
        var step = steering ? "steering-test" : null;
        byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP3sAAAAASUVORK5CYII=");
        using var file = new MemoryStream(image);
        var attachment = await attachments.ImportAsync(session.Id, "Original.png", "image/png", file);
        using var text = new MemoryStream(Encoding.UTF8.GetBytes("Messwert: 42; Größe: 7 µm"));
        var document = (await documents.ImportAsync(session.Id, "Messdaten.txt", text)).Document!;
        var selectedDocuments = await service.PendingDocumentsAsync(session.Id, await documents.ListAsync(session.Id), default);
        Assert.Single(selectedDocuments);
        using var newer = new MemoryStream([1, 2, 3]);
        var next = await attachments.ImportAsync(session.Id, "Nächster-Prompt.bin", "application/octet-stream", newer);

        var bound = await service.BindAsync(owner, [attachment], selectedDocuments, step, default);
        Assert.Equal(next.Id, Assert.Single(await attachments.ListAsync(session.Id)).Id);
        Assert.Empty(await service.PendingDocumentsAsync(session.Id, await documents.ListAsync(session.Id), default));
        Assert.Single(await documents.ListAsync(session.Id));
        Assert.Contains("Messwert: 42", string.Join("", await documents.ReadPagesAsync(document.Id)));
        var saved = await artifacts.ListForMessageAsync(owner.Id);
        Assert.Equal(2, saved.Count);
        Assert.All(saved, item => Assert.Equal(step, item.StepId));
        await using (var original = await blobs.OpenReadAsync(Assert.Single(bound).BlobId))
        {
            using var bytes = new MemoryStream();
            await original.CopyToAsync(bytes);
            Assert.Equal(image, bytes.ToArray());
        }
        // Lost acknowledgements repeat the same owner/selection without duplicating cards.
        await service.BindAsync(owner, [attachment], selectedDocuments, step, default);
        Assert.Equal(saved.Select(item => item.Id), (await artifacts.ListForMessageAsync(owner.Id)).Select(item => item.Id));
        var unrelated = await chats.CreateSessionAsync("Andere Sitzung");
        Assert.Empty(await artifacts.ListSubmittedDocumentIdsAsync(unrelated.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync(owner,
            [attachment with { SessionId = unrelated.Id }], [], step, default));
        Assert.Equal(next.Id, Assert.Single(await attachments.ListAsync(session.Id)).Id);

        // Reattaching an already parsed document creates a new composer selection;
        // a late acknowledgement of the earlier prompt cannot consume that selection.
        await artifacts.RegisterDocumentSelectionAsync(session.Id, document.Id);
        var reattached = await service.PendingDocumentsAsync(session.Id, await documents.ListAsync(session.Id), default);
        Assert.Single(reattached);
        await service.BindAsync(owner, [attachment], selectedDocuments, step, default);
        Assert.Single(await service.PendingDocumentsAsync(session.Id, await documents.ListAsync(session.Id), default));
        var nextTurn = await chats.AddTurnAsync(session.Id, "Dasselbe Dokument erneut prüfen");
        await service.BindAsync(nextTurn.UserMessage, [], reattached, null, default);
        Assert.Empty(await service.PendingDocumentsAsync(session.Id, await documents.ListAsync(session.Id), default));
        Assert.Single(await artifacts.ListForMessageAsync(nextTurn.UserMessage.Id));
        Assert.Equal(2, (await artifacts.ListForMessageAsync(owner.Id)).Count);
    }
}
