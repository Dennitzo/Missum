using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Missum.Tests;

public sealed class DocumentOfficeToolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string InitialTable = "| Posten | Betrag | Ergebnis |\n| --- | ---: | ---: |\n| Energie | 12.5 | =B2*2 |";
    private const string UpdatedTable = "| Posten | Betrag | Ergebnis |\n| --- | ---: | ---: |\n| Energie korrigiert | 15 | =B2*2 |";

    [Theory]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    [InlineData("docx")]
    public async Task OfficeToolCreatesRealVersionedArtifactsWithSectionReplacementAndImport(string format)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Office-Werkzeuge");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var service = Service(environment, exporter);
        var first = Json(await service.CreateAsync(Arguments("create", "Bericht." + format, format, "kosten", "Kosten", InitialTable), session.Id, message.Id, CancellationToken.None));
        var documentId = first.GetProperty("documentId").GetGuid().ToString("D");
        var appended = Json(await service.CreateAsync(Arguments("appendSection", documentId, format, "fazit", "Fazit", "Zweites Kapitel bleibt erhalten.", first.GetProperty("sha256").GetString()), session.Id, message.Id, CancellationToken.None));
        var updated = Json(await service.CreateAsync(Arguments("replaceSection", documentId, format, "kosten", "Kosten", UpdatedTable, appended.GetProperty("sha256").GetString()), session.Id, message.Id, CancellationToken.None));

        Assert.Equal(3L, updated.GetProperty("revision").GetInt64());
        Assert.Equal("Bericht." + format, updated.GetProperty("fileName").GetString());
        var artifactId = updated.GetProperty("artifactId").GetGuid();
        var path = await ExportArtifactAsync(environment, artifactId);
        var codec = environment.Get<IDocumentFileCodec>();
        var decoded = string.Join('\n', await codec.ReadAsync(path));
        Assert.Contains("Energie korrigiert", decoded, StringComparison.Ordinal);
        Assert.Contains("Zweites Kapitel bleibt erhalten.", decoded, StringComparison.Ordinal);

        if (format == "xlsx")
        {
            using var workbook = SpreadsheetDocument.Open(path, false);
            AssertValid(workbook);
            var workbookPart = Assert.IsType<WorkbookPart>(workbook.WorkbookPart);
            var workbookRoot = Assert.IsType<S.Workbook>(workbookPart.Workbook);
            Assert.Equal(2, workbookRoot.GetFirstChild<S.Sheets>()!.ChildElements.Count);
            var sheet = workbookPart.WorksheetParts.Single(part => part.Worksheet!.Descendants<S.CellFormula>().Any());
            var worksheet = Assert.IsType<S.Worksheet>(sheet.Worksheet);
            Assert.Equal("B2*2", Assert.Single(worksheet.Descendants<S.CellFormula>()).Text);
            var number = worksheet.Descendants<S.Cell>().Single(cell => cell.CellReference?.Value == "B2");
            Assert.Equal(S.CellValues.Number, number.DataType!.Value);
            Assert.Equal("15", number.CellValue!.Text);
            Assert.True(workbookRoot.GetFirstChild<S.CalculationProperties>()!.FullCalculationOnLoad!.Value);
            Assert.Contains("=B2*2", decoded, StringComparison.Ordinal);
        }
        else if (format == "pptx")
        {
            using var presentation = PresentationDocument.Open(path, false);
            AssertValid(presentation);
            var presentationPart = Assert.IsType<PresentationPart>(presentation.PresentationPart);
            var presentationRoot = Assert.IsType<DocumentFormat.OpenXml.Presentation.Presentation>(presentationPart.Presentation);
            Assert.Equal(2, presentationRoot.SlideIdList!.ChildElements.Count);
            Assert.Single(presentationPart.SlideParts.SelectMany(part => part.Slide!.Descendants<DocumentFormat.OpenXml.Drawing.Table>()));
        }
        else
        {
            using var word = WordprocessingDocument.Open(path, false);
            AssertValid(word);
            Assert.Single(Assert.IsType<W.Document>(word.MainDocumentPart!.Document).Descendants<W.Table>());
        }

        var artifactRead = Json(await service.ReadAsync(JsonSerializer.SerializeToElement(new
        {
            scope = "session", mode = "read", reference = artifactId.ToString("D"), maximumCharacters = 4000,
        }), session.Id, CancellationToken.None));
        Assert.Contains("Energie korrigiert", artifactRead.GetRawText(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(Arguments("replaceSection", documentId, format, "kosten", "Kosten", InitialTable, first.GetProperty("sha256").GetString()), session.Id, message.Id, CancellationToken.None));

        var otherSession = await chats.CreateSessionAsync("Importiert");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(JsonSerializer.SerializeToElement(new
        {
            scope = "session", mode = "read", reference = artifactId.ToString("D"),
        }), otherSession.Id, CancellationToken.None));
        await using var input = File.OpenRead(path);
        var imported = await environment.Get<IDocumentIngestor>().ImportAsync(otherSession.Id, Path.GetFileName(path), input);
        Assert.True(imported.Success, imported.Error);
        var pages = await environment.Get<IDocumentIngestor>().ReadPagesAsync(imported.Document!.Id);
        Assert.Contains(pages, page => page.Text.Contains("Energie korrigiert", StringComparison.Ordinal));
        Assert.Contains(pages, page => page.Text.Contains("Zweites Kapitel bleibt erhalten.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task XlsxReadDecodesSharedStringsSparseCellsBooleansAndFormulasInSheetOrder()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "import.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            var shared = workbook.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text("Gemeinsamer Text")));
            var second = workbook.AddNewPart<WorksheetPart>();
            second.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
            {
                CellReference = "A1", DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text("Zweites Blatt")),
            })));
            var first = workbook.AddNewPart<WorksheetPart>();
            first.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                new S.Cell { CellReference = "C1", DataType = S.CellValues.Boolean, CellValue = new S.CellValue("1") }),
                new S.Row(new S.Cell { CellReference = "A2", CellFormula = new S.CellFormula("SUM(1,2)"), CellValue = new S.CellValue("3") })));
            workbook.Workbook = new S.Workbook(new S.Sheets(
                new S.Sheet { Name = "Erstes", SheetId = 1U, Id = workbook.GetIdOfPart(first) },
                new S.Sheet { Name = "Zweites", SheetId = 2U, Id = workbook.GetIdOfPart(second) }));
        }

        var pages = await environment.Get<IDocumentFileCodec>().ReadAsync(path);
        Assert.Equal(2, pages.Count);
        Assert.StartsWith("# Erstes", pages[0], StringComparison.Ordinal);
        Assert.Contains("Gemeinsamer Text |  | TRUE", pages[0], StringComparison.Ordinal);
        Assert.Contains("=SUM(1,2)", pages[0], StringComparison.Ordinal);
        Assert.Contains("Zweites Blatt", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task XlsxTsvKeepsIdentifiersAndExplicitTextInsteadOfTurningThemIntoFormulas()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "zellen.xlsx");
        await environment.Get<IDocumentFileCodec>().WriteXlsxAsync("# Daten\nID\tText\tAktiv\n00123\t'=A1+1\ttrue", path);
        using var workbook = SpreadsheetDocument.Open(path, false);
        AssertValid(workbook);
        var row = Assert.IsType<S.Worksheet>(workbook.WorkbookPart!.WorksheetParts.Single().Worksheet).Descendants<S.Row>().Last();
        var cells = row.Elements<S.Cell>().ToArray();
        Assert.Equal("00123", cells[0].InlineString!.InnerText);
        Assert.Equal("=A1+1", cells[1].InlineString!.InnerText);
        Assert.Null(cells[1].CellFormula);
        Assert.Equal(S.CellValues.Boolean, cells[2].DataType!.Value);
    }

    [Theory]
    [InlineData("=WEBSERVICE(\"https://example.invalid\")")]
    [InlineData("='[external.xlsx]Sheet1'!A1")]
    [InlineData("=cmd|' /c anything'!A0")]
    public async Task XlsxRejectsExternalDataFormulasWithoutCreatingAnOutput(string formula)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "external.xlsx");
        await Assert.ThrowsAsync<InvalidDataException>(() => environment.Get<IDocumentFileCodec>().WriteXlsxAsync("Wert\tFormel\n1\t" + formula, path));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(environment.Directory, "external.xlsx.*.tmp"));
    }

    [Fact]
    public async Task PptxRejectsOverfullSlideWithoutOverwritingExistingPresentation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var codec = environment.Get<IDocumentFileCodec>();
        var path = Path.Combine(environment.Directory, "folien.pptx");
        await codec.WritePptxAsync("# Titel\nKurzer Inhalt.", path);
        var original = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.WritePptxAsync("# Zu viel\n" + string.Join('\n', Enumerable.Repeat("Eine weitere Textzeile.", 25)), path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        using var presentation = PresentationDocument.Open(path, false);
        AssertValid(presentation);
    }

    [Fact]
    public async Task RealPdfFixtureCanBeOpenedImportedAndReadThroughDocumentTool()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("PDF lesen");
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Pruefbarer PDF Inhalt Seite eins", 12, new PdfPoint(40, 780), font);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Abschluss auf Seite zwei", 12, new PdfPoint(40, 780), font);
        var path = Path.Combine(environment.Directory, "pruefung.pdf");
        await File.WriteAllBytesAsync(path, builder.Build());
        var pages = await environment.Get<IDocumentFileCodec>().ReadAsync(path);
        Assert.Equal(2, pages.Count);
        Assert.Contains("Abschluss auf Seite zwei", pages[1], StringComparison.Ordinal);
        await using var stream = File.OpenRead(path);
        var imported = await environment.Get<IDocumentIngestor>().ImportAsync(session.Id, "pruefung.pdf", stream);
        Assert.True(imported.Success, imported.Error);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var result = Json(await Service(environment, exporter).ReadAsync(JsonSerializer.SerializeToElement(new
        {
            scope = "session", mode = "read", reference = imported.Document!.Id.ToString("D"), startUnit = 2, maximumUnits = 1,
        }), session.Id, CancellationToken.None));
        Assert.Contains("Abschluss auf Seite zwei", result.GetProperty("units")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public void OfficeFormatsPassRealClientToolValidation(string format)
    {
        LocalToolBroker.ValidateProposal(new ToolProposal("office-create", "office-run", ClientToolNames.DocumentCreate,
            Arguments("create", "Datei." + format, format, "start", "Start", InitialTable),
            ToolRiskClass.LocalMutation, "Dokument erzeugen", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    private static JsonElement Arguments(string operation, string reference, string format, string sectionId, string heading, string content, string? hash = null)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["operation"] = operation, ["reference"] = reference, ["format"] = format,
            ["sectionId"] = sectionId, ["heading"] = heading, ["content"] = content,
        };
        if (hash is not null) values["expectedSha256"] = hash;
        return JsonSerializer.SerializeToElement(values);
    }

    private static async Task<string> ExportArtifactAsync(TestEnvironment environment, Guid artifactId)
    {
        var artifact = (await environment.Get<IChatArtifactRepository>().GetAsync(artifactId))!;
        var path = Path.Combine(environment.Directory, artifact.FileName);
        await using var input = await environment.Get<IBinaryObjectStore>().OpenReadAsync(artifact.BlobId);
        await using var output = File.Create(path);
        await input.CopyToAsync(output);
        return path;
    }

    private static void AssertValid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(package).Select(error => error.Description).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static LocalDocumentToolService Service(TestEnvironment environment, DocumentPdfExporter exporter) => new(
        environment.Get<IGeneratedDocumentRepository>(), environment.Get<IDocumentIngestor>(),
        environment.Get<IChatArtifactRepository>(), environment.Get<IBinaryObjectStore>(),
        environment.Get<IChatRepository>(), environment.Get<IDocumentFileCodec>(), exporter);
}
