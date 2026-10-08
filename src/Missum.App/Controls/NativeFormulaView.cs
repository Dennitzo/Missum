using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Missum.App.Controls;

/// <summary>A native, accessible formula; its original LaTeX remains copyable.</summary>
public sealed class NativeFormulaView : Button
{
    private readonly string _source;
    private readonly bool _display;
    private readonly double _fontSize;
    private readonly string? _renderLatex;
    private bool? _renderedDark;
    internal bool IsTypeset { get; private set; }
    internal double RenderedHeight { get; private set; }
    internal double RenderedBaselineOffset { get; private set; }

    public NativeFormulaView(string source, bool display, double fontSize = 16, Uri? linkUri = null, string? renderLatex = null)
    {
        _source = source; _display = display; _fontSize = fontSize; _renderLatex = renderLatex;
        Padding = new Thickness(0);
        MinWidth = 0; MinHeight = 0;
        FontSize = fontSize;
        FontFamily = new FontFamily("Segoe UI Variable Text");
        FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        Foreground = linkUri is null ? NativeThemeBrushes.Text : NativeThemeBrushes.ReadableAccent;
        BorderThickness = new Thickness(0);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(this, "Formel: " + source);
        var copyLabel = renderLatex is null ? "LaTeX kopieren" : "Formeltext kopieren";
        var tooltip = linkUri is null ? copyLabel : "Link öffnen: " + linkUri + " · Rechtsklick: " + copyLabel;
        ToolTipService.SetToolTip(this, tooltip);
        void CopySource() { var package = new DataPackage(); package.SetText(_source); Clipboard.SetContent(package); }
        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = copyLabel };
        copyItem.Click += (_, _) => CopySource();
        menu.Items.Add(copyItem); ContextFlyout = menu;
        Click += async (_, _) =>
        {
            if (linkUri is not null) { await Windows.System.Launcher.LaunchUriAsync(linkUri); return; }
            CopySource();
            ToolTipService.SetToolTip(this, renderLatex is null ? "✓ LaTeX kopiert" : "✓ Formeltext kopiert");
            await Task.Delay(2000);
            ToolTipService.SetToolTip(this, tooltip);
        };
        Loaded += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
        Render();
    }

    private void Render()
    {
        var dark = ActualTheme != ElementTheme.Light;
        if (_renderedDark == dark) return;
        _renderedDark = dark;
        var trim = _source.Trim();
        var delimiter = (trim.StartsWith("$$", StringComparison.Ordinal) && trim.EndsWith("$$", StringComparison.Ordinal))
            || (trim.StartsWith(@"\(", StringComparison.Ordinal) && trim.EndsWith(@"\)", StringComparison.Ordinal))
            || (trim.StartsWith(@"\[", StringComparison.Ordinal) && trim.EndsWith(@"\]", StringComparison.Ordinal)) ? 2
            : trim.StartsWith('$') && trim.EndsWith('$') ? 1 : 0;
        var latex = _renderLatex ?? (delimiter > 0 && trim.Length >= delimiter * 2 ? trim[delimiter..^delimiter] : trim);
        var bitmap = NativeMathRenderer.Render(latex, _display, _fontSize, dark);
        IsTypeset = bitmap.Error is null && bitmap.Png.Length > 0;
        RenderedHeight = bitmap.Height;
        RenderedBaselineOffset = bitmap.BaselineOffset;
        if (!IsTypeset)
        {
            Margin = new Thickness(0);
            Content = new TextBlock { Text = _source, TextWrapping = TextWrapping.Wrap, FontSize = _fontSize,
                FontFamily = FontFamily, IsTextSelectionEnabled = true };
            ToolTipService.SetToolTip(this, "Formel als LaTeX · " + bitmap.Error);
            return;
        }
        using var memory = new MemoryStream(bitmap.Png);
        using var stream = memory.AsRandomAccessStream();
        var source = new BitmapImage();
        source.SetSource(stream);
        Content = new Image { Source = source, Width = bitmap.Width, Height = bitmap.Height, Stretch = Stretch.Uniform };
        // WinUI treats a Button's entire desired height as its inline baseline. Remove the
        // formula's descent from that layout height so its actual TeX baseline meets the
        // surrounding text. The image keeps its natural height, including scripts/fractions.
        Margin = _display ? new Thickness(0) : new Thickness(0, 0, 0, -bitmap.Descent);
    }
}
