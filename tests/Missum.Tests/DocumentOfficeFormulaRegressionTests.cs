using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Repositories;
using Missum.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Missum.Tests;

public sealed class DocumentOfficeFormulaRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SharedFormulaImportPreservesMasterCoordinatesCachedResultAndMissingResultWarning()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "shared-formulas.xlsx");
        using (var package = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = package.AddWorkbookPart();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(new S.Cell
                {
                    CellReference = "B2", CellValue = new S.CellValue("4"),
                    CellFormula = new S.CellFormula("A2*2") { FormulaType = S.CellFormulaValues.Shared, SharedIndex = 7U, Reference = "B2:B4" },
                }) { RowIndex = 2U },
                new S.Row(new S.Cell
                {
                    CellReference = "B3", CellValue = new S.CellValue("6"),
                    CellFormula = new S.CellFormula { FormulaType = S.CellFormulaValues.Shared, SharedIndex = 7U },
                }) { RowIndex = 3U },
                new S.Row(new S.Cell
                {
                    CellReference = "B4",
                    CellFormula = new S.CellFormula { FormulaType = S.CellFormulaValues.Shared, SharedIndex = 7U },
                }) { RowIndex = 4U }));
            workbook.Workbook = new S.Workbook(new S.Sheets(new S.Sheet
            {
                Name = "Daten", SheetId = 1U, Id = workbook.GetIdOfPart(worksheet),
            }));
        }

        var text = Assert.Single(await environment.Get<IDocumentFileCodec>().ReadAsync(path));
        Assert.Contains("Basis B2: =A2*2", text, StringComparison.Ordinal);
        Assert.Contains("Ziel B3; Basis B2: =A2*2; gespeicherter Ergebniswert: 6", text, StringComparison.Ordinal);
        Assert.Contains("Ziel B4; Basis B2: =A2*2; kein gespeicherter Ergebniswert", text, StringComparison.Ordinal);
        Assert.Contains("relative Formel nicht aufgelöst, Ergebnis nicht neu berechnet", text, StringComparison.Ordinal);
        Assert.DoesNotContain(text.Split('\n'), static line => line.TrimEnd().EndsWith(" | =", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrphanSharedFormulaRetainsCachedResultAndExplicitlyReportsMissingMaster()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "orphan-formula.xlsx");
        using (var package = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = package.AddWorkbookPart();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
            {
                CellReference = "C1", CellValue = new S.CellValue("42"),
                CellFormula = new S.CellFormula { FormulaType = S.CellFormulaValues.Shared, SharedIndex = 99U },
            }) { RowIndex = 1U }));
            workbook.Workbook = new S.Workbook(new S.Sheets(new S.Sheet
            {
                Name = "Daten", SheetId = 1U, Id = workbook.GetIdOfPart(worksheet),
            }));
        }

        var text = Assert.Single(await environment.Get<IDocumentFileCodec>().ReadAsync(path));
        Assert.Contains("Zelle C1; gespeicherter Ergebniswert: 42; Formelbasis fehlt", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=Kosten!B2")]
    [InlineData("='Kosten'!B2")]
    [InlineData("=SUM(Kosten:Fazit!B2)")]
    [InlineData("=INDIRECT(\"Kosten\"&\"!B2\")")]
    public async Task RenamingReferencedWorksheetFailsBeforePersistingARevisionOrArtifact(string formula)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Blattverweise");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var service = Service(environment, exporter);
        var first = Json(await service.CreateAsync(Arguments("create", "Kosten.xlsx", "kosten", "Kosten", "Posten\tBetrag\nEnergie\t12.5"), session.Id, message.Id, CancellationToken.None));
        var id = first.GetProperty("documentId").GetGuid();
        var appended = Json(await service.CreateAsync(Arguments("appendSection", id.ToString("D"), "fazit", "Fazit", "Wert\n" + formula,
            first.GetProperty("sha256").GetString()), session.Id, message.Id, CancellationToken.None));
        var before = await environment.Get<IGeneratedDocumentRepository>().GetAsync(id);
        var artifactIds = (await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id)).Select(static artifact => artifact.Id).ToArray();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(
            Arguments("replaceSection", id.ToString("D"), "kosten", "Kosten 2027", "Posten\tBetrag\nEnergie\t15", appended.GetProperty("sha256").GetString()),
            session.Id, message.Id, CancellationToken.None));
        Assert.Contains("Blattname", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await environment.Get<IGeneratedDocumentRepository>().GetAsync(id));
        Assert.Equal(artifactIds, (await environment.Get<IChatArtifactRepository>().ListForMessageAsync(message.Id)).Select(static artifact => artifact.Id).ToArray());
    }

    [Theory]
    [InlineData("Kosten 2027", "=B2*2", "Kosten 2027")]
    [InlineData("Kosten?", "=Kosten!B2", "Kosten")]
    public async Task RenamingWithoutCrossSheetReferencesOrWithoutChangingPhysicalNameStillWorks(string heading, string formula, string expectedName)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Erlaubte Blattänderung");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Completed);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var service = Service(environment, exporter);
        var first = Json(await service.CreateAsync(Arguments("create", "Kosten.xlsx", "kosten", "Kosten", "Posten\tBetrag\tFormel\nEnergie\t12.5\t" + formula), session.Id, message.Id, CancellationToken.None));
        var result = Json(await service.CreateAsync(Arguments("replaceSection", first.GetProperty("documentId").GetGuid().ToString("D"), "kosten", heading,
            "Posten\tBetrag\tFormel\nEnergie\t15\t" + formula, first.GetProperty("sha256").GetString()), session.Id, message.Id, CancellationToken.None));
        Assert.Equal(2L, result.GetProperty("revision").GetInt64());
        var artifact = (await environment.Get<IChatArtifactRepository>().GetAsync(result.GetProperty("artifactId").GetGuid()))!;
        await using var input = await environment.Get<IBinaryObjectStore>().OpenReadAsync(artifact.BlobId);
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        buffer.Position = 0;
        using var package = SpreadsheetDocument.Open(buffer, false);
        var workbookPart = Assert.IsType<WorkbookPart>(package.WorkbookPart);
        var workbook = Assert.IsType<S.Workbook>(workbookPart.Workbook);
        Assert.Equal(expectedName, Assert.Single(workbook.GetFirstChild<S.Sheets>()!.Elements<S.Sheet>()).Name?.Value);
        var worksheet = Assert.IsType<S.Worksheet>(workbookPart.WorksheetParts.Single().Worksheet);
        Assert.Equal(formula[1..], Assert.Single(worksheet.Descendants<S.CellFormula>()).Text);
    }

    [Fact]
    public async Task OfficeMigrationUpgradesExistingFormatConstraintPreservingDocumentsAndConstraints()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Bestehende Dokumente");
        var source = "Ein vorhandenes Dokument bleibt unverändert.";
        var now = DateTimeOffset.UtcNow;
        var original = new GeneratedDocument(Guid.NewGuid(), session.Id, "vorhanden.docx", "docx", source,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(), 7, now, now);
        await environment.Get<IGeneratedDocumentRepository>().CreateAsync(original);

        // Recreate the actual pre-Office CHECK constraint, rather than merely deleting
        // a version marker from a schema that already accepts XLSX/PPTX.
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IMissumDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                BEGIN IMMEDIATE;
                CREATE TABLE generated_documents_legacy(
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
                    file_name TEXT NOT NULL,
                    format TEXT NOT NULL CHECK(format IN ('markdown','text','docx','pdf')),
                    source_markdown TEXT NOT NULL,
                    sha256 TEXT NOT NULL CHECK(length(sha256)=64),
                    revision INTEGER NOT NULL CHECK(revision>=1),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(session_id,file_name COLLATE NOCASE)
                ) STRICT;
                INSERT INTO generated_documents_legacy SELECT * FROM generated_documents;
                DROP TABLE generated_documents;
                ALTER TABLE generated_documents_legacy RENAME TO generated_documents;
                CREATE INDEX idx_generated_documents_session_updated ON generated_documents(session_id,updated_at,id);
                DELETE FROM schema_migrations WHERE version=51;
                COMMIT;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new SqliteDatabase(new MissumInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await migrated.InitializeAsync();
        Assert.True(await migrated.CheckIntegrityAsync());
        var repository = new SqliteGeneratedDocumentRepository(migrated);
        Assert.Equal(original, await repository.GetAsync(original.Id));
        foreach (var format in new[] { "xlsx", "pptx" })
        {
            var document = original with { Id = Guid.NewGuid(), FileName = "neu." + format, Format = format, Revision = 1 };
            Assert.Equal(document, await repository.CreateAsync(document));
            Assert.Equal(document, await repository.GetAsync(document.Id));
        }
        await Assert.ThrowsAsync<SqliteException>(() => repository.CreateAsync(original with { Id = Guid.NewGuid(), FileName = "VORHANDEN.DOCX" }));
        await Assert.ThrowsAsync<SqliteException>(() => repository.CreateAsync(original with { Id = Guid.NewGuid(), SessionId = Guid.NewGuid() }));
        await using var reopened = new SqliteDatabase(new MissumInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        Assert.Equal(3, (await new SqliteGeneratedDocumentRepository(reopened).ListAsync(session.Id)).Count);
        Assert.True(await reopened.CheckIntegrityAsync());
    }

    private static JsonElement Arguments(string operation, string reference, string sectionId, string heading, string content, string? sha256 = null) =>
        JsonSerializer.SerializeToElement(new { operation, reference, format = "xlsx", sectionId, heading, content, expectedSha256 = sha256 });

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static LocalDocumentToolService Service(TestEnvironment environment, DocumentPdfExporter exporter) => new(
        environment.Get<IGeneratedDocumentRepository>(), environment.Get<IDocumentIngestor>(),
        environment.Get<IChatArtifactRepository>(), environment.Get<IBinaryObjectStore>(),
        environment.Get<IChatRepository>(), environment.Get<IDocumentFileCodec>(), exporter);
}
