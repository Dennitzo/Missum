using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Missum.App.Controls;

/// <summary>Native receipt layout: compact wrapping facts and independently labelled output cards.</summary>
public sealed class NativeToolResultView : StackPanel
{
    public NativeToolResultView(JsonElement data, string path, bool input, bool hidePatch, string? sourceLanguage = null)
    {
        Spacing = 12;
        var facts = new FactsPanel();
        var cards = new List<FrameworkElement>();
        foreach (var property in data.EnumerateObject())
        {
            var key = property.Name;
            if (hidePatch && key is "diff" or "stagedDiff" or "content" or "oldText" or "newText" or "edits") continue;
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())) continue;
            if (!PrimaryFields.Contains(key)) continue;
            if (key == "source" && sourceLanguage is not null && value.ValueKind == JsonValueKind.String)
            { cards.Add(CodeCard("Lean-Beweis", value.GetString() ?? "", sourceLanguage)); continue; }
            if (value.ValueKind == JsonValueKind.String && key is "content" or "code" or "text" or "stdout" or "stderr" or "snippet" or "oldText" or "newText")
            {
                var language = key is "stdout" or "stderr" ? "text" : sourceLanguage
                    ?? (string.IsNullOrEmpty(Path.GetExtension(path)) ? "text" : Path.GetExtension(path).TrimStart('.'));
                cards.Add(CodeCard(Label(key), value.GetString() ?? "", language, path));
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array && key is "entries" or "matches" or "results" or "sources" or "citations")
            {
                var results = new StackPanel { Spacing = 12 };
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) results.Children.Add(new NativeToolResultView(item, path, false, false));
                    else results.Children.Add(new TextBlock { Text = item.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = 13 });
                }
                cards.Add(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = Label(key), FontSize = 13, Foreground = ResourceBrush("MissumAccentBrush", Microsoft.UI.Colors.MediumPurple) }, results } });
                continue;
            }
            var fact = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 18, 6) };
            fact.Children.Add(new TextBlock { Text = Label(key), FontSize = 12, Foreground = ResourceBrush("MissumAccentBrush", Microsoft.UI.Colors.MediumPurple), VerticalAlignment = VerticalAlignment.Top });
            var text = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? JsonSerializer.Serialize(value, PrettyJson)
                : value.ValueKind == JsonValueKind.True ? "Ja" : value.ValueKind == JsonValueKind.False ? "Nein" : value.ToString();
            var content = new TextBlock { Text = text, FontFamily = new("Segoe UI Variable Text"), FontSize = 13, Foreground = ResourceBrush("TextFillColorPrimaryBrush", Microsoft.UI.Colors.WhiteSmoke), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) new NativeCodeHighlighter(content).UpdateText(text, "json");
            fact.Children.Add(content); facts.Children.Add(fact);
        }
        // Input facts precede the content; result metadata follows it, as in Go-WinUI.
        if (input && facts.Children.Count > 0) Children.Add(facts);
        foreach (var card in cards) Children.Add(card);
        if (!input && facts.Children.Count > 0) Children.Add(facts);
    }

    private static readonly HashSet<string> PrimaryFields = new(StringComparer.Ordinal)
    {
        "path", "file", "content", "code", "text", "oldText", "newText", "stdout", "stderr", "snippet", "diff", "entries", "matches", "results", "sources", "citations",
        "startLine", "totalLines", "maximumLines", "nextLine", "truncated", "diffTruncated", "exitCode", "workingDirectory", "executable", "arguments",
        "error", "message", "summary", "query", "title", "url", "source", "timedOut"
    };

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static Brush ResourceBrush(string key, Windows.UI.Color fallback) => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    public static Button CopyButton(string title, string text)
    {
        var button = new Button { Content = title, FontSize = 12, Padding = new(7, 3, 7, 3), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0) };
        button.Click += async (_, _) =>
        {
            var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package);
            button.Content = "✓ Kopiert";
            await Task.Delay(2000);
            button.Content = title;
        };
        return button;
    }

    public static Border CodeCard(string title, string source, string language, string? path = null)
    {
        FrameworkElement body;
        if (language == "diff") body = new NativeDiffView(source);
        else
        {
            var text = new TextBlock { FontFamily = new(language == "text" ? "Segoe UI Variable Text" : "Cascadia Mono"), FontSize = language == "text" ? 14 : 13, LineHeight = 23, IsTextSelectionEnabled = true, TextWrapping = language == "text" ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new(12) };
            if (language == "text") text.Text = source;
            else new NativeCodeHighlighter(text).UpdateText(source, language);
            body = new ScrollViewer { Content = text, MaxHeight = 520, HorizontalScrollBarVisibility = language == "text" ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = language == "text" ? ScrollMode.Disabled : ScrollMode.Enabled };
        }
        return Frame(title + (string.IsNullOrEmpty(path) ? "" : "\n" + path), body, CopyButton(language == "diff" ? "Diff kopieren" : "Kopieren", source));
    }

    private static Border Frame(string title, FrameworkElement body, Button? copy = null)
    {
        var header = new Grid { Padding = new(14, 8, 10, 8), ColumnSpacing = 8, Background = ResourceBrush("MissumAccentSubtleBrush", Windows.UI.Color.FromArgb(24, 150, 110, 220)) };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = ResourceBrush("MissumAccentBrush", Microsoft.UI.Colors.MediumPurple), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (copy is not null) { Grid.SetColumn(copy, 1); header.Children.Add(copy); }
        var content = new StackPanel();
        content.Children.Add(new Border { Child = header, BorderThickness = new(0, 0, 0, 1), BorderBrush = ResourceBrush("MissumStrokeBrush", Microsoft.UI.Colors.DimGray) });
        content.Children.Add(body);
        return new Border { Child = content, CornerRadius = new(8), BorderThickness = new(1), BorderBrush = ResourceBrush("MissumAccentSubtleBrush", Windows.UI.Color.FromArgb(40, 150, 110, 220)), Background = ResourceBrush("MissumLayerBrush", Windows.UI.Color.FromArgb(255, 27, 27, 27)) };
    }

    private static string Label(string key) => key switch
    {
        "path" or "file" => "Pfad", "content" or "text" => "Inhalt", "code" => "Code", "oldText" => "Vorher", "newText" => "Nachher", "snippet" => "Auszug", "stdout" => "Standardausgabe", "stderr" => "Fehlerausgabe", "exitCode" => "Exitcode",
        "startLine" => "Ab Zeile", "totalLines" => "Zeilen", "maximumLines" => "Zeilenlimit", "nextLine" => "Fortsetzung ab Zeile", "sha256" => "Prüfsumme", "previousSha256" or "expectedSha256" => "Geprüfte Ausgangsversion",
        "truncated" or "diffTruncated" => "Ausgabe gekürzt", "elapsedMilliseconds" => "Laufzeit (ms)", "workingDirectory" => "Arbeitsordner", "executable" => "Programm", "arguments" => "Argumente",
        "entries" => "Einträge", "matches" or "results" => "Treffer", "sources" or "citations" => "Quellen", "error" => "Fehler", "success" => "Erfolgreich", "summary" => "Zusammenfassung", "toolStatus" => "Status", "query" => "Suchbegriff", "queries" => "Suchbegriffe", "maximumResults" => "Trefferlimit", "title" => "Titel", "url" => "Adresse", "source" => "Quelle", "language" => "Sprache", "profile" => "Profil", "thumbnailUrl" => "Vorschaubild", _ => key
    };

    private sealed class FactsPanel : Panel
    {
        protected override Size MeasureOverride(Size available)
        {
            var width = double.IsInfinity(available.Width) ? 900 : available.Width;
            double x = 0, y = 0, rowHeight = 0;
            foreach (var child in Children)
            {
                if (child is StackPanel fact && fact.Children.Count == 2 && fact.Children[0] is TextBlock label && fact.Children[1] is TextBlock value)
                {
                    label.Measure(new Size(width, double.PositiveInfinity));
                    value.MaxWidth = Math.Max(30, Math.Min(520, width - label.DesiredSize.Width - fact.Spacing - fact.Margin.Right));
                }
                child.Measure(new Size(width, double.PositiveInfinity));
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > width) { x = 0; y += rowHeight; rowHeight = 0; }
                x += Math.Min(width, size.Width); rowHeight = Math.Max(rowHeight, size.Height);
            }
            return new Size(width, y + rowHeight);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, rowHeight = 0;
            foreach (var child in Children)
            {
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width) { x = 0; y += rowHeight; rowHeight = 0; }
                var width = Math.Min(finalSize.Width, size.Width);
                child.Arrange(new Rect(x, y, width, size.Height));
                x += width; rowHeight = Math.Max(rowHeight, size.Height);
            }
            return finalSize;
        }
    }
}
