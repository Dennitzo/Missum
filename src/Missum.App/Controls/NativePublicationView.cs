using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Missum.App.Controls;

/// <summary>Continuous native PDF pages, with bitmaps loaded only near the viewport.</summary>
internal sealed class NativePublicationView : Grid, IDisposable
{
    private readonly StackPanel _pages = new() { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScrollViewer _scroll;
    private readonly List<Image> _images = [];
    private readonly Dictionary<int, double> _aspectRatios = [];
    private CancellationTokenSource? _loading, _rendering;
    private PdfDocument? _document;
    private Task _renderTask = Task.CompletedTask;
    private long _version;
    private string? _path;
    private bool _renderAgain, _disposed;
    internal uint PageCount => _document?.PageCount ?? 0;
    internal bool HasRenderedPage => _images.Any(image => image.Source is not null);
    internal int PageVisualCount => _images.Count;
    internal bool IsLastPageRendered => _images.Count > 0 && _images[^1].Source is not null;
    internal string ScrollDiagnostics => $"offset={_scroll.VerticalOffset:F1}, end={_scroll.ScrollableHeight:F1}, viewport={_scroll.ViewportHeight:F1}, content={_pages.ActualHeight:F1}, pages={PageCount}, rendered={_images.Count(image => image.Source is not null)}";

    internal async Task ScrollToEndAsync()
    {
        UpdateLayout();
        var end = _scroll.ScrollableHeight;
        if (Math.Abs(_scroll.VerticalOffset - end) > 1)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSettled(object? sender, ScrollViewerViewChangedEventArgs args)
            {
                if (!args.IsIntermediate) changed.TrySetResult();
            }
            _scroll.ViewChanged += OnSettled;
            try
            {
                if (_scroll.ChangeView(null, end, null, true))
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { _scroll.ViewChanged -= OnSettled; }
        }
        UpdateLayout();
        RequestRendering(); await _renderTask;
    }

    public NativePublicationView()
    {
        _scroll = new ScrollViewer
        {
            Content = _pages, Padding = new Thickness(18, 0, 18, 24),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = .5f, MaxZoomFactor = 3f,
        };
        Children.Add(_scroll);
        AutomationProperties.SetName(_scroll, "Wissenschaftliche Publikation, alle PDF-Seiten untereinander");
        _scroll.SizeChanged += (_, _) => { FitPages(); RequestRendering(); };
        _scroll.ViewChanged += (_, _) => RequestRendering();
        Loaded += (_, _) => { FitPages(); RequestRendering(); };
        Unloaded += (_, _) => _rendering?.Cancel();
    }

    internal async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (path == _path && _document is not null) { RequestRendering(); await _renderTask; return; }
        var version = ++_version;
        _loading?.Cancel(); _rendering?.Cancel();
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loading = loading;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(loading.Token);
            var document = await PdfDocument.LoadFromFileAsync(file).AsTask(loading.Token);
            if (_disposed || version != _version) return;
            var offset = _scroll.VerticalOffset;
            _document = document; _path = path; _aspectRatios.Clear(); _images.Clear(); _pages.Children.Clear();
            for (var index = 0; index < document.PageCount; index++)
            {
                var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
                AutomationProperties.SetName(image, $"PDF-Seite {index + 1} von {document.PageCount}");
                _images.Add(image); _pages.Children.Add(image);
            }
            FitPages(); UpdateLayout(); _scroll.ChangeView(null, offset, null, true);
            await _renderTask;
            if (_disposed || version != _version) return;
            RequestRendering(); await _renderTask;
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested || version != _version || _disposed) { }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void FitPages()
    {
        var width = Math.Min(1150, Math.Max(240, _scroll.ActualWidth - 36));
        for (var index = 0; index < _images.Count; index++)
        {
            _images[index].Width = width;
            _images[index].Height = width * _aspectRatios.GetValueOrDefault(index, 1.4142);
        }
    }

    private void RequestRendering()
    {
        if (_disposed || _document is null) return;
        _renderAgain = true;
        if (!_renderTask.IsCompleted) return;
        _renderTask = RenderVisiblePagesSafelyAsync();
    }

    private async Task RenderVisiblePagesSafelyAsync()
    {
        using var rendering = new CancellationTokenSource();
        _rendering = rendering;
        var version = _version;
        try
        {
            while (_renderAgain && !_disposed && version == _version)
            {
                _renderAgain = false;
                var document = _document;
                if (document is null) return;
                var zoom = Math.Max(.5, _scroll.ZoomFactor);
                var viewport = Math.Max(500, _scroll.ViewportHeight / zoom);
                var offset = _scroll.VerticalOffset / zoom;
                double top = 0;
                for (var index = 0; index < _images.Count; index++)
                {
                    rendering.Token.ThrowIfCancellationRequested();
                    var image = _images[index];
                    var bottom = top + image.Height;
                    var near = bottom >= offset - viewport && top <= offset + viewport * 2;
                    top = bottom + _pages.Spacing;
                    if (!near) { image.Source = null; continue; }
                    if (image.Source is not null) continue;
                    using var page = document.GetPage((uint)index);
                    using var stream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                        { DestinationWidth = 1600, BackgroundColor = Microsoft.UI.Colors.White }).AsTask(rendering.Token);
                    stream.Seek(0);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream).AsTask(rendering.Token);
                    if (_disposed || version != _version || !ReferenceEquals(document, _document)) return;
                    _aspectRatios[index] = bitmap.PixelWidth > 0 ? (double)bitmap.PixelHeight / bitmap.PixelWidth : 1.4142;
                    image.Source = bitmap;
                }
                if (!_disposed && version == _version) FitPages();
            }
        }
        catch (OperationCanceledException) when (rendering.IsCancellationRequested || _disposed || version != _version) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_disposed && version == _version)
                AutomationProperties.SetHelpText(_scroll, "PDF-Vorschau konnte nicht geladen werden: " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_rendering, rendering)) _rendering = null;
            if (!_disposed && _renderAgain && IsLoaded && rendering.IsCancellationRequested)
                DispatcherQueue.TryEnqueue(RequestRendering);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_version; _loading?.Cancel(); _rendering?.Cancel();
        _document = null; _path = null;
        foreach (var image in _images) image.Source = null;
        _images.Clear(); _pages.Children.Clear();
    }
}
