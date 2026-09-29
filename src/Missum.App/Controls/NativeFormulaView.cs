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
    private bool? _renderedDark;
    internal bool IsTypeset { get; private set; }

    public NativeFormulaView(string source, bool display, double fontSize = 18, Uri? linkUri = null)
    {
        _source = source; _display = display; _fontSize = fontSize;
        Padding = new Thickness(2, 1, 2, 1);
        MinWidth = 0; MinHeight = 0;
        BorderThickness = new Thickness(0);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(this, "Formel: " + source);
        var tooltip = linkUri is null ? "LaTeX kopieren" : "Link öffnen: " + linkUri + " · Rechtsklick: LaTeX kopieren";
        ToolTipService.SetToolTip(this, tooltip);
        void CopySource() { var package = new DataPackage(); package.SetText(_source); Clipboard.SetContent(package); }
        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = "LaTeX kopieren" };
        copyItem.Click += (_, _) => CopySource();
        menu.Items.Add(copyItem); ContextFlyout = menu;
        Click += async (_, _) =>
        {
            if (linkUri is not null) { await Windows.System.Launcher.LaunchUriAsync(linkUri); return; }
            CopySource();
            ToolTipService.SetToolTip(this, "✓ LaTeX kopiert");
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
        var delimiter = trim.StartsWith("$$", StringComparison.Ordinal) || trim.StartsWith(@"\(", StringComparison.Ordinal) || trim.StartsWith(@"\[", StringComparison.Ordinal) ? 2 : 1;
        var latex = trim.Length >= delimiter * 2 ? trim[delimiter..^delimiter] : trim;
        var bitmap = NativeMathRenderer.Render(latex, _display, _fontSize, dark);
        IsTypeset = bitmap.Error is null && bitmap.Png.Length > 0;
        if (!IsTypeset)
        {
            Content = new TextBlock { Text = _source, TextWrapping = TextWrapping.Wrap, FontSize = 16, IsTextSelectionEnabled = true };
            ToolTipService.SetToolTip(this, "Formel als LaTeX · " + bitmap.Error);
            return;
        }
        using var memory = new MemoryStream(bitmap.Png);
        using var stream = memory.AsRandomAccessStream();
        var source = new BitmapImage();
        source.SetSource(stream);
        Content = new Image { Source = source, Width = bitmap.Width, Height = bitmap.Height, Stretch = Stretch.Uniform };
    }
}
