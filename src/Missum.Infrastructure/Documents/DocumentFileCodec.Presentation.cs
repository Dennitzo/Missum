using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace Missum.Infrastructure.Documents;

public sealed partial class DocumentFileCodec
{
    private static readonly XNamespace DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;

    public Task WritePptxAsync(string sourceMarkdown, string outputPath, CancellationToken cancellationToken = default) =>
        WriteOfficeFileAsync(sourceMarkdown, outputPath, (path, sections) =>
        {
            using var document = PresentationDocument.Create(path, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
            var presentation = document.AddPresentationPart();
            var master = presentation.AddNewPart<SlideMasterPart>();
            var layout = master.AddNewPart<SlideLayoutPart>();
            layout.AddPart(master);
            var theme = master.AddNewPart<ThemePart>();
            WriteXml(theme, PresentationTheme());
            WriteXml(layout, P("sldLayout", new XAttribute("type", "blank"), new XAttribute("preserve", "1"),
                P("cSld", new XAttribute("name", "Leer"), EmptyShapeTree()),
                P("clrMapOvr", A("masterClrMapping"))));
            WriteXml(master, P("sldMaster",
                P("cSld", EmptyShapeTree()),
                P("clrMap", new XAttribute("accent1", "accent1"), new XAttribute("accent2", "accent2"),
                    new XAttribute("accent3", "accent3"), new XAttribute("accent4", "accent4"), new XAttribute("accent5", "accent5"),
                    new XAttribute("accent6", "accent6"), new XAttribute("bg1", "lt1"), new XAttribute("bg2", "lt2"),
                    new XAttribute("folHlink", "folHlink"), new XAttribute("hlink", "hlink"), new XAttribute("tx1", "dk1"), new XAttribute("tx2", "dk2")),
                P("sldLayoutIdLst", P("sldLayoutId", new XAttribute("id", "2147483649"),
                    new XAttribute(RelationshipNamespace + "id", master.GetIdOfPart(layout)))),
                P("txStyles", P("titleStyle"), P("bodyStyle"), P("otherStyle"))));

            var slideIds = P("sldIdLst");
            uint slideId = 256;
            foreach (var section in sections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slide = presentation.AddNewPart<SlidePart>();
                slide.AddPart(layout);
                WriteXml(slide, CreateSlide(section, cancellationToken));
                slideIds.Add(P("sldId", new XAttribute("id", slideId++),
                    new XAttribute(RelationshipNamespace + "id", presentation.GetIdOfPart(slide))));
            }
            WriteXml(presentation, P("presentation",
                P("sldMasterIdLst", P("sldMasterId", new XAttribute("id", "2147483648"),
                    new XAttribute(RelationshipNamespace + "id", presentation.GetIdOfPart(master)))),
                slideIds,
                P("sldSz", new XAttribute("cx", SlideWidth), new XAttribute("cy", SlideHeight), new XAttribute("type", "screen16x9")),
                P("notesSz", new XAttribute("cx", "6858000"), new XAttribute("cy", "9144000")),
                P("defaultTextStyle")));
        }, cancellationToken);

    private static XElement CreateSlide(OfficeSection section, CancellationToken cancellationToken)
    {
        var lines = SourceLines(section.Content);
        var paragraphs = new List<string>();
        List<string[]>? table = null;
        var tableParagraphIndex = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            if (TryReadTable(lines, ref index, out var tableRows))
            {
                if (table is not null) throw new InvalidDataException("Eine Folie unterstützt eine Tabelle. Bitte weitere Tabellen als eigene Abschnitte anlegen.");
                table = tableRows;
                tableParagraphIndex = paragraphs.Count;
            }
            else
            {
                var line = HeadingRegex().Replace(lines[index], "$2");
                if (BulletRegex().IsMatch(line)) line = "• " + BulletRegex().Replace(line, string.Empty, 1);
                paragraphs.Add(NormalizeInlineMarkdown(line));
            }
        }
        // Bound slide density instead of silently clipping content in PowerPoint.
        var textLines = paragraphs.Sum(static text => Math.Max(1, (text.Length + 84) / 85));
        if (section.Title.Length > 150 || textLines > 15 || (table is not null && (table.Count > 16 || table[0].Length > 8 || textLines > 5)))
            throw new InvalidDataException("Der Folieninhalt ist zu groß. Bitte in mehrere kurze Abschnitte/Folien aufteilen (höchstens 15 Textzeilen oder eine Tabelle mit 16 Zeilen und 8 Spalten).");

        var shapes = EmptyShapeTree();
        shapes.Add(TextBox(2, "Titel", section.Title, 600000, 370000, 10992000, 950000, 3000, true));
        if (table is null)
        {
            if (paragraphs.Count > 0)
                shapes.Add(TextBox(3, "Inhalt", string.Join('\n', paragraphs), 600000, 1510000, 10992000, 4900000, 2100, false));
        }
        else
        {
            var before = paragraphs.Take(tableParagraphIndex).ToArray();
            var after = paragraphs.Skip(tableParagraphIndex).ToArray();
            var beforeHeight = before.Sum(static text => Math.Max(1, (text.Length + 84) / 85)) * 360000L;
            var afterHeight = after.Sum(static text => Math.Max(1, (text.Length + 84) / 85)) * 360000L;
            var top = 1540000 + beforeHeight + (before.Length > 0 ? 140000 : 0);
            var bottom = 6200000 - afterHeight - (after.Length > 0 ? 140000 : 0);
            if (before.Length > 0)
                shapes.Add(TextBox(3, "Einleitung", string.Join('\n', before), 600000, 1510000, 10992000, beforeHeight, 2100, false));
            shapes.Add(SlideTable(table, top, bottom - top));
            if (after.Length > 0)
                shapes.Add(TextBox(5, "Nachbemerkung", string.Join('\n', after), 600000, bottom + 140000, 10992000, afterHeight, 2100, false));
        }
        return P("sld", P("cSld",
            P("bg", P("bgPr", A("solidFill", A("srgbClr", new XAttribute("val", "FFFFFF"))), A("effectLst"))),
            shapes), P("clrMapOvr", A("masterClrMapping")));
    }

