using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ArtifactWorkspaceRecoveryTests
{
    private static readonly byte[] Original = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");

    [Fact]
    public async Task DeletedUploadIsRecoveredOfflineFromAnEarlierByteVerifiedWorkspaceReceipt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (thumbnail, source) = await CreateAsync(environment);
        var repository = environment.Get<IChatArtifactRepository>();
        using var resolver = new AssistantArtifactOriginalResolver(repository, environment.Get<IChatRepository>(),
            (_, _) => throw new InvalidOperationException("This original is available offline; do not start a gateway."));

        var recovered = await resolver.ResolveAsync(thumbnail, CancellationToken.None);

        Assert.Equal(Hash(Original), recovered.Sha256);
        Assert.Equal(thumbnail.MessageId, recovered.MessageId);
        Assert.Equal(thumbnail.StepId, recovered.StepId);
        Assert.Equal("plot.png", recovered.FileName);
        Assert.Equal("verifiedWorkspaceExecution", recovered.Metadata!["recoveredFrom"]);
        Assert.Equal("execution", recovered.Metadata["verifiedByStepId"]);
        Assert.Equal(thumbnail.Id.ToString("D"), recovered.Metadata["replacesArtifactId"]);
        await File.WriteAllBytesAsync(source, "A later plot must not alter the original"u8.ToArray());
        Assert.Equal(recovered.Id, (await resolver.ResolveAsync(thumbnail, CancellationToken.None)).Id);
        using var previews = new AssistantArtifactPreviewService(repository, environment.Get<IBinaryObjectStore>(),
            Path.Combine(environment.Directory, "original-preview"));
        Assert.Equal(Original, await File.ReadAllBytesAsync(await previews.MaterializeOriginalAsync(recovered.Id, CancellationToken.None)));
        Assert.Equal(thumbnail.Sha256, (await repository.GetAsync(thumbnail.Id))!.Sha256);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("escaped")]
    [InlineData("later-receipt")]
    [InlineData("failed-execution")]
    public async Task UnverifiedWorkspaceFilesNeverReplaceTheSavedThumbnail(string scenario)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (thumbnail, source) = await CreateAsync(environment, scenario);
        if (scenario == "changed") await File.WriteAllBytesAsync(source, "changed plot"u8.ToArray());
        var repository = environment.Get<IChatArtifactRepository>();
        using var resolver = new AssistantArtifactOriginalResolver(repository, environment.Get<IChatRepository>(),
            (_, _) => throw new InvalidOperationException("Originalupload unavailable"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(thumbnail, CancellationToken.None));

        Assert.Equal("Originalupload unavailable", error.Message);
        Assert.Equal(thumbnail.Id, Assert.Single(await repository.ListForMessageAsync(thumbnail.MessageId)).Id);
    }

    private static async Task<(ChatArtifact Artifact, string Source)> CreateAsync(TestEnvironment environment, string? scenario = null)
    {
        var root = Path.Combine(environment.Directory, "workspace");
        var relative = "Science/research-test/work/plot.png";
        var source = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, Original);
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Verifiziertes Bild");
        await chats.SetCodingWorkspacePathAsync(session.Id, root);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bild", MessageStatus.Completed);
        var receipt = new AssistantToolStep("execution", "research.code.execute", "completed", OutputJson: JsonSerializer.Serialize(new
        { success = true, projectId = "research-test", runs = new[] { new { exitCode = scenario == "failed-execution" ? 1 : 0,
            timedOut = false, outputHashes = new Dictionary<string, string> { ["work/plot.png"] = Hash(Original) } } } }));
        if (scenario != "later-receipt") await chats.SaveToolStepAsync(message.Id, receipt);
        await chats.SaveToolStepAsync(message.Id, new("image-source", "image.input", "completed",
            InputJson: JsonSerializer.Serialize(new { operation = "file", path = scenario == "escaped" ? "../plot.png" : relative }),
            OutputJson: """{"uploadId":"upload-expired","mediaType":"image/png","source":"file"}"""));
        if (scenario == "later-receipt") await chats.SaveToolStepAsync(message.Id, receipt);
        await chats.SaveToolStepAsync(message.Id, new("image-analysis", "media.analyze", "completed",
            InputJson: """{"uploadId":"upload-expired"}""", ContentOffset: 24));
        await using var input = new MemoryStream("Old thumbnail bytes"u8.ToArray());
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(message.Id, "thumbnail", "thumbnail.jpg", "image/jpeg",
            Hash("Old thumbnail bytes"u8.ToArray()), input.Length, "media", "image-analysis",
            new Dictionary<string, string> { ["role"] = "thumbnail" }, input);
        return (artifact, source);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
