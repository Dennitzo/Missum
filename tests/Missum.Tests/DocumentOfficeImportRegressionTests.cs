using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Missum.Core.Contracts;

namespace Missum.Tests;

public sealed class DocumentOfficeImportRegressionTests
{
    private static readonly XNamespace PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [Fact]
    public async Task NestedPowerPointGroupsPreserveSurroundingTextAndEveryTableInDocumentOrder()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "gruppierte-folien.pptx");
        var codec = environment.Get<IDocumentFileCodec>();
        await codec.WritePptxAsync("# Gruppierter Inhalt\nText davor.\n\n| Spalte | Anzahl |\n| --- | --- |\n| Alpha | 2 |\n\nText danach.", path);

        // Start from a real, complete package (masters, theme, layout, relationships)
        // and group its existing native shapes/tables into two nested group levels.
        using (var package = PresentationDocument.Open(path, true))
        {
            var slide = Assert.Single(package.PresentationPart!.SlideParts);
            XDocument xml;
            using (var input = slide.GetStream(FileMode.Open, FileAccess.Read)) xml = XDocument.Load(input);
            var tree = Assert.Single(xml.Descendants(PresentationNamespace + "spTree"));
            var shapes = tree.Elements().Where(element => element.Name == PresentationNamespace + "sp"
                || element.Name == PresentationNamespace + "graphicFrame").ToArray();
            Assert.Equal(4, shapes.Length);
            foreach (var shape in shapes) shape.Remove();
            var secondTable = new XElement(shapes[2]);
            secondTable.Descendants(PresentationNamespace + "cNvPr").Single().SetAttributeValue("id", "200");
            foreach (var text in secondTable.Descendants(DrawingNamespace + "t"))
                if (text.Value == "Alpha") text.Value = "Beta";
            tree.Add(Group(100, shapes[0], Group(101, shapes[1], shapes[2], shapes[3]), secondTable));
            using var output = slide.GetStream(FileMode.Create, FileAccess.Write);
            xml.Save(output);
        }

        using (var package = PresentationDocument.Open(path, false))
        {
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(package).Select(static error => error.Description).ToArray();
            Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
        }
        var expected = new[] { "Gruppierter Inhalt", "Text davor.", "Spalte | Anzahl", "Alpha | 2", "Text danach.", "Spalte | Anzahl", "Beta | 2" };
        var decoded = Assert.Single(await codec.ReadAsync(path));
        Assert.Equal(expected, decoded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Gruppenimport");
        await using var stream = File.OpenRead(path);
        var ingestor = environment.Get<IDocumentIngestor>();
        var imported = await ingestor.ImportAsync(session.Id, Path.GetFileName(path), stream);
        Assert.True(imported.Success, imported.Error);
        var page = Assert.Single(await ingestor.ReadPagesAsync(imported.Document!.Id));
        Assert.Equal(expected, page.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static XElement Group(int id, params XElement[] children) => new(PresentationNamespace + "grpSp",
        new XElement(PresentationNamespace + "nvGrpSpPr",
            new XElement(PresentationNamespace + "cNvPr", new XAttribute("id", id), new XAttribute("name", "Gruppe " + id)),
            new XElement(PresentationNamespace + "cNvGrpSpPr"), new XElement(PresentationNamespace + "nvPr")),
        new XElement(PresentationNamespace + "grpSpPr", new XElement(DrawingNamespace + "xfrm",
            new XElement(DrawingNamespace + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
            new XElement(DrawingNamespace + "ext", new XAttribute("cx", "12192000"), new XAttribute("cy", "6858000")),
            new XElement(DrawingNamespace + "chOff", new XAttribute("x", "0"), new XAttribute("y", "0")),
            new XElement(DrawingNamespace + "chExt", new XAttribute("cx", "12192000"), new XAttribute("cy", "6858000")))),
        children);
}