    private static XElement EmptyShapeTree() => P("spTree",
        P("nvGrpSpPr", P("cNvPr", new XAttribute("id", "1"), new XAttribute("name", "")), P("cNvGrpSpPr"), P("nvPr")),
        P("grpSpPr", A("xfrm", Point("off", 0, 0), Size("ext", 0, 0), Point("chOff", 0, 0), Size("chExt", 0, 0))));

    private static XElement TextBox(uint id, string name, string text, long x, long y, long width, long height, int fontSize, bool bold) =>
        P("sp", P("nvSpPr", P("cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                P("cNvSpPr", new XAttribute("txBox", "1")), P("nvPr")),
            P("spPr", A("xfrm", Point("off", x, y), Size("ext", width, height)),
                A("prstGeom", new XAttribute("prst", "rect"), A("avLst")), A("noFill"), A("ln", A("noFill"))),
            P("txBody", A("bodyPr", new XAttribute("wrap", "square"), new XAttribute("lIns", "0"),
                    new XAttribute("tIns", "0"), new XAttribute("rIns", "0"), new XAttribute("bIns", "0"), A("normAutofit")),
                A("lstStyle"), SourceLines(text).Select(line => DrawingParagraph(line, fontSize, bold))));

    private static XElement DrawingParagraph(string text, int fontSize, bool bold = false) => A("p",
        A("pPr", A("spcAft", A("spcPts", new XAttribute("val", "550")))),
        A("r", A("rPr", new XAttribute("lang", "de-DE"), new XAttribute("sz", fontSize), new XAttribute("b", bold ? "1" : "0"),
                A("solidFill", A("srgbClr", new XAttribute("val", "172033"))), A("latin", new XAttribute("typeface", "Aptos"))),
            A("t", text)),
        A("endParaRPr", new XAttribute("lang", "de-DE"), new XAttribute("sz", fontSize)));

    private static XElement SlideTable(List<string[]> rows, long top, long height)
    {
        var width = 10992000L;
        var columnCount = rows[0].Length;
        var columnWidth = width / columnCount;
        var fontSize = columnCount <= 4 ? 1600 : 1300;
        var rowWeights = rows.Select(row => Math.Max(1, row.Max(value => (NormalizeInlineMarkdown(value).Length + Math.Max(12, 110 / columnCount) - 1) / Math.Max(12, 110 / columnCount)))).ToArray();
        if (rowWeights.Sum() > (height / 210000))
            throw new InvalidDataException("Die Tabelle enthält zu viel Text für eine Folie. Bitte Tabelle auf mehrere Folien verteilen.");
        var table = A("tbl", A("tblPr", new XAttribute("firstRow", "1"), new XAttribute("bandRow", "1")),
            A("tblGrid", Enumerable.Range(0, columnCount).Select(index => A("gridCol", new XAttribute("w", index == columnCount - 1 ? width - columnWidth * index : columnWidth)))));
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = A("tr", new XAttribute("h", height * rowWeights[rowIndex] / rowWeights.Sum()));
            foreach (var text in rows[rowIndex])
            {
                row.Add(A("tc", A("txBody", A("bodyPr"), A("lstStyle"), DrawingParagraph(NormalizeInlineMarkdown(text), fontSize, rowIndex == 0)),
                    A("tcPr", new XAttribute("marL", "110000"), new XAttribute("marR", "110000"),
                        new XAttribute("marT", "50000"), new XAttribute("marB", "50000"),
                        A("solidFill", A("srgbClr", new XAttribute("val", rowIndex == 0 ? "DBEAFE" : rowIndex % 2 == 0 ? "F1F5F9" : "FFFFFF"))))));
            }
            table.Add(row);
        }
        return P("graphicFrame", P("nvGraphicFramePr", P("cNvPr", new XAttribute("id", "4"), new XAttribute("name", "Tabelle")),
                P("cNvGraphicFramePr"), P("nvPr")),
            P("xfrm", Point("off", 600000, top), Size("ext", width, height)),
            A("graphic", A("graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/table"), table)));
    }

