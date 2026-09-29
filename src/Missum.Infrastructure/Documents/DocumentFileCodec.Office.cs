using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Missum.Infrastructure.Documents;

public sealed partial class DocumentFileCodec
{
    private const int MaximumOfficeText = 4 * 1024 * 1024;

    public Task WriteXlsxAsync(string sourceMarkdown, string outputPath, CancellationToken cancellationToken = default) =>
        WriteOfficeFileAsync(sourceMarkdown, outputPath, (path, sections) =>
        {
            using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var sheets = new S.Sheets();
            workbook.Workbook.Append(sheets);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint sheetId = 0;
            foreach (var section in sections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = workbook.AddNewPart<WorksheetPart>();
                var rows = new S.SheetData();
                var columns = new S.Columns();
                var lines = SourceLines(section.Content);
                uint rowIndex = 0;
                var maxColumns = 1;
                for (var index = 0; index < lines.Length; index++)
                {
                    if (string.IsNullOrWhiteSpace(lines[index])) continue;
                    if (TryReadTable(lines, ref index, out var table))
                    {
                        foreach (var values in table) AddRow(values);
                    }
                    else
                    {
                        AddRow([HeadingRegex().Replace(lines[index], "$2")]);
                    }
                }
                if (rowIndex == 0) AddRow([string.Empty]);
                for (var column = 1; column <= maxColumns; column++)
                    columns.Append(new S.Column { Min = (uint)column, Max = (uint)column, Width = column == 1 ? 30D : 22D, CustomWidth = true });
                part.Worksheet = new S.Worksheet(columns, rows);
                part.Worksheet.Save();
                sheets.Append(new S.Sheet
                {
                    Id = workbook.GetIdOfPart(part), SheetId = ++sheetId,
                    Name = UniqueSheetName(section.Title, names),
                });

                void AddRow(IReadOnlyList<string> values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (values.Count > 16384 || rowIndex >= 1048576)
                        throw new InvalidDataException("Die Tabelle überschreitet die XLSX-Zellgrenzen.");
                    var row = new S.Row { RowIndex = ++rowIndex };
                    maxColumns = Math.Max(maxColumns, values.Count);
                    for (var index = 0; index < values.Count; index++)
                        row.Append(SpreadsheetCell(values[index], ColumnName(index + 1) + rowIndex.ToString(CultureInfo.InvariantCulture)));
                    rows.Append(row);
                }
            }
            workbook.Workbook.Append(new S.CalculationProperties
            {
                CalculationMode = S.CalculateModeValues.Auto,
                FullCalculationOnLoad = true, ForceFullCalculation = true,
            });
            workbook.Workbook.Save();
        }, cancellationToken);

    private static S.Cell SpreadsheetCell(string source, string reference)
    {
        var value = source.Trim();
        if (value.Length > 32767) throw new InvalidDataException("Eine XLSX-Zelle darf höchstens 32767 Zeichen enthalten.");
        var cell = new S.Cell { CellReference = reference };
        if (value.StartsWith('='))
        {
            var formula = value[1..];
            if (formula.Length == 0 || formula.Length > 8192 || ExternalFormulaRegex().IsMatch(formula))
                throw new InvalidDataException("Formeln dürfen nur lokale Arbeitsblattdaten verwenden; externe Datenquellen und leere Formeln werden nicht unterstützt.");
            cell.CellFormula = new S.CellFormula(formula);
            // Do not invent a cached result. Excel/LibreOffice recalculates on open.
        }
        else if (!value.StartsWith('\'') && bool.TryParse(value, out var boolean))
        {
            cell.DataType = S.CellValues.Boolean;
            cell.CellValue = new S.CellValue(boolean ? "1" : "0");
        }
        else if (InvariantNumberRegex().IsMatch(value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number))
        {
            cell.DataType = S.CellValues.Number;
            cell.CellValue = new S.CellValue(value);
        }
        else
        {
            cell.DataType = S.CellValues.InlineString;
            cell.InlineString = new S.InlineString(new S.Text(value.StartsWith('\'') ? value[1..] : NormalizeInlineMarkdown(value))
            {
                Space = SpaceProcessingModeValues.Preserve,
            });
        }
        return cell;
    }

