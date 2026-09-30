using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Missum.App.Controls;

/// <summary>Native Windows PDF rendering; the publication remains a real, exportable PDF.</summary>
internal sealed class NativePublicationView : Grid
{
    private readonly Image _page = new() { Stretch = Stretch.Uniform, MaxWidth = 1150, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _counter = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
    private readonly Button _previous = new() { Content = "‹", Width = 34 };
    private readonly Button _next = new() { Content = "›", Width = 34 };
    private readonly ScrollViewer _scroll;
    private PdfDocument? _document;
    private long _version;
    private uint _index;
    private string? _path;
    internal uint PageCount => _document?.PageCount ?? 0;
    internal bool HasRenderedPage => _page.Source is not null;

    public NativePublicationView()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };
        toolbar.Children.Add(_previous); toolbar.Children.Add(_counter); toolbar.Children.Add(_next);
        Children.Add(toolbar);
        _scroll = new ScrollViewer { Content = _page, Padding = new Thickness(18, 0, 18, 24), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Enabled, MinZoomFactor = .5f, MaxZoomFactor = 3f };
        Grid.SetRow(_scroll, 1); Children.Add(_scroll);
        AutomationProperties.SetName(_previous, "Vorherige PDF-Seite");
        AutomationProperties.SetName(_next, "Nächste PDF-Seite");
        AutomationProperties.SetName(_page, "Wissenschaftliche Publikation als PDF");
        _previous.Click += async (_, _) => { if (_index > 0) { _index--; await RenderPageSafelyAsync(); } };
        _next.Click += async (_, _) => { if (_document is not null && _index + 1 < _document.PageCount) { _index++; await RenderPageSafelyAsync(); } };
        _previous.IsEnabled = _next.IsEnabled = false;
    }

    internal async Task LoadAsync(string path)
    {
        if (path == _path) return;
        var version = ++_version;
        _previous.IsEnabled = _next.IsEnabled = false;
        var document = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        if (version != _version) return;
        _document = document;
        _index = Math.Min(_index, document.PageCount == 0 ? 0 : document.PageCount - 1);
        await RenderPageAsync(version);
        if (version == _version) _path = path;
    }

    private async Task RenderPageSafelyAsync()
    {
        try { await RenderPageAsync(++_version); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _counter.Text = "PDF-Seite konnte nicht geladen werden: " + exception.Message; }
    }

    private async Task RenderPageAsync(long version)
    {
        if (_document is null || _document.PageCount == 0) return;
        var document = _document;
        using var page = document.GetPage(_index);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1600, BackgroundColor = Microsoft.UI.Colors.White });
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        if (version != _version || !ReferenceEquals(document, _document)) return;
        _page.Source = bitmap;
        _counter.Text = $"Seite {_index + 1} von {document.PageCount}";
        _previous.IsEnabled = _index > 0; _next.IsEnabled = _index + 1 < document.PageCount;
        AutomationProperties.SetHelpText(_page, _counter.Text);
    }
}