    private static List<string> ReadPptx(string path, CancellationToken cancellationToken)
    {
        using var document = PresentationDocument.Open(path, false);
        var presentation = document.PresentationPart ?? throw new InvalidDataException("PPTX enthält keine Präsentation.");
        var slideIds = presentation.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>()
            ?? throw new InvalidDataException("PPTX enthält keine Folienliste.");
        var pages = new List<string>();
        var totalCharacters = 0;
        foreach (var id in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id.RelationshipId?.Value is not { } relationship || presentation.GetPartById(relationship) is not SlidePart slide) continue;
            using var stream = slide.GetStream(FileMode.Open, FileAccess.Read);
            var xml = XDocument.Load(stream);
            var page = new StringBuilder();
            // Traverse semantic content in document order, including every nested
            // group. Table paragraphs are emitted by their rows, never a second time.
            var content = xml.Descendants(PresentationNamespace + "spTree").Descendants()
                .Where(static element => element.Name == DrawingNamespace + "tbl"
                    || (element.Name == DrawingNamespace + "p" && !element.Ancestors(DrawingNamespace + "tbl").Any()));
            foreach (var element in content)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (element.Name == DrawingNamespace + "tbl")
                {
                    foreach (var row in element.Elements(DrawingNamespace + "tr"))
                        page.AppendLine(string.Join(" | ", row.Elements(DrawingNamespace + "tc")
                            .Select(cell => string.Join(" ", cell.Descendants(DrawingNamespace + "t").Select(static text => text.Value)))));
                }
                else
                {
                    page.AppendLine(string.Concat(element.Descendants(DrawingNamespace + "t").Select(static text => text.Value)));
                }
                if (page.Length + totalCharacters > MaximumOfficeText)
                    throw new InvalidDataException("Die Präsentation überschreitet die Lesegrenze von 4 MiB Text.");
            }
            totalCharacters += page.Length;
            pages.Add(page.ToString().TrimEnd());
        }
        return pages.Count == 0 ? [string.Empty] : pages;
    }

    private static XElement PresentationTheme()
    {
        var colors = new (string Name, string Value)[]
        {
            ("dk1", "172033"), ("lt1", "FFFFFF"), ("dk2", "334155"), ("lt2", "F1F5F9"),
            ("accent1", "2563EB"), ("accent2", "0D9488"), ("accent3", "EA580C"), ("accent4", "7C3AED"),
            ("accent5", "DB2777"), ("accent6", "475569"), ("hlink", "2563EB"), ("folHlink", "7C3AED"),
        };
        return A("theme", new XAttribute("name", "Missum"), A("themeElements",
            A("clrScheme", new XAttribute("name", "Missum"), colors.Select(color => A(color.Name, A("srgbClr", new XAttribute("val", color.Value))))),
            A("fontScheme", new XAttribute("name", "Missum"),
                A("majorFont", A("latin", new XAttribute("typeface", "Aptos Display")), A("ea", new XAttribute("typeface", "")), A("cs", new XAttribute("typeface", ""))),
                A("minorFont", A("latin", new XAttribute("typeface", "Aptos")), A("ea", new XAttribute("typeface", "")), A("cs", new XAttribute("typeface", "")))),
            A("fmtScheme", new XAttribute("name", "Missum"),
                A("fillStyleLst", Enumerable.Range(0, 3).Select(static _ => A("solidFill", A("schemeClr", new XAttribute("val", "phClr"))))),
                A("lnStyleLst", Enumerable.Range(1, 3).Select(index => A("ln", new XAttribute("w", index * 6350),
                    A("solidFill", A("schemeClr", new XAttribute("val", "phClr"))), A("prstDash", new XAttribute("val", "solid"))))),
                A("effectStyleLst", Enumerable.Range(0, 3).Select(static _ => A("effectStyle", A("effectLst")))),
                A("bgFillStyleLst", Enumerable.Range(0, 3).Select(static _ => A("solidFill", A("schemeClr", new XAttribute("val", "phClr"))))))));
    }

    private static XElement P(string name, params object[] content) => new(PresentationNamespace + name, content);
    private static XElement A(string name, params object[] content) => new(DrawingNamespace + name, content);
    private static XElement Point(string name, long x, long y) => A(name, new XAttribute("x", x), new XAttribute("y", y));
    private static XElement Size(string name, long width, long height) => A(name, new XAttribute("cx", width), new XAttribute("cy", height));

    private static void WriteXml(OpenXmlPart part, XElement root)
    {
        root.SetAttributeValue(XNamespace.Xmlns + "a", DrawingNamespace.NamespaceName);
        root.SetAttributeValue(XNamespace.Xmlns + "p", PresentationNamespace.NamespaceName);
        root.SetAttributeValue(XNamespace.Xmlns + "r", RelationshipNamespace.NamespaceName);
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(stream);
    }
}