    private static List<string> ReadXlsx(string path, CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("XLSX enthält keine Arbeitsmappe.");
        var sheets = workbook.Workbook?.GetFirstChild<S.Sheets>()?.Elements<S.Sheet>()
            ?? throw new InvalidDataException("XLSX enthält keine Arbeitsblätter.");
        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>()
            .Select(static item => item.InnerText).ToArray() ?? [];
        var pages = new List<string>();
        var totalCharacters = 0;
        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart worksheet) continue;
            var page = new StringBuilder().Append("# ").AppendLine(sheet.Name?.Value ?? "Arbeitsblatt");
            var sheetRoot = worksheet.Worksheet ?? throw new InvalidDataException("XLSX-Arbeitsblatt enthält keine Zellstruktur.");
            // Excel normally stores one master formula followed by empty shared-formula
            // elements. Keep the master and the cached value, including when the master
            // occurs after a follower, instead of presenting an empty formula as "=".
            var sharedFormulas = sheetRoot.Descendants<S.Cell>()
                .Where(static cell => cell.CellFormula is { } formula && formula.FormulaType?.Value == S.CellFormulaValues.Shared
                    && formula.SharedIndex is not null && !string.IsNullOrWhiteSpace(formula.Text))
                .GroupBy(static cell => cell.CellFormula!.SharedIndex!.Value)
                .ToDictionary(static group => group.Key, static group => group.First());
            foreach (var row in sheetRoot.Descendants<S.Row>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new List<string>();
                foreach (var cell in row.Elements<S.Cell>())
                {
                    var column = ColumnIndex(cell.CellReference?.Value);
                    while (values.Count < column - 1) values.Add(string.Empty);
                    var text = ReadSpreadsheetCell(cell, sharedStrings, sharedFormulas);
                    values.Add(text);
                }
                page.AppendLine(string.Join(" | ", values));
                if (page.Length + totalCharacters > MaximumOfficeText)
                    throw new InvalidDataException("Die Arbeitsmappe überschreitet die Lesegrenze von 4 MiB Text.");
            }
            totalCharacters += page.Length;
            pages.Add(page.ToString().TrimEnd());
        }
        return pages.Count == 0 ? [string.Empty] : pages;
    }

    private static string ReadSpreadsheetCell(S.Cell cell, string[] sharedStrings,
        Dictionary<uint, S.Cell> sharedFormulas)
    {
        var value = cell.DataType?.Value == S.CellValues.SharedString
                && int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex)
                && sharedIndex >= 0 && sharedIndex < sharedStrings.Length ? sharedStrings[sharedIndex]
            : cell.DataType?.Value == S.CellValues.InlineString ? cell.InlineString?.InnerText ?? string.Empty
            : cell.DataType?.Value == S.CellValues.Boolean && cell.CellValue is not null ? cell.CellValue.Text == "1" ? "TRUE" : "FALSE"
            : cell.CellValue?.Text ?? string.Empty;
        if (cell.CellFormula is not { } formula) return value;
        if (!string.IsNullOrWhiteSpace(formula.Text)) return "=" + formula.Text;

        var cached = cell.CellValue is null ? "kein gespeicherter Ergebniswert" : "gespeicherter Ergebniswert: " + value;
        if (formula.FormulaType?.Value == S.CellFormulaValues.Shared
            && formula.SharedIndex?.Value is { } index && sharedFormulas.TryGetValue(index, out var master))
        {
            // A formula evaluator / token parser is deliberately not approximated here:
            // relative references, names and quoted strings need Excel semantics. State
            // both coordinates and preserve the complete master expression explicitly.
            return $"[Geteilte Formel; Ziel {cell.CellReference?.Value ?? "unbekannt"}; Basis {master.CellReference?.Value ?? "unbekannt"}: ={master.CellFormula!.Text}; {cached}; relative Formel nicht aufgelöst, Ergebnis nicht neu berechnet]";
        }
        return $"[Formel ohne Ausdruck; Zelle {cell.CellReference?.Value ?? "unbekannt"}; {cached}; Formelbasis fehlt]";
    }

    /// <summary>Prevents section-title changes from silently breaking cross-sheet formulas.</summary>
    public static void ValidateXlsxRevision(string previousSource, string revisedSource)
    {
        var previous = OfficeSections(previousSource);
        var revised = OfficeSections(revisedSource);
        var previousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var revisedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oldNames = previous.Select(section => UniqueSheetName(section.Title, previousNames)).ToArray();
        var newNames = revised.Select(section => UniqueSheetName(section.Title, revisedNames)).ToArray();
        if (!oldNames.Where((name, index) => index >= newNames.Length
                || !string.Equals(name, newNames[index], StringComparison.OrdinalIgnoreCase)).Any()) return;

        // Keep this conservative for 3-D references and INDIRECT: changing a sheet in
        // the middle of a range, or a dynamically constructed name, can affect a formula
        // without spelling that sheet's old name in the expression.
        if (revised.Any(section => WorksheetFormulaValues(section.Content)
            .Any(static value => value.Contains('!') || Regex.IsMatch(value, @"\bINDIRECT\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))))
        {
            throw new InvalidDataException("Der Blattname kann bei vorhandenen Formeln mit Blattverweisen nicht geändert werden. Behalte die bisherige Überschrift (heading) bei oder entferne zuerst die betroffenen Querverweise.");
        }
    }

    private static IEnumerable<string> WorksheetFormulaValues(string content)
    {
        var lines = SourceLines(content);
        for (var index = 0; index < lines.Length; index++)
        {
            if (TryReadTable(lines, ref index, out var table))
            {
                foreach (var value in table.SelectMany(static row => row))
                    if (value.TrimStart().StartsWith('=')) yield return value.Trim();
            }
            else
            {
                var value = HeadingRegex().Replace(lines[index], "$2").Trim();
                if (value.StartsWith('=')) yield return value;
            }
        }
    }

    private static string UniqueSheetName(string title, HashSet<string> names)
    {
        var cleaned = new string(title.Where(static character => !char.IsControl(character)
            && "[]:*?/\\".IndexOf(character, StringComparison.Ordinal) < 0).ToArray()).Trim().Trim('\'');
        if (cleaned.Length == 0) cleaned = "Arbeitsblatt";
        if (cleaned.Length > 31) cleaned = cleaned[..31];
        var candidate = cleaned;
        for (var counter = 2; !names.Add(candidate); counter++)
        {
            var suffix = " (" + counter.ToString(CultureInfo.InvariantCulture) + ")";
            candidate = cleaned[..Math.Min(cleaned.Length, 31 - suffix.Length)] + suffix;
        }
        return candidate;
    }

    private static string ColumnName(int index)
    {
        var result = string.Empty;
        while (index > 0)
        {
            index--;
            result = (char)('A' + index % 26) + result;
            index /= 26;
        }
        return result;
    }

    private static int ColumnIndex(string? reference)
    {
        var index = 0;
        foreach (var character in reference ?? string.Empty)
        {
            if (!char.IsAsciiLetter(character)) break;
            index = index * 26 + char.ToUpperInvariant(character) - 'A' + 1;
            if (index > 16384) throw new InvalidDataException("Ungültige XLSX-Zellreferenz.");
        }
        return index;
    }

    private static async Task WriteOfficeFileAsync(string source, string outputPath,
        Action<string, IReadOnlyList<OfficeSection>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var sections = OfficeSections(source);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Ungültiger Office-Zielpfad.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                write(temporaryPath, sections);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private static List<OfficeSection> OfficeSections(string source)
    {
        var matches = OfficeSectionRegex().Matches(source);
        var sections = new List<OfficeSection>();
        if (matches.Count == 0) sections.Add(Section("Dokument", source));
        foreach (Match match in matches)
            sections.Add(Section(match.Groups["id"].Value, match.Groups["body"].Value));
        if (sections.Count > 200) throw new InvalidDataException("Office-Dokumente dürfen höchstens 200 Blätter oder Folien enthalten.");
        return sections;

        static OfficeSection Section(string fallbackTitle, string content)
        {
            var lines = SourceLines(content.Trim());
            var firstHeading = lines.Length > 0 ? HeadingRegex().Match(lines[0]) : Match.Empty;
            return firstHeading.Success
                ? new(NormalizeInlineMarkdown(firstHeading.Groups[2].Value), string.Join('\n', lines.Skip(1)).Trim())
                : new(fallbackTitle, content.Trim());
        }
    }

    private static string[] SourceLines(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static bool TryReadTable(string[] lines, ref int index, out List<string[]> rows)
    {
        rows = [];
        var first = SplitTableRow(lines[index]);
        if (first is null) return false;
        var tabSeparated = lines[index].Contains('\t');
        // A Markdown table needs a delimiter row; TSV is an explicit structured form.
        if (!tabSeparated && (index + 1 >= lines.Length || !IsTableDelimiter(lines[index + 1]))) return false;
        rows.Add(first);
        var next = index + (tabSeparated ? 1 : 2);
        while (next < lines.Length && !string.IsNullOrWhiteSpace(lines[next]))
        {
            var values = SplitTableRow(lines[next]);
            if (values is null || (!tabSeparated && IsTableDelimiter(lines[next]))) break;
            rows.Add(values);
            next++;
        }
        var width = rows.Max(static row => row.Length);
        for (var row = 0; row < rows.Count; row++)
            if (rows[row].Length < width) rows[row] = rows[row].Concat(Enumerable.Repeat(string.Empty, width - rows[row].Length)).ToArray();
        index = next - 1;
        return true;
    }

    private static string[]? SplitTableRow(string line)
    {
        if (line.Contains('\t')) return line.Split('\t').Select(static value => value.Trim()).ToArray();
        var trimmed = line.Trim();
        if (!trimmed.Contains('|')) return null;
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return Regex.Split(trimmed, @"(?<!\\)\|").Select(static value => value.Replace("\\|", "|", StringComparison.Ordinal).Trim()).ToArray();
    }

    private static bool IsTableDelimiter(string line) => SplitTableRow(line) is { Length: > 0 } cells
        && cells.All(static cell => Regex.IsMatch(cell, @"^:?-{3,}:?$", RegexOptions.CultureInvariant));

    private static W.Table CreateWordTable(List<string[]> rows)
    {
        var table = new W.Table(new W.TableProperties(
            new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Color = "CBD5E1" },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4U, Color = "CBD5E1" },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Color = "CBD5E1" },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4U, Color = "CBD5E1" },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4U, Color = "CBD5E1" },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4U, Color = "CBD5E1" })));
        table.Append(new W.TableGrid(rows[0].Select(static _ => new W.GridColumn())));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = new W.TableRow();
            foreach (var value in rows[index])
            {
                var run = new W.Run();
                if (index == 0) run.Append(new W.RunProperties(new W.Bold()));
                run.Append(new W.Text(NormalizeInlineMarkdown(value)) { Space = SpaceProcessingModeValues.Preserve });
                row.Append(new W.TableCell(new W.Paragraph(run)));
            }
            table.Append(row);
        }
        return table;
    }

    private sealed record OfficeSection(string Title, string Content);

    [GeneratedRegex(@"<!-- Missum-DOCUMENT-SECTION:(?<id>[^\s>]+) -->\s*(?<body>[\s\S]*?)\s*<!-- /Missum-DOCUMENT-SECTION:\k<id> -->", RegexOptions.CultureInvariant)]
    private static partial Regex OfficeSectionRegex();

    [GeneratedRegex(@"^[+-]?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex InvariantNumberRegex();

    [GeneratedRegex(@"[\[\]|]|(?:https?|ftp|file):|(?:WEBSERVICE|RTD|DDE|FILTERXML|IMAGE|HYPERLINK)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalFormulaRegex();
}
