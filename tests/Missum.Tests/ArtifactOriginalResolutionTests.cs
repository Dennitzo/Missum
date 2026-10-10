using System.Security.Cryptography;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ArtifactOriginalResolutionTests
{
    [Fact]
    public async Task LegacyThumbnailRecoversVerifiedOriginalOnceAndKeepsThumbnailBytes()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, thumbnail) = await ThumbnailAsync(environment);
        var repository = environment.Get<IChatArtifactRepository>();
        var content = "Original image bytes with transparency and full resolution"u8.ToArray();
        var descriptor = Descriptor(content);
        var calls = 0;
        using var resolver = new AssistantArtifactOriginalResolver(repository, environment.Get<IChatRepository>(), (upload, _) =>
        {
            Assert.Equal("upload-image", upload); calls++;
            return Task.FromResult((descriptor, content));
        });
        var original = await resolver.ResolveAsync(thumbnail, CancellationToken.None);
        Assert.Equal("Original.png", original.FileName);
        Assert.Equal(descriptor.Sha256, original.Sha256);
        Assert.Equal(message.Id, original.MessageId);
        Assert.NotEqual(thumbnail.BlobId, original.BlobId);
        Assert.Equal(thumbnail.Sha256, (await repository.GetAsync(thumbnail.Id))!.Sha256);
        var again = await resolver.ResolveAsync(thumbnail, CancellationToken.None);
        Assert.Equal(original.Id, again.Id); Assert.Equal(1, calls);
        await environment.Get<IChatRepository>().SaveToolStepAsync(message.Id,
            new("repeated-analysis", "media.analyze", "completed", InputJson: "{\"uploadId\":\"upload-image\"}"));
        await using var repeatedBytes = new MemoryStream("Repeated thumbnail"u8.ToArray());
        var repeated = await repository.ImportAsync(message.Id, "repeated-thumbnail", "thumbnail.jpg", "image/jpeg",
            Hash("Repeated thumbnail"u8.ToArray()), repeatedBytes.Length, "media", "repeated-analysis",
            new Dictionary<string, string> { ["role"] = "thumbnail" }, repeatedBytes);
        Assert.Equal(original.Id, (await resolver.ResolveAsync(repeated, CancellationToken.None)).Id);
        Assert.Equal(1, calls);
        Assert.Equal(original.Id, Assert.Single(AssistantArtifactOriginalResolver.DisplayArtifacts(
            await repository.ListForMessageAsync(message.Id), (await environment.Get<IChatRepository>().GetMessageAsync(message.Id))!.ToolSteps)).Id);
        using var previews = new AssistantArtifactPreviewService(repository, environment.Get<IBinaryObjectStore>(), Path.Combine(environment.Directory, "previews"));
        var path = await previews.MaterializeOriginalAsync(original.Id, CancellationToken.None);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        var corrupted = content.ToArray(); corrupted[0] ^= 1;
        await File.WriteAllBytesAsync(path, corrupted);
        await previews.MaterializeOriginalAsync(original.Id, CancellationToken.None);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RecordedSourceUploadOverridesWrongRequestedUploadAndDeduplicatesRecoveredDisplay()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var repository = environment.Get<IChatArtifactRepository>();
        var session = await chats.CreateSessionAsync("Tatsächlich verwendetes Originalbild");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bild", MessageStatus.Completed);
        var thumbnails = new List<ChatArtifact>();
        var thumbnailBytes = "Small lossy thumbnail"u8.ToArray();
        for (var index = 0; index < 2; index++)
        {
            var stepId = "inspection-" + index;
            // media.inspect can select a current-upload fallback while its
            // old receipt lacks resolvedUploadId. Imported metadata records
            // the real upload, even though the model's input was incorrect.
            await chats.SaveToolStepAsync(message.Id, new(stepId, "media.inspect", "completed",
                InputJson: "{\"uploadId\":\"wrong-requested-upload\"}", OutputJson: "{\"kind\":\"image\"}"));
            await using var input = new MemoryStream(thumbnailBytes, writable: false);
            thumbnails.Add(await repository.ImportAsync(message.Id, "thumbnail-" + index, "thumbnail.jpg", "image/jpeg",
                Hash(thumbnailBytes), thumbnailBytes.Length, "media", stepId,
                new Dictionary<string, string> { ["role"] = "thumbnail", ["sourceUploadId"] = "upload-image" }, input));
        }
        var originalBytes = "Verified bytes of the actually inspected original"u8.ToArray();
        var descriptor = Descriptor(originalBytes);
        var calls = 0;
        using var resolver = new AssistantArtifactOriginalResolver(repository, chats, (upload, _) =>
        {
            Assert.Equal("upload-image", upload);
            calls++;
            return Task.FromResult((descriptor, originalBytes));
        });

        var original = await resolver.ResolveAsync(thumbnails[0], CancellationToken.None);
        Assert.Equal(message.Id, original.MessageId);
        Assert.Equal(Hash(originalBytes), original.Sha256);
        var stored = await repository.ListForMessageAsync(message.Id);
        Assert.Equal(3, stored.Count);
        var steps = (await chats.GetMessageAsync(message.Id))!.ToolSteps;
        // The second thumbnail is not linked by replacesArtifactId. Only its
        // actual source-upload provenance can suppress the duplicate card.
        Assert.Equal(original.Id, Assert.Single(AssistantArtifactOriginalResolver.DisplayArtifacts(stored, steps)).Id);
        Assert.Equal(original.Id, (await resolver.ResolveAsync(thumbnails[1], CancellationToken.None)).Id);
        Assert.Equal(1, calls);
        foreach (var thumbnail in thumbnails)
            Assert.Equal(Hash(thumbnailBytes), (await repository.GetAsync(thumbnail.Id))!.Sha256);
    }

    [Fact]
    public async Task CorruptOriginalNeverReplacesOrHidesTheStoredThumbnail()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, thumbnail) = await ThumbnailAsync(environment);
        var content = "Original"u8.ToArray();
        var descriptor = Descriptor(content) with { Sha256 = new string('0', 64) };
        var repository = environment.Get<IChatArtifactRepository>();
        using var resolver = new AssistantArtifactOriginalResolver(repository, environment.Get<IChatRepository>(),
            (_, _) => Task.FromResult((descriptor, content)));
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(thumbnail, CancellationToken.None));
        Assert.Equal(thumbnail.Id, Assert.Single(await repository.ListForMessageAsync(message.Id)).Id);
    }

    [Fact]
    public async Task ARecordedOriginalInAnotherMessageDoesNotAuthorizeCrossMessageResolution()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, thumbnail) = await ThumbnailAsync(environment);
        var chats = environment.Get<IChatRepository>();
        var foreign = await chats.AddMessageAsync(message.SessionId, ChatRole.Assistant, "Andere Nachricht", MessageStatus.Completed);
        var repository = environment.Get<IChatArtifactRepository>();
        var bytes = "Foreign image"u8.ToArray();
        await using var input = new MemoryStream(bytes);
        await repository.ImportAsync(foreign.Id, "foreign-image", "foreign.png", "image/png", Hash(bytes), bytes.Length,
            "media", thumbnail.StepId, new Dictionary<string, string> { ["role"] = "original", ["replacesArtifactId"] = thumbnail.Id.ToString("D") }, input);
        using var resolver = new AssistantArtifactOriginalResolver(repository, chats, (_, _) =>
            throw new InvalidOperationException("Originalupload nicht mehr verfügbar"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(thumbnail, CancellationToken.None));
        Assert.Single(await repository.ListForMessageAsync(message.Id));
    }

    [Fact]
    public async Task NormalImageDoesNotNeedAnAiGatewayToPreviewOrOpen()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (_, thumbnail) = await ThumbnailAsync(environment);
        var image = thumbnail with { Metadata = new Dictionary<string, string> { ["role"] = "original" } };
        using var resolver = new AssistantArtifactOriginalResolver(environment.Get<IChatArtifactRepository>(),
            environment.Get<IChatRepository>(), (_, _) => throw new InvalidOperationException("No network expected"));
        Assert.Same(image, await resolver.ResolveAsync(image, CancellationToken.None));
    }

    private static async Task<(ChatMessage Message, ChatArtifact Thumbnail)> ThumbnailAsync(TestEnvironment environment)
    {
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Originalbild");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bild", MessageStatus.Completed);
        await chats.SaveToolStepAsync(message.Id, new("image-analysis", "media.analyze", "completed", InputJson: "{\"uploadId\":\"upload-image\"}"));
        var bytes = "Small lossy thumbnail"u8.ToArray();
        await using var input = new MemoryStream(bytes);
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(message.Id, "thumbnail", "thumbnail.jpg",
            "image/jpeg", Hash(bytes), bytes.Length, "media", "image-analysis", new Dictionary<string, string> { ["role"] = "thumbnail" }, input);
        return (message, artifact);
    }

    private static ArtifactDescriptor Descriptor(byte[] content) => new("original-image", "Original.png", "image/png", content.Length,
        Hash(content), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), new Dictionary<string, string>
        { ["role"] = "original", ["sourceUploadId"] = "upload-image" });
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
