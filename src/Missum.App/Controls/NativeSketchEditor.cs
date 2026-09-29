using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Missum.App.Controls;

/// <summary>A session-owned native drawing surface that can remain open inside an assistant tab.</summary>
public sealed class NativeSketchEditor : IDisposable
{
    private const int CanvasWidth = 600;
    private const int CanvasHeight = 360;
    private const int MaximumStrokes = 256;
    private const int MaximumPointsPerStroke = 4096;
    private readonly Canvas _canvas;
    private readonly List<Polyline> _strokes = [];
    private readonly Dictionary<uint, Polyline> _activeStrokes = [];
    private readonly Button _undoButton;
    private readonly Button _clearButton;
    private bool _saving;
    private bool _disposed;

    public NativeSketchEditor()
    {
        _canvas = new Canvas
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            ManipulationMode = ManipulationModes.None,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, CanvasWidth, CanvasHeight) },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_canvas,
            "Skizzenfläche: mit Maus, Stift oder Finger zeichnen");

        _undoButton = new Button { Content = "Rückgängig", IsEnabled = false };
        _clearButton = new Button { Content = "Leeren", IsEnabled = false };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(_undoButton);
        toolbar.Children.Add(_clearButton);
        var drawingView = new Viewbox
        {
            MaxWidth = CanvasWidth,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _canvas,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Zeichne mit Maus, Stift oder Finger. Die Skizze wird erst nach „An Chat anhängen“ gespeichert.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(toolbar);
        content.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = drawingView,
        });
        View = content;

        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerEnded;
        _canvas.PointerCanceled += OnPointerEnded;
        _canvas.PointerCaptureLost += OnPointerCaptureLost;
        _undoButton.Click += OnUndo;
        _clearButton.Click += OnClear;
    }

    public FrameworkElement View { get; }
    public bool HasStrokes => _strokes.Count > 0;
    public event EventHandler? Changed;

    public async Task<string> SaveAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasStrokes) throw new InvalidOperationException("Zeichne zuerst etwas auf die Skizzenfläche.");
        token.ThrowIfCancellationRequested();
        _saving = true;
        ReleaseCaptures();
        UpdateButtons();
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(_canvas, CanvasWidth, CanvasHeight).AsTask(token);
            var pixels = await bitmap.GetPixelsAsync().AsTask(token);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(token);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync().AsTask(token);
            stream.Seek(0);
            using var source = stream.AsStreamForRead();
            using var png = new MemoryStream();
            await source.CopyToAsync(png, token);
            var directory = System.IO.Path.Combine(App.Current.DataDirectory, "attachments", "sketches");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, $"Skizze-{Guid.NewGuid():N}.png");
            try
            {
                await File.WriteAllBytesAsync(path, png.ToArray(), token);
                return path;
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        }
        finally
        {
            _saving = false;
            UpdateButtons();
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || _saving || _strokes.Count >= MaximumStrokes || _activeStrokes.ContainsKey(args.Pointer.PointerId)) return;
        if (!_canvas.CapturePointer(args.Pointer)) return;
        var point = ClampPoint(args.GetCurrentPoint(_canvas).Position);
        var stroke = new Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Black),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            Points = new PointCollection { point, new Point(point.X + 0.01, point.Y) },
        };
        _strokes.Add(stroke);
        _activeStrokes.Add(args.Pointer.PointerId, stroke);
        _canvas.Children.Add(stroke);
        UpdateButtons();
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_activeStrokes.TryGetValue(args.Pointer.PointerId, out var stroke)) return;
        foreach (var sample in args.GetIntermediatePoints(_canvas).Reverse())
        {
            if (stroke.Points.Count >= MaximumPointsPerStroke) break;
            var point = ClampPoint(sample.Position);
            var previous = stroke.Points[^1];
            var dx = point.X - previous.X;
            var dy = point.Y - previous.Y;
            if (dx * dx + dy * dy >= 0.25) stroke.Points.Add(point);
        }
        args.Handled = true;
    }

    private void OnPointerEnded(object sender, PointerRoutedEventArgs args)
    {
        if (_activeStrokes.Remove(args.Pointer.PointerId))
        {
            _canvas.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args) =>
        _activeStrokes.Remove(args.Pointer.PointerId);

    private void OnUndo(object sender, RoutedEventArgs args)
    {
        ReleaseCaptures();
        if (_strokes.Count == 0) return;
        _canvas.Children.Remove(_strokes[^1]);
        _strokes.RemoveAt(_strokes.Count - 1);
        UpdateButtons();
    }

    private void OnClear(object sender, RoutedEventArgs args)
    {
        ReleaseCaptures();
        _strokes.Clear();
        _canvas.Children.Clear();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _undoButton.IsEnabled = HasStrokes && !_saving;
        _clearButton.IsEnabled = HasStrokes && !_saving;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseCaptures()
    {
        _canvas.ReleasePointerCaptures();
        _activeStrokes.Clear();
    }

    private static Point ClampPoint(Point point) => new(
        Math.Clamp(point.X, 0, CanvasWidth),
        Math.Clamp(point.Y, 0, CanvasHeight));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseCaptures();
        _canvas.PointerPressed -= OnPointerPressed;
        _canvas.PointerMoved -= OnPointerMoved;
        _canvas.PointerReleased -= OnPointerEnded;
        _canvas.PointerCanceled -= OnPointerEnded;
        _canvas.PointerCaptureLost -= OnPointerCaptureLost;
        _undoButton.Click -= OnUndo;
        _clearButton.Click -= OnClear;
    }
}
