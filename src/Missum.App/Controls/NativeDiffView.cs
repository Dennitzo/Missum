using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>A selectable, native unified diff with independent old and new line numbers.</summary>
public sealed partial class NativeDiffView : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly SolidColorBrush _neutralForeground = ColorBrush(0xD7, 0xD7, 0xD7);
    private readonly SolidColorBrush _mutedForeground = ColorBrush(0x8D, 0x8D, 0x8D);
    private readonly SolidColorBrush _neutralBackground = ColorBrush(0x19, 0x19, 0x19);
    private readonly SolidColorBrush _addedForeground = ColorBrush(0x31, 0xC7, 0x7D);
    private readonly SolidColorBrush _addedBackground = ColorBrush(0x13, 0x33, 0x25);
    private readonly SolidColorBrush _removedForeground = ColorBrush(0xFF, 0x62, 0x5A);
    private readonly SolidColorBrush _removedBackground = ColorBrush(0x3B, 0x1D, 0x1A);
    private readonly SolidColorBrush _metaBackground = ColorBrush(0x24, 0x24, 0x24);
    private string _diff = "";

    public NativeDiffView(string diff)
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = 420,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            Background = _neutralBackground,
        };
        AutomationProperties.SetName(_scroll, "Dateiänderungen als Git-Diff");
        Content = _scroll;

        var copy = new MenuFlyoutItem { Text = "Gesamten Diff kopieren" };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(_diff);
            Clipboard.SetContent(package);
        };
        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        ContextFlyout = menu;
        UpdateDiff(diff);
    }

    /// <summary>Replaces the unified diff. Empty and incomplete diff fragments remain displayable.</summary>
    public void UpdateDiff(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (_diff == diff && _rows.Children.Count > 0) return;
        _diff = diff;
        _rows.Children.Clear();

        if (string.IsNullOrWhiteSpace(diff))
        {
            AddMeta("Keine Textänderungen verfügbar.");
            return;
        }

        int? oldLine = null;
        int? newLine = null;
        var oldRemaining = 0;
        var newRemaining = 0;
        var language = "";
        var lines = diff.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            // A terminal newline is a separator, not an extra context line.
            if (line.Length == 0 && index == lines.Length - 1) continue;
            var hunk = HunkHeader().Match(line);
            if (hunk.Success)
            {
                oldLine = ParseNumber(hunk.Groups[1].Value);
                newLine = ParseNumber(hunk.Groups[3].Value);
                oldRemaining = hunk.Groups[2].Success ? ParseNumber(hunk.Groups[2].Value) ?? 0 : 1;
                newRemaining = hunk.Groups[4].Success ? ParseNumber(hunk.Groups[4].Value) ?? 0 : 1;
                AddMeta(line);
                continue;
            }

            var insideHunk = oldRemaining > 0 || newRemaining > 0;
            if (line.StartsWith("diff ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || (!insideHunk && (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))))
            {
                if (line.StartsWith("diff ", StringComparison.Ordinal)) language = "";
                else if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var path = line[4..].Split('\t')[0].Trim('"');
                    if (path != "/dev/null") language = NativeCodeHighlighter.LanguageForPath(path);
                }
                oldLine = newLine = null;
                oldRemaining = newRemaining = 0;
                AddMeta(line);
                continue;
            }

            if (line.StartsWith('+'))
            {
                AddLine(line[1..], "+", null, insideHunk ? newLine : null, _addedForeground, _addedBackground, language);
                if (newLine.HasValue) newLine++;
                newRemaining = Math.Max(0, newRemaining - 1);
            }
            else if (line.StartsWith('-'))
            {
                AddLine(line[1..], "−", insideHunk ? oldLine : null, null, _removedForeground, _removedBackground, language);
                if (oldLine.HasValue) oldLine++;
                oldRemaining = Math.Max(0, oldRemaining - 1);
            }
            else if (line.StartsWith(' '))
            {
                AddLine(line[1..], "", insideHunk ? oldLine : null, insideHunk ? newLine : null, _neutralForeground, _neutralBackground, language);
                if (oldLine.HasValue) oldLine++;
                if (newLine.HasValue) newLine++;
                oldRemaining = Math.Max(0, oldRemaining - 1);
                newRemaining = Math.Max(0, newRemaining - 1);
            }
            else
            {
                // Metadata, no-newline markers and truncation notices never consume source lines.
                AddMeta(line);
            }
        }
    }

    private void AddMeta(string text)
    {
        _rows.Children.Add(new Border
        {
            Background = _metaBackground,
            Padding = new Thickness(12, 4, 12, 4),
            Child = CodeText(text, _mutedForeground),
        });
    }

    private void AddLine(string text, string marker, int? oldLine, int? newLine,
        SolidColorBrush foreground, SolidColorBrush background, string language)
    {
        var row = new Grid { Background = background, MinHeight = 24, Padding = new Thickness(0, 2, 12, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddCell(FormatLineNumber(oldLine), 0, _mutedForeground, TextAlignment.Right);
        AddCell(FormatLineNumber(newLine), 1, _mutedForeground, TextAlignment.Right);
        AddCell(marker, 2, foreground, TextAlignment.Center);
        AddCell(text, 3, foreground, TextAlignment.Left);
        _rows.Children.Add(row);

        void AddCell(string value, int column, SolidColorBrush brush, TextAlignment alignment)
        {
            var block = CodeText(value, brush);
            block.TextAlignment = alignment;
            block.Margin = column < 2 ? new Thickness(2, 0, 9, 0) : new Thickness(0);
            block.IsTextSelectionEnabled = column == 3;
            if (column == 3) new NativeCodeHighlighter(block).UpdateText(value, language);
            Grid.SetColumn(block, column);
            row.Children.Add(block);
        }
    }

    private static TextBlock CodeText(string text, SolidColorBrush foreground) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Cascadia Mono"),
        FontSize = 13,
        LineHeight = 20,
        Foreground = foreground,
        TextWrapping = TextWrapping.NoWrap,
        IsTextSelectionEnabled = true,
    };

    private static string FormatLineNumber(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static int? ParseNumber(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static SolidColorBrush ColorBrush(byte red, byte green, byte blue) => new(Color.FromArgb(255, red, green, blue));

    [GeneratedRegex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
