using System.Text.Json;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Controls;

/// <summary>Native receipt layout: compact wrapping facts and independently labelled output cards.</summary>
public sealed class NativeToolResultView : StackPanel
{
    public NativeToolResultView(JsonElement data, string path, bool input, bool hidePatch, string? sourceLanguage = null)
        : this(NativeToolResultFormatter.Format(data, path, hidePatch, sourceLanguage)) { }

    internal NativeToolResultView(IEnumerable<NativeToolResultFormatter.Block> content)
    {
        Spacing = 12;
        // Both the parent transcript and subagent tabs use this view. Flat
        // paragraphs/cards avoid recursive JSON trees and wrap at chat width.
        var blocks = content.GetEnumerator();
        NativeToolResultFormatter.Block? next = blocks.MoveNext() ? blocks.Current : null;
        var more = new Button { Content = "Weitere Ergebnisse anzeigen", HorizontalAlignment = HorizontalAlignment.Left };
        void AppendBatch()
        {
            Children.Remove(more);
            for (var index = 0; index < 16 && next is { } block; index++)
            {
                Children.Add(CodeCard(block.Title, block.Text, block.Language ?? "text", block.Path));
                next = blocks.MoveNext() ? blocks.Current : null;
            }
            if (next is not null) Children.Add(more);
            else blocks.Dispose();
        }
        more.Click += (_, _) => AppendBatch();
        AppendBatch();
    }

    private static Brush ResourceBrush(string key, Windows.UI.Color fallback) => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    public static Button CopyButton(string title, string text)
    {
        var button = new Button { Content = title, FontSize = 12, Padding = new(7, 3, 7, 3), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0) };
        button.Click += async (_, _) =>
        {
            var copied = await NativeClipboard.WriteTextAsync(text);
            button.Content = copied ? "✓ Kopiert" : title;
            ToolTipService.SetToolTip(button, copied ? "Kopiert"
                : NativeClipboard.UnavailableMessage);
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
            var text = new TextBlock { FontFamily = new("Cascadia Mono"), Foreground = NativeThemeBrushes.Text, FontSize = language == "text" ? 14 : 13, LineHeight = 23, IsTextSelectionEnabled = true, TextWrapping = language == "text" ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new(12) };
            var scroll = new ScrollViewer { Content = text, MaxHeight = 520, HorizontalScrollBarVisibility = language == "text" ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = language == "text" ? ScrollMode.Disabled : ScrollMode.Enabled };
            var pages = new List<int> { 0 };
            var current = 0;
            var nextOffset = 0;
            var previous = new Button { Content = "Zurück", FontSize = 12 };
            var next = new Button { Content = "Weitere Zeilen", FontSize = 12 };
            var position = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = NativeThemeBrushes.MutedText };
            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new(12, 6, 12, 10) };
            pager.Children.Add(previous); pager.Children.Add(position); pager.Children.Add(next);
            void ShowPage()
            {
                var start = pages[current];
                nextOffset = Math.Min(source.Length, start + 16_384);
                if (nextOffset < source.Length)
                {
                    var line = source.LastIndexOf('\n', nextOffset - 1, nextOffset - start);
                    if (line >= start + 8_192) nextOffset = line + 1;
                    else if (char.IsHighSurrogate(source[nextOffset - 1])) nextOffset--;
                }
                var visible = source[start..nextOffset];
                if (language == "text") text.Text = visible;
                else new NativeCodeHighlighter(text).UpdateText(visible, language);
                previous.IsEnabled = current > 0;
                next.IsEnabled = nextOffset < source.Length;
                position.Text = "Abschnitt " + (current + 1);
                pager.Visibility = pages.Count > 1 || next.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
                scroll.ChangeView(null, 0, null, disableAnimation: true);
            }
            previous.Click += (_, _) => { if (current > 0) { current--; ShowPage(); } };
            next.Click += (_, _) =>
            {
                if (nextOffset >= source.Length) return;
                if (pages.Count == current + 1) pages.Add(nextOffset);
                current++; ShowPage();
            };
            ShowPage();
            body = new StackPanel { Children = { scroll, pager } };
        }
        return Frame(title + (string.IsNullOrEmpty(path) ? "" : "\n" + path), body, CopyButton(language == "diff" ? "Diff kopieren" : "Kopieren", source));
    }

    private static Border Frame(string title, FrameworkElement body, Button? copy = null)
    {
        var header = new Grid { Padding = new(14, 8, 10, 8), ColumnSpacing = 8, Background = ResourceBrush("MissumAccentSubtleBrush", Windows.UI.Color.FromArgb(24, 150, 110, 220)) };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = NativeThemeBrushes.ReadableAccent, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (copy is not null) { Grid.SetColumn(copy, 1); header.Children.Add(copy); }
        var content = new StackPanel();
        content.Children.Add(new Border { Child = header, BorderThickness = new(0, 0, 0, 1), BorderBrush = ResourceBrush("MissumStrokeBrush", Microsoft.UI.Colors.DimGray) });
        content.Children.Add(body);
        return new Border { Child = content, CornerRadius = new(8), BorderThickness = new(1), BorderBrush = ResourceBrush("MissumAccentSubtleBrush", Windows.UI.Color.FromArgb(40, 150, 110, 220)), Background = ResourceBrush("MissumLayerBrush", Windows.UI.Color.FromArgb(255, 27, 27, 27)) };
    }

}
