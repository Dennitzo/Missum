using System.Text;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class DocumentArtifactAtomicityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobImportFailureLeavesDocumentAndConversationUnchangedAndAllowsRetry(bool replace)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Importfehler");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var normal = Service(environment, exporter);
        JsonElement? first = replace ? Json(await normal.CreateAsync(Arguments("create", "Bericht.md", "Original"), session.Id, message.Id, CancellationToken.None)) : null;
        var before = await environment.Get<IGeneratedDocumentRepository>().ListAsync(session.Id);
        var messageBefore = await chats.GetMessageAsync(message.Id);
        var sessionBefore = await chats.GetSessionAsync(session.Id);
        var args = first is { } original
            ? Arguments("replaceSection", original.GetProperty("documentId").GetGuid().ToString("D"), "Neue Fassung", original.GetProperty("sha256").GetString())
            : Arguments("create", "Bericht.md", "Neue Fassung");
        var failing = Service(environment, exporter, new InterceptingBlobStore(environment.Get<IBinaryObjectStore>(), failBeforeImport: true));

        await Assert.ThrowsAsync<IOException>(() => failing.CreateAsync(args, session.Id, message.Id, CancellationToken.None));

        Assert.Equal(before, await environment.Get<IGeneratedDocumentRepository>().ListAsync(session.Id));
        Assert.Equal(messageBefore, await chats.GetMessageAsync(message.Id));
        Assert.Equal(sessionBefore, await chats.GetSessionAsync(session.Id));
        Assert.Equal(replace ? 1 : 0, (await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id)).Count);
        var retry = Json(await normal.CreateAsync(args, session.Id, message.Id, CancellationToken.None));
        Assert.Equal(replace ? 2L : 1L, retry.GetProperty("revision").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtifactSqlFailureRollsBackDocumentAndLeavesOriginalHashUsable(bool replace)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Transaktionsfehler");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var tracking = new InterceptingBlobStore(environment.Get<IBinaryObjectStore>());
        var service = Service(environment, exporter, tracking);
        JsonElement? first = replace ? Json(await service.CreateAsync(Arguments("create", "Bericht.md", "Original"), session.Id, message.Id, CancellationToken.None)) : null;
        var before = await environment.Get<IGeneratedDocumentRepository>().ListAsync(session.Id);
        var messageBefore = await chats.GetMessageAsync(message.Id);
        var sessionBefore = await chats.GetSessionAsync(session.Id);
        var args = first is { } original
            ? Arguments("replaceSection", original.GetProperty("documentId").GetGuid().ToString("D"), "Neue Fassung", original.GetProperty("sha256").GetString())
            : Arguments("create", "Bericht.md", "Neue Fassung");
        await SetArtifactFailureAsync(environment, true);

        var error = await Assert.ThrowsAsync<SqliteException>(() => service.CreateAsync(args, session.Id, message.Id, CancellationToken.None));

        Assert.Contains("injected artifact commit failure", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await environment.Get<IGeneratedDocumentRepository>().ListAsync(session.Id));
        Assert.Equal(messageBefore, await chats.GetMessageAsync(message.Id));
        Assert.Equal(sessionBefore, await chats.GetSessionAsync(session.Id));
        Assert.Equal(replace ? 1 : 0, (await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id)).Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => environment.Get<IBinaryObjectStore>().OpenReadAsync(tracking.LastImportedId));
        await SetArtifactFailureAsync(environment, false);
        var retry = Json(await service.CreateAsync(args, session.Id, message.Id, CancellationToken.None));
        Assert.Equal(replace ? 2L : 1L, retry.GetProperty("revision").GetInt64());
        Assert.True(await environment.Get<IMissumDatabase>().CheckIntegrityAsync());
    }

    [Fact]
    public async Task CancellationAfterBlobCommitDoesNotCreateDocumentOrArtifact()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Abbruch");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var cancellation = new CancellationTokenSource();
        var tracking = new InterceptingBlobStore(environment.Get<IBinaryObjectStore>(), () => { cancellation.Cancel(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(environment, exporter, tracking).CreateAsync(
            Arguments("create", "Abgebrochen.md", "Nicht zugesagt"), session.Id, message.Id, cancellation.Token));

        Assert.Empty(await environment.Get<IGeneratedDocumentRepository>().ListAsync(session.Id));
        Assert.Empty(await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => environment.Get<IBinaryObjectStore>().OpenReadAsync(tracking.LastImportedId));
        Assert.True(await environment.Get<IMissumDatabase>().CheckIntegrityAsync());
    }

    [Fact]
    public async Task FailedCommitNeverDeletesADeduplicatedBlobUsedByAnExistingArtifact()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Gemeinsame Binärdaten");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var tracking = new InterceptingBlobStore(environment.Get<IBinaryObjectStore>());
        var service = Service(environment, exporter, tracking);
        var original = Json(await service.CreateAsync(Arguments("create", "Original.md", "Identischer Inhalt"), session.Id, message.Id, CancellationToken.None));
        var originalArtifact = (await environment.Get<IChatArtifactRepository>().GetAsync(original.GetProperty("artifactId").GetGuid()))!;
        await SetArtifactFailureAsync(environment, true);

        await Assert.ThrowsAsync<SqliteException>(() => service.CreateAsync(Arguments("create", "Kopie.md", "Identischer Inhalt"), session.Id, message.Id, CancellationToken.None));

        Assert.Equal(originalArtifact.BlobId, tracking.LastImportedId);
        Assert.Single(await environment.Get<IGeneratedDocumentRepository>().ListAsync(session.Id));
        Assert.Equal(originalArtifact.Id, Assert.Single(await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id)).Id);
        Assert.True(await environment.Get<IBinaryObjectStore>().VerifyAsync(originalArtifact.BlobId));
        await using var input = await environment.Get<IBinaryObjectStore>().OpenReadAsync(originalArtifact.BlobId);
        using var reader = new StreamReader(input, Encoding.UTF8);
        Assert.Contains("Identischer Inhalt", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        Assert.True(await environment.Get<IMissumDatabase>().CheckIntegrityAsync());
    }

    [Fact]
    public async Task RepeatedIdenticalSectionReusesArtifactWithoutAdvancingConversationRevisions()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Idempotenz");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var service = Service(environment, exporter);
        var first = Json(await service.CreateAsync(Arguments("create", "Bericht.md", "Unverändert"), session.Id, message.Id, CancellationToken.None));
        var sessionBefore = await chats.GetSessionAsync(session.Id);
        var messageBefore = await chats.GetMessageAsync(message.Id);

        var repeated = Json(await service.CreateAsync(Arguments("replaceSection", first.GetProperty("documentId").GetGuid().ToString("D"), "Unverändert",
            first.GetProperty("sha256").GetString()), session.Id, message.Id, CancellationToken.None));

        Assert.Equal(first.GetProperty("artifactId").GetGuid(), repeated.GetProperty("artifactId").GetGuid());
        Assert.Equal(1L, repeated.GetProperty("revision").GetInt64());
        Assert.False(repeated.GetProperty("changed").GetBoolean());
        Assert.Single(await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id));
        Assert.Equal(sessionBefore, await chats.GetSessionAsync(session.Id));
        Assert.Equal(messageBefore, await chats.GetMessageAsync(message.Id));
    }

    [Fact]
    public async Task ConcurrentCommittedRevisionWinsWithoutPublishingLosingArtifact()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Nebenläufige Dokumentänderung");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var normal = Service(environment, exporter);
        var first = Json(await normal.CreateAsync(Arguments("create", "Bericht.md", "Original"), session.Id, message.Id, CancellationToken.None));
        var documentId = first.GetProperty("documentId").GetGuid();
        var oldHash = first.GetProperty("sha256").GetString();
        JsonElement winner = default;
        var tracking = new InterceptingBlobStore(environment.Get<IBinaryObjectStore>(), async () =>
        {
            winner = Json(await normal.CreateAsync(Arguments("replaceSection", documentId.ToString("D"), "Bereits zugesagte Änderung", oldHash),
                session.Id, message.Id, CancellationToken.None));
        });

        await Assert.ThrowsAsync<RevisionConflictException>(() => Service(environment, exporter, tracking).CreateAsync(
            Arguments("replaceSection", documentId.ToString("D"), "Veraltete parallele Änderung", oldHash), session.Id, message.Id, CancellationToken.None));

        var stored = (await environment.Get<IGeneratedDocumentRepository>().GetAsync(documentId))!;
        Assert.Equal(winner.GetProperty("sha256").GetString(), stored.Sha256);
        Assert.Equal(2L, stored.Revision);
        Assert.Equal(2, (await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id)).Count);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => environment.Get<IBinaryObjectStore>().OpenReadAsync(tracking.LastImportedId));
        var winnerArtifact = (await environment.Get<IChatArtifactRepository>().GetAsync(winner.GetProperty("artifactId").GetGuid()))!;
        Assert.True(await environment.Get<IBinaryObjectStore>().VerifyAsync(winnerArtifact.BlobId));
    }

    private static async Task SetArtifactFailureAsync(TestEnvironment environment, bool enabled)
    {
        await using var connection = new SqliteConnection($"Data Source={environment.Get<IMissumDatabase>().DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = enabled ? """
            CREATE TRIGGER fail_document_artifact AFTER INSERT ON chat_artifacts
            WHEN NEW.provider='document-tool'
            BEGIN SELECT RAISE(ABORT, 'injected artifact commit failure'); END;
            """ : "DROP TRIGGER fail_document_artifact;";
        await command.ExecuteNonQueryAsync();
    }

    private static JsonElement Arguments(string operation, string reference, string content, string? sha256 = null) =>
        JsonSerializer.SerializeToElement(new { operation, reference, format = "markdown", sectionId = "start", heading = "Bericht", content, expectedSha256 = sha256 });

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static LocalDocumentToolService Service(TestEnvironment environment, DocumentPdfExporter exporter, IBinaryObjectStore? blobs = null) => new(
        environment.Get<IGeneratedDocumentRepository>(), environment.Get<IDocumentIngestor>(), environment.Get<IChatArtifactRepository>(),
        blobs ?? environment.Get<IBinaryObjectStore>(), environment.Get<IChatRepository>(), environment.Get<IDocumentFileCodec>(), exporter);

    private sealed class InterceptingBlobStore(IBinaryObjectStore inner, Func<Task>? afterImport = null, bool failBeforeImport = false) : IBinaryObjectStore
    {
        public Guid LastImportedId { get; private set; }

        public async Task<BinaryObjectDescriptor> ImportAsync(Stream source, string contentType, CancellationToken cancellationToken = default)
        {
            if (failBeforeImport) throw new IOException("Injected insufficient import space.");
            var result = await inner.ImportAsync(source, contentType, cancellationToken);
            LastImportedId = result.Id;
            if (afterImport is not null) await afterImport();
            return result;
        }

        public Task<Stream> OpenReadAsync(Guid id, CancellationToken cancellationToken = default) => inner.OpenReadAsync(id, cancellationToken);
        public Task ExportAsync(Guid id, Stream destination, CancellationToken cancellationToken = default) => inner.ExportAsync(id, destination, cancellationToken);
        public Task<bool> VerifyAsync(Guid id, CancellationToken cancellationToken = default) => inner.VerifyAsync(id, cancellationToken);
        public Task DeleteIfUnreferencedAsync(Guid id, CancellationToken cancellationToken = default) => inner.DeleteIfUnreferencedAsync(id, cancellationToken);
    }
}
