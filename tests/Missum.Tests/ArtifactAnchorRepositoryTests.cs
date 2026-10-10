using System.Security.Cryptography;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ArtifactAnchorRepositoryTests
{
    private static readonly byte[] ImageBytes = "The same verified image bytes"u8.ToArray();
    private static readonly byte[] OtherBytes = "A different image with a forged descriptor"u8.ToArray();
    private const string ServerId = "server-original-image";
    private const string Prefix = "Hier entsteht das Bild.";

    [Fact]
    public async Task VerifiedReplayAddsOnlyTheMissingAnchorAndRetainsItsTimelineOffset()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, original) = await CreateLegacyAsync(environment);
        var repository = environment.Get<IChatArtifactRepository>();
        var chats = environment.Get<IChatRepository>();
        await chats.SaveToolStepAsync(message.Id, new("image-step", "media.analyze", "completed", ContentOffset: Prefix.Length));
        var revision = (await chats.GetMessageAsync(message.Id))!.Revision;
        using var replay = new MemoryStream(ImageBytes, writable: false);

        var repaired = await repository.ImportAsync(message.Id, ServerId, "image.png", "image/png", Hash(ImageBytes),
            ImageBytes.Length, "media", "image-step", null, replay);

        Assert.Equal(original.Id, repaired.Id);
        Assert.Equal(original.BlobId, repaired.BlobId);
        Assert.Equal(original.Sha256, repaired.Sha256);
        Assert.Equal("image-step", repaired.StepId);
        var snapshot = await environment.Get<IConversationSnapshotRepository>().GetAsync(message.SessionId);
        Assert.NotNull(snapshot);
        var stored = Assert.Single(snapshot.Artifacts[message.Id]);
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal("image-step", stored.StepId);
        var restored = Assert.Single(snapshot.Messages, item => item.Id == message.Id);
        Assert.True(restored.Revision > revision);
        Assert.Equal(Prefix.Length, Assert.Single(restored.ToolSteps!).ContentOffset);
    }

    [Fact]
    public async Task ExistingAnchorIsNeverReassignedByALaterReplay()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, original) = await CreateLegacyAsync(environment, "original-step");
        using var replay = new MemoryStream(ImageBytes, writable: false);

        var retained = await environment.Get<IChatArtifactRepository>().ImportAsync(message.Id, ServerId,
            "image.png", "image/png", Hash(ImageBytes), ImageBytes.Length, "media", "later-step", null, replay);

        Assert.Equal(original.Id, retained.Id);
        Assert.Equal("original-step", retained.StepId);
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("length")]
    [InlineData("type")]
    public async Task DescriptorMismatchCannotAssignAnAnchorToAnExistingImage(string difference)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, original) = await CreateLegacyAsync(environment);
        using var replay = new MemoryStream(ImageBytes, writable: false);

        var retained = await environment.Get<IChatArtifactRepository>().ImportAsync(message.Id, ServerId, "image.png",
            difference == "type" ? "image/jpeg" : "image/png", difference == "sha" ? Hash(OtherBytes) : Hash(ImageBytes),
            difference == "length" ? ImageBytes.Length + 1 : ImageBytes.Length, "media", "wrong-step", null, replay);

        Assert.Equal(original.Id, retained.Id);
        Assert.Equal(original.Sha256, retained.Sha256);
        Assert.Null(retained.StepId);
        Assert.Null((await environment.Get<IChatArtifactRepository>().GetAsync(original.Id))!.StepId);
    }

    [Fact]
    public async Task ForgedMatchingDescriptorWithDifferentActualBytesCannotRepairAnAnchor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, original) = await CreateLegacyAsync(environment);
        var repository = environment.Get<IChatArtifactRepository>();
        using var replay = new MemoryStream(OtherBytes, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ImportAsync(message.Id, ServerId,
            "image.png", "image/png", Hash(ImageBytes), ImageBytes.Length, "media", "wrong-step", null, replay));

        var retained = Assert.Single(await repository.ListForMessageAsync(message.Id));
        Assert.Equal(original.Id, retained.Id);
        Assert.Equal(original.BlobId, retained.BlobId);
        Assert.Equal(original.Sha256, retained.Sha256);
        Assert.Null(retained.StepId);
    }

    [Fact]
    public async Task IdenticalServerArtifactForAnotherMessageDoesNotAlterTheOriginalOwner()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, original) = await CreateLegacyAsync(environment);
        var foreign = await environment.Get<IChatRepository>().AddMessageAsync(message.SessionId,
            ChatRole.Assistant, "Andere Bildaktion", MessageStatus.Completed);
        var repository = environment.Get<IChatArtifactRepository>();
        using var content = new MemoryStream(ImageBytes, writable: false);

        var other = await repository.ImportAsync(foreign.Id, ServerId, "image.png", "image/png", Hash(ImageBytes),
            ImageBytes.Length, "media", "foreign-step", null, content);

        Assert.NotEqual(original.Id, other.Id);
        Assert.Equal(foreign.Id, other.MessageId);
        Assert.Equal("foreign-step", other.StepId);
        Assert.Null((await repository.GetAsync(original.Id))!.StepId);
    }

    [Fact]
    public async Task ConcurrentReplaysAssignAtMostOneAnchorWithoutReplacingIt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (message, original) = await CreateLegacyAsync(environment);
        var repository = environment.Get<IChatArtifactRepository>();
        using var first = new MemoryStream(ImageBytes, writable: false);
        using var second = new MemoryStream(ImageBytes, writable: false);

        await Task.WhenAll(
            repository.ImportAsync(message.Id, ServerId, "image.png", "image/png", Hash(ImageBytes), ImageBytes.Length,
                "media", "first-step", null, first),
            repository.ImportAsync(message.Id, ServerId, "image.png", "image/png", Hash(ImageBytes), ImageBytes.Length,
                "media", "second-step", null, second));

        var retained = Assert.Single(await repository.ListForMessageAsync(message.Id));
        Assert.Equal(original.Id, retained.Id);
        Assert.True(retained.StepId is "first-step" or "second-step");
        using var later = new MemoryStream(ImageBytes, writable: false);
        var replay = await repository.ImportAsync(message.Id, ServerId, "image.png", "image/png", Hash(ImageBytes),
            ImageBytes.Length, "media", "later-step", null, later);
        Assert.Equal(retained.StepId, replay.StepId);
    }

    private static async Task<(ChatMessage Message, ChatArtifact Artifact)> CreateLegacyAsync(TestEnvironment environment, string? stepId = null)
    {
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Bildanker");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, Prefix, MessageStatus.Completed);
        using var input = new MemoryStream(ImageBytes, writable: false);
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(message.Id, ServerId, "image.png",
            "image/png", Hash(ImageBytes), ImageBytes.Length, "media", stepId, null, input);
        return (message, artifact);
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
