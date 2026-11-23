using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Missum.App.Controls;
using Missum.App.Services.Meetings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Missum.App.Pages;

/// <summary>
/// The free canvas of the meeting whiteboard (after Prezi): text boxes, tables,
/// images, shapes and arrows placed anywhere on a large zoomable surface. Left
/// drag on empty space pans the board; left drag on an element moves it.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private Canvas? _boardArrows;
    private Canvas? _boardElementsLayer;
    private readonly Dictionary<string, CanvasElementView> _canvasViews = new(StringComparer.Ordinal);
    private string? _selectedElementId;
    private string? _editingElementId;
    private string? _arrowStartId;
    private bool _arrowTool;
    private bool _boardPanning;
    private Point _panStart;
    private (double H, double V) _panOffset;
    private CanvasDrag? _canvasDrag;

    /// <summary>Called once while the board root is built.</summary>
    private void CreateCanvasLayers(BoardPanSurface surface)
    {
        surface.Width = MeetingRoom.CanvasWidth;
        surface.Height = MeetingRoom.CanvasHeight;
        surface.IsTabStop = true;
        surface.UseSystemFocusVisuals = false;
        _boardArrows = new Canvas { IsHitTestVisible = false, Width = MeetingRoom.CanvasWidth, Height = MeetingRoom.CanvasHeight };
        _boardElementsLayer = new Canvas { Width = MeetingRoom.CanvasWidth, Height = MeetingRoom.CanvasHeight };
        surface.Children.Add(_boardArrows);
        surface.Children.Add(_boardElementsLayer);
        surface.PointerPressed += OnBoardPointerPressed;
        surface.PointerMoved += OnBoardPointerMoved;
        surface.PointerReleased += OnBoardPointerReleased;
        surface.PointerCaptureLost += (_, _) => EndBoardGesture(commit: true);
        surface.KeyDown += OnBoardKeyDown;
    }

    private bool IsBoardBackground(object source) =>
        ReferenceEquals(source, _boardContent) || ReferenceEquals(source, _boardGridLines) || ReferenceEquals(source, _boardElementsLayer)
        || ReferenceEquals(source, _boardColumns);

    // ── Pointer: panning and element moves ───────────────────────────────

    private void OnBoardPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_boardContent);
        if (!point.Properties.IsLeftButtonPressed || _boardContent is null || _boardScroll is null) return;
        if (!IsBoardBackground(args.OriginalSource)) return;
        if (_arrowTool) { CancelArrowTool(); return; }
        SelectCanvasElement(null);
        _boardContent.Focus(FocusState.Pointer);
        _boardPanning = true;
        // Screen-relative start: the content moves under the pointer while panning.
        _panStart = args.GetCurrentPoint(_boardScroll).Position;
        _panOffset = (_boardScroll.HorizontalOffset, _boardScroll.VerticalOffset);
        _boardContent.CapturePointer(args.Pointer);
        ((BoardPanSurface)_boardContent).SetPanCursor(true);
        args.Handled = true;
    }

    private void OnBoardPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_boardScroll is null || _boardContent is null) return;
        if (_boardPanning)
        {
            var now = args.GetCurrentPoint(_boardScroll).Position;
            _boardScroll.ChangeView(_panOffset.H - (now.X - _panStart.X), _panOffset.V - (now.Y - _panStart.Y), null, true);
            args.Handled = true;
            return;
        }
        if (_canvasDrag is not { } drag || !_canvasViews.TryGetValue(drag.Id, out var view)) return;
        var position = args.GetCurrentPoint(_boardContent).Position;
        var dx = position.X - drag.Start.X;
        var dy = position.Y - drag.Start.Y;
        if (!drag.Moved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
        drag.Moved = true;
        if (drag.Resize)
        {
            var width = Math.Max(40, drag.Bounds.Width + dx);
            var height = drag.KeepAspect ? width * drag.Bounds.Height / Math.Max(1, drag.Bounds.Width) : Math.Max(30, drag.Bounds.Height + dy);
            drag.Current = new Rect(drag.Bounds.X, drag.Bounds.Y, width, height);
        }
        else
        {
            drag.Current = new Rect(Math.Clamp(drag.Bounds.X + dx, 0, MeetingRoom.CanvasWidth - 40),
                Math.Clamp(drag.Bounds.Y + dy, 0, MeetingRoom.CanvasHeight - 40), drag.Bounds.Width, drag.Bounds.Height);
        }
        PlaceCanvasView(view, drag.Current);
        RouteArrows();
        args.Handled = true;
    }

    private void OnBoardPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_boardPanning || _canvasDrag is not null)
        {
            EndBoardGesture(commit: true);
            _boardContent?.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        }
    }

    private void EndBoardGesture(bool commit)
    {
        if (_boardPanning)
        {
            _boardPanning = false;
            (_boardContent as BoardPanSurface)?.SetPanCursor(false);
        }
        if (_canvasDrag is { } drag)
        {
            _canvasDrag = null;
            if (commit && drag.Moved)
                Meetings.Send(new MeetingOp
                {
                    Type = MeetingOpTypes.ElementMove,
                    NoteId = drag.Id,
                    X = drag.Current.X,
                    Y = drag.Current.Y,
                    Width = drag.Current.Width,
                    Height = drag.Current.Height,
                });
        }
    }

    private void BeginCanvasDrag(CanvasElementView view, PointerRoutedEventArgs args, bool resize)
    {
        if (!Meetings.CanEdit || _boardContent is null || view.Element is not { } element) return;
        var point = args.GetCurrentPoint(_boardContent);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_arrowTool) { PickArrowEndpoint(element.Id); args.Handled = true; return; }
        SelectCanvasElement(element.Id);
        if (_editingElementId == element.Id) return;
        _canvasDrag = new CanvasDrag(element.Id, point.Position, new Rect(element.X, element.Y, element.Width, element.Height), resize, element.Kind == "image");
        _boardContent.CapturePointer(args.Pointer);
        _boardContent.Focus(FocusState.Pointer);
        args.Handled = true;
    }

    private void OnBoardKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_editingElementId is not null) return;
        var control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (args.Key == VirtualKey.Delete && _selectedElementId is { } selected && Meetings.CanEdit)
        {
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementDelete, NoteId = selected });
            _selectedElementId = null;
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape)
        {
            CancelArrowTool();
            SelectCanvasElement(null);
        }
        else if (control && args.Key == VirtualKey.V)
        {
            args.Handled = true;
            _ = PasteImageAsync();
        }
    }

    private void SelectCanvasElement(string? id)
    {
        if (_selectedElementId == id) return;
        if (_editingElementId is { } editing && editing != id) EndCanvasEdit(commit: true);
        _selectedElementId = id;
        foreach (var view in _canvasViews.Values) UpdateSelectionChrome(view);
        // Others see who is working on which element.
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.Presence, DocumentId = id is null ? null : "whiteboard", BlockId = id });
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void RenderCanvasElements(MeetingRoomState snapshot, bool canEdit)
    {
        if (_boardElementsLayer is null) return;
        var presence = Meetings.Room?.Presence() ?? new Dictionary<Guid, (string?, string?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in snapshot.Whiteboard.Elements.Where(e => e.Kind != "arrow").OrderBy(e => e.Z))
        {
            seen.Add(element.Id);
            if (!_canvasViews.TryGetValue(element.Id, out var view) || view.Kind != element.Kind)
            {
                if (view is not null) _boardElementsLayer.Children.Remove(view.Root);
                view = CreateCanvasView(element);
                _canvasViews[element.Id] = view;
                _boardElementsLayer.Children.Add(view.Root);
            }
            if (_canvasDrag?.Id != element.Id) UpdateCanvasView(view, element, canEdit);
            var editor = presence.FirstOrDefault(p => p.Key != Meetings.LocalParticipantId && p.Value.Item1 == "whiteboard" && p.Value.Item2 == element.Id).Key;
            var person = editor == Guid.Empty ? null : snapshot.Participants.FirstOrDefault(p => p.Id == editor);
            view.Presence = person;
            UpdateSelectionChrome(view);
        }
        foreach (var stale in _canvasViews.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _boardElementsLayer.Children.Remove(_canvasViews[stale].Root);
            _canvasViews.Remove(stale);
            if (_selectedElementId == stale) _selectedElementId = null;
        }
        _arrowElements = snapshot.Whiteboard.Elements.Where(e => e.Kind == "arrow").ToList();
        RouteArrows();
    }

    private List<WhiteboardElement> _arrowElements = [];

    private CanvasElementView CreateCanvasView(WhiteboardElement element)
    {
        var content = new Grid();
        var chrome = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(element.Kind == "text" ? 6 : 10),
            Margin = new Thickness(-5),
            IsHitTestVisible = false,
        };
        var handle = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -11, -11),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Colors.White),
            Visibility = Visibility.Collapsed,
        };
        var tag = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-4, -24, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(content);
        root.Children.Add(chrome);
        root.Children.Add(tag);
        root.Children.Add(handle);
        AutomationProperties.SetName(root, element.Kind switch
        {
            "table" => "Tabelle",
            "image" => "Bild",
            "shape" => "Form",
            _ => "Textfeld",
        });
        var view = new CanvasElementView(element.Id, element.Kind, root, content, chrome, handle, tag);
        root.PointerPressed += (_, args) => BeginCanvasDrag(view, args, resize: false);
        handle.PointerPressed += (_, args) => BeginCanvasDrag(view, args, resize: true);
        root.DoubleTapped += (_, args) =>
        {
            if (view.Kind is "text" or "table" or "shape") { args.Handled = true; BeginCanvasEdit(view); }
        };
        return view;
    }

    private static void PlaceCanvasView(CanvasElementView view, Rect bounds)
    {
        Canvas.SetLeft(view.Root, bounds.X);
        Canvas.SetTop(view.Root, bounds.Y);
        view.Root.Width = bounds.Width;
        view.Root.Height = bounds.Height;
        view.Bounds = bounds;
    }

    private void UpdateCanvasView(CanvasElementView view, WhiteboardElement element, bool canEdit)
    {
        var changed = view.Element is null || view.Element.UpdatedAt != element.UpdatedAt || view.Element.Text != element.Text
            || view.Element.ColorIndex != element.ColorIndex || view.Element.FontSize != element.FontSize;
        view.Element = element;
        PlaceCanvasView(view, new Rect(element.X, element.Y, element.Width, element.Height));
        Canvas.SetZIndex(view.Root, (int)Math.Min(element.Z, 100_000));
        view.Root.ContextFlyout = canEdit ? CanvasMenu(view, element) : null;
        if (!changed || _editingElementId == element.Id) return;
        view.Content.Children.Clear();
        switch (element.Kind)
        {
            case "text":
                view.Content.Children.Add(new TextBlock
                {
                    Text = element.Text.Length == 0 ? "Text eingeben …" : element.Text,
                    FontSize = element.FontSize,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = element.FontSize >= 28 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = element.Text.Length == 0 ? ThemeBrush("MissumMutedTextBrush", 150) : ThemeBrush("MissumTextBrush", 240),
                    Padding = new Thickness(4),
                });
                break;
            case "table":
                view.Content.Children.Add(TableGrid(element, editable: false, view));
                break;
            case "image":
                var image = new Image { Stretch = Stretch.UniformToFill };
                view.Content.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = image });
                _ = LoadCanvasImageAsync(image, element);
                break;
            case "shape":
                view.Content.Children.Add(ShapeVisual(element));
                if (element.Text.Length > 0)
                    view.Content.Children.Add(new TextBlock
                    {
                        Text = element.Text,
                        FontSize = element.FontSize,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(14),
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22)),
                    });
                break;
        }
    }

    private void UpdateSelectionChrome(CanvasElementView view)
    {
        var selected = _selectedElementId == view.Id;
        view.Chrome.BorderBrush = selected ? ThemeBrush("MissumAccentBrush", 170)
            : view.Presence is { } person ? ParticipantBrush(person.ColorIndex)
            : new SolidColorBrush(Colors.Transparent);
        view.Handle.Visibility = selected && Meetings.CanEdit && _editingElementId != view.Id ? Visibility.Visible : Visibility.Collapsed;
        view.Handle.Background = ThemeBrush("MissumAccentBrush", 170);
        if (view.Presence is { } other && !selected)
        {
            view.Tag.Background = ParticipantBrush(other.ColorIndex);
            view.Tag.Child = new TextBlock { Text = other.Name, FontSize = 11, Foreground = new SolidColorBrush(Colors.White) };
            view.Tag.Visibility = Visibility.Visible;
        }
        else view.Tag.Visibility = Visibility.Collapsed;
    }

    private static FrameworkElement ShapeVisual(WhiteboardElement element)
    {
        var fill = new SolidColorBrush(StickyColors[element.ColorIndex % StickyColors.Length]);
        var stroke = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
        return element.Shape switch
        {
            "ellipse" => new Ellipse { Fill = fill, Stroke = stroke, StrokeThickness = 1.5 },
            "diamond" => new Polygon
            {
                Points = { new Point(.5, 0), new Point(1, .5), new Point(.5, 1), new Point(0, .5) },
                Stretch = Stretch.Fill,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1.5,
            },
            _ => new Rectangle { Fill = fill, Stroke = stroke, StrokeThickness = 1.5, RadiusX = 12, RadiusY = 12 },
        };
    }

    private static Border TableGrid(WhiteboardElement element, bool editable, CanvasElementView view)
    {
        var rows = element.Rows ?? [["", ""], ["", ""]];
        var columns = rows.Max(r => r.Count);
        var grid = new Grid { Background = ThemeBrush("MissumLayerBrush", 32) };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        view.Cells = editable ? new TextBox[rows.Count, columns] : null;
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";
                FrameworkElement cell;
                if (editable)
                {
                    var box = PlainTextBox(14);
                    box.Text = text;
                    box.FontWeight = r == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                    view.Cells![r, c] = box;
                    cell = box;
                }
                else
                {
                    cell = new TextBlock
                    {
                        Text = text,
                        FontSize = 14,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(8, 5, 8, 5),
                        FontWeight = r == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    };
                }
                var border = new Border
                {
                    BorderBrush = ThemeBrush("MissumStrokeBrush", 60),
                    BorderThickness = new Thickness(c == 0 ? 0 : 1, r == 0 ? 0 : 1, 0, 0),
                    Background = r == 0 ? ThemeBrush("MissumLayerStrongBrush", 40) : null,
                    Child = cell,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 70),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    private static async Task LoadCanvasImageAsync(Image image, WhiteboardElement element)
    {
        if (string.IsNullOrEmpty(element.ImageData)) return;
        try
        {
            var bytes = Convert.FromBase64String(element.ImageData);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or System.Runtime.InteropServices.COMException) { _ = exception; }
    }

    // ── Editing ──────────────────────────────────────────────────────────

    private void BeginCanvasEdit(CanvasElementView view)
    {
        if (!Meetings.CanEdit || view.Element is not { } element) return;
        SelectCanvasElement(element.Id);
        _editingElementId = element.Id;
        view.Content.Children.Clear();
        if (element.Kind == "table")
        {
            view.Content.Children.Add(TableGrid(element, editable: true, view));
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => view.Cells?[0, 0]?.Focus(FocusState.Programmatic));
        }
        else
        {
            var box = PlainTextBox(element.FontSize, element.Kind == "shape" ? "Text in der Form" : "Text eingeben …");
            box.AcceptsReturn = true;
            box.Text = element.Text;
            box.TextAlignment = element.Kind == "shape" ? TextAlignment.Center : TextAlignment.Left;
            box.VerticalAlignment = element.Kind == "shape" ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            if (element.Kind == "shape")
            {
                view.Content.Children.Add(ShapeVisual(element));
                box.Foreground = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22));
                box.Resources["TextControlForegroundFocused"] = box.Foreground;
                box.Margin = new Thickness(10);
            }
            box.Background = element.Kind == "text" ? ThemeBrush("MissumLayerBrush", 32) : new SolidColorBrush(Colors.Transparent);
            box.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { args.Handled = true; EndCanvasEdit(commit: false); } };
            view.Content.Children.Add(box);
            view.Editor = box;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                box.Focus(FocusState.Programmatic);
                box.SelectionStart = box.Text.Length;
            });
        }
        UpdateSelectionChrome(view);
    }

    private void EndCanvasEdit(bool commit)
    {
        if (_editingElementId is not { } id || !_canvasViews.TryGetValue(id, out var view) || view.Element is not { } element)
        {
            _editingElementId = null;
            return;
        }
        _editingElementId = null;
        if (commit)
        {
            var updated = MeetingJson.Clone(element);
            updated.ImageData = null;
            if (element.Kind == "table" && view.Cells is { } cells)
            {
                updated.Rows = Enumerable.Range(0, cells.GetLength(0))
                    .Select(r => Enumerable.Range(0, cells.GetLength(1)).Select(c => cells[r, c]?.Text ?? "").ToList()).ToList();
            }
            else if (view.Editor is { } editor)
            {
                updated.Text = editor.Text.Replace("\r", "\n", StringComparison.Ordinal).Trim();
            }
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = updated });
        }
        view.Editor = null;
        view.Cells = null;
        view.Element = null;
        UpdateCanvasView(view, element, Meetings.CanEdit);
        UpdateSelectionChrome(view);
    }

    private MenuFlyout CanvasMenu(CanvasElementView view, WhiteboardElement element)
    {
        var menu = new MenuFlyout();
        void Item(string text, string glyph, Action action, MenuFlyoutSubItem? parent = null)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => RunUiCallback("Canvas.Menu", action);
            if (parent is null) menu.Items.Add(item); else parent.Items.Add(item);
        }
        void Update(Action<WhiteboardElement> change)
        {
            var copy = MeetingJson.Clone(element);
            copy.ImageData = null;
            change(copy);
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = copy });
        }

        if (element.Kind is "text" or "table" or "shape") Item("Bearbeiten", "", () => BeginCanvasEdit(view));
        if (element.Kind is "text" or "shape")
        {
            var size = new MenuFlyoutSubItem { Text = "Schriftgröße", Icon = new FontIcon { Glyph = "" } };
            foreach (var (label, value) in new[] { ("Klein", 14.0), ("Mittel", 18.0), ("Groß", 28.0), ("Titel", 40.0) })
                Item(label, "", () => Update(e => e.FontSize = value), size);
            menu.Items.Add(size);
        }
        if (element.Kind == "shape")
        {
            var form = new MenuFlyoutSubItem { Text = "Form", Icon = new FontIcon { Glyph = "" } };
            foreach (var (label, shape) in new[] { ("Rechteck", "rect"), ("Ellipse", "ellipse"), ("Raute", "diamond") })
                Item(label, "", () => Update(e => e.Shape = shape), form);
            menu.Items.Add(form);
            var color = new MenuFlyoutSubItem { Text = "Farbe", Icon = new FontIcon { Glyph = "" } };
            for (var index = 0; index < StickyColors.Length; index++)
            {
                var colorIndex = index;
                var item = new MenuFlyoutItem { Text = $"Farbe {index + 1}", Icon = new FontIcon { Glyph = "", Foreground = new SolidColorBrush(StickyColors[index]) } };
                item.Click += (_, _) => Update(e => e.ColorIndex = colorIndex);
                color.Items.Add(item);
            }
            menu.Items.Add(color);
        }
        if (element.Kind == "table")
        {
            var table = new MenuFlyoutSubItem { Text = "Tabelle", Icon = new FontIcon { Glyph = "" } };
            Item("Zeile hinzufügen", "", () => Update(e => { var cols = e.Rows![0].Count; e.Rows.Add(Enumerable.Repeat("", cols).ToList()); e.Height += 36; }), table);
            Item("Spalte hinzufügen", "", () => Update(e => { foreach (var row in e.Rows!) row.Add(""); e.Width += 110; }), table);
            Item("Letzte Zeile entfernen", "", () => Update(e => { if (e.Rows!.Count > 1) { e.Rows.RemoveAt(e.Rows.Count - 1); e.Height = Math.Max(60, e.Height - 36); } }), table);
            Item("Letzte Spalte entfernen", "", () => Update(e => { if (e.Rows![0].Count > 1) { foreach (var row in e.Rows) row.RemoveAt(row.Count - 1); e.Width = Math.Max(120, e.Width - 110); } }), table);
            menu.Items.Add(table);
        }
        Item("Pfeil von hier ziehen", "", () => { StartArrowTool(); PickArrowEndpoint(element.Id); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Duplizieren", "", () =>
        {
            var copy = MeetingJson.Clone(element);
            copy.Id = MeetingDocument.NewId();
            copy.X += 30;
            copy.Y += 30;
            copy.Z = 0;
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = copy });
        });
        Item("In den Vordergrund", "", () => Update(e => e.Z = (Meetings.Room?.Read(s => s.Whiteboard.Elements.Select(x => x.Z).DefaultIfEmpty(0).Max()) ?? 0) + 1));
        Item("In den Hintergrund", "", () => Update(e => e.Z = (Meetings.Room?.Read(s => s.Whiteboard.Elements.Select(x => x.Z).DefaultIfEmpty(0).Min()) ?? 0) - 1));
        Item("Löschen", "", () => Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementDelete, NoteId = element.Id }));
        return menu;
    }

    // ── Inserting ────────────────────────────────────────────────────────

    private Point ViewportCenter(double width, double height)
    {
        if (_boardScroll is null) return new Point(400, 300);
        var zoom = Math.Max(.1, _boardScroll.ZoomFactor);
        var x = (_boardScroll.HorizontalOffset + _boardScroll.ViewportWidth / 2) / zoom - width / 2;
        var y = (_boardScroll.VerticalOffset + _boardScroll.ViewportHeight / 2) / zoom - height / 2;
        // Small offsets keep repeated inserts from stacking exactly on top of each other.
        var jitter = (_canvasViews.Count % 6) * 18;
        return new Point(Math.Clamp(x + jitter, 0, MeetingRoom.CanvasWidth - width), Math.Clamp(y + jitter, 0, MeetingRoom.CanvasHeight - height));
    }

    private void AddCanvasElement(string kind, string? shape = null)
    {
        if (!Meetings.CanEdit) return;
        var (width, height) = kind switch
        {
            "table" => (330.0, 144.0),
            "shape" => (200.0, 130.0),
            _ => (280.0, 90.0),
        };
        var at = ViewportCenter(width, height);
        var element = new WhiteboardElement
        {
            Kind = kind,
            X = at.X,
            Y = at.Y,
            Width = width,
            Height = height,
            Shape = shape,
            FontSize = kind == "shape" ? 16 : 18,
            ColorIndex = kind == "shape" ? 1 : 0,
            Rows = kind == "table" ? [["Spalte 1", "Spalte 2", "Spalte 3"], ["", "", ""], ["", "", ""], ["", "", ""]] : null,
        };
        _pendingCanvasEdit = kind is "text" or "table" ? element.Id : null;
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = element });
        SelectCanvasElement(element.Id);
    }

    private string? _pendingCanvasEdit;

    private void OpenPendingCanvasEdit()
    {
        if (_pendingCanvasEdit is { } id && _canvasViews.TryGetValue(id, out var view))
        {
            _pendingCanvasEdit = null;
            BeginCanvasEdit(view);
        }
    }

    private async Task PickImageAsync()
    {
        if (!Meetings.CanEdit) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
            foreach (var type in new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }) picker.FileTypeFilter.Add(type);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            using var stream = await file.OpenReadAsync();
            await InsertImageAsync(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError("Das Bild konnte nicht eingefügt werden: " + exception.Message); }
    }

    private async Task PasteImageAsync()
    {
        if (!Meetings.CanEdit) return;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap)) return;
            var reference = await content.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            await InsertImageAsync(stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError("Das Bild aus der Zwischenablage konnte nicht eingefügt werden: " + exception.Message); }
    }

    /// <summary>Downscales to at most 1600 px, encodes as JPEG and places the picture in view.</summary>
    private async Task InsertImageAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var jpeg = await MeetingCameraService.EncodeJpegAsync(bitmap, 1600);
        if (Convert.ToBase64String(jpeg).Length > MeetingRoom.MaximumImageBase64Length)
            jpeg = await MeetingCameraService.EncodeJpegAsync(bitmap, 960);
        var data = Convert.ToBase64String(jpeg);
        if (data.Length > MeetingRoom.MaximumImageBase64Length)
        {
            ShowMeetingError("Das Bild ist zu groß für das Whiteboard.");
            return;
        }
        var width = 360.0;
        var height = Math.Max(60, width * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth));
        var at = ViewportCenter(width, height);
        var element = new WhiteboardElement { Kind = "image", X = at.X, Y = at.Y, Width = width, Height = height, ImageData = data };
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = element });
        SelectCanvasElement(element.Id);
    }

    // ── Arrows ───────────────────────────────────────────────────────────

    private void StartArrowTool()
    {
        if (!Meetings.CanEdit) return;
        SelectCanvasElement(null);
        _arrowTool = true;
        _arrowStartId = null;
        UpdateBoardHint();
    }

    private void CancelArrowTool()
    {
        if (!_arrowTool) return;
        _arrowTool = false;
        _arrowStartId = null;
        UpdateBoardHint();
    }

    /// <summary>First pick sets the source, the second one the target (elements or sticky notes).</summary>
    private void PickArrowEndpoint(string id)
    {
        if (!_arrowTool) return;
        if (_arrowStartId is null)
        {
            _arrowStartId = id;
            UpdateBoardHint();
            return;
        }
        if (_arrowStartId != id)
            Meetings.Send(new MeetingOp
            {
                Type = MeetingOpTypes.ElementUpsert,
                Element = new WhiteboardElement { Kind = "arrow", FromId = _arrowStartId, ToId = id, Width = 40, Height = 30, ColorIndex = 0 },
            });
        CancelArrowTool();
    }

    private Rect? EndpointBounds(string id)
    {
        if (_canvasViews.TryGetValue(id, out var view)) return view.Bounds;
        if (_stickyViews.TryGetValue(id, out var sticky) && sticky.Root.Parent is not null && _boardContent is not null && sticky.Root.ActualWidth > 0)
        {
            var origin = sticky.Root.TransformToVisual(_boardContent).TransformPoint(new Point(0, 0));
            return new Rect(origin.X, origin.Y, sticky.Root.ActualWidth, sticky.Root.ActualHeight);
        }
        return null;
    }

    private void RouteArrows()
    {
        if (_boardArrows is null) return;
        _boardArrows.Children.Clear();
        var brush = ThemeBrush("MissumTextBrush", 220);
        foreach (var arrow in _arrowElements)
        {
            if (arrow.FromId is null || arrow.ToId is null || EndpointBounds(arrow.FromId) is not { } from || EndpointBounds(arrow.ToId) is not { } to) continue;
            var start = EdgePoint(from, Center(to));
            var end = EdgePoint(to, Center(from));
            _boardArrows.Children.Add(new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = brush, StrokeThickness = 2.5, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round });
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            const double head = 13;
            _boardArrows.Children.Add(new Polygon
            {
                Fill = brush,
                Points =
                {
                    end,
                    new Point(end.X - head * Math.Cos(angle - .45), end.Y - head * Math.Sin(angle - .45)),
                    new Point(end.X - head * Math.Cos(angle + .45), end.Y - head * Math.Sin(angle + .45)),
                },
            });
        }
    }

    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    /// <summary>Where the line from the rectangle's center towards <paramref name="target"/> leaves the rectangle.</summary>
    internal static Point EdgePoint(Rect rect, Point target)
    {
        var center = Center(rect);
        var dx = target.X - center.X;
        var dy = target.Y - center.Y;
        if (Math.Abs(dx) < .001 && Math.Abs(dy) < .001) return center;
        var scale = Math.Min(Math.Abs(dx) < .001 ? double.MaxValue : rect.Width / 2 / Math.Abs(dx),
                             Math.Abs(dy) < .001 ? double.MaxValue : rect.Height / 2 / Math.Abs(dy));
        return new Point(center.X + dx * scale, center.Y + dy * scale);
    }

    private sealed class CanvasElementView(string id, string kind, Grid root, Grid content, Border chrome, Border handle, Border tag)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public Grid Root { get; } = root;
        public Grid Content { get; } = content;
        public Border Chrome { get; } = chrome;
        public Border Handle { get; } = handle;
        public Border Tag { get; } = tag;
        public WhiteboardElement? Element { get; set; }
        public Rect Bounds { get; set; }
        public TextBox? Editor { get; set; }
        public TextBox?[,]? Cells { get; set; }
        public MeetingParticipant? Presence { get; set; }
    }

    private sealed class CanvasDrag(string id, Point start, Rect bounds, bool resize, bool keepAspect)
    {
        public string Id { get; } = id;
        public Point Start { get; } = start;
        public Rect Bounds { get; } = bounds;
        public bool Resize { get; } = resize;
        public bool KeepAspect { get; } = keepAspect;
        public bool Moved { get; set; }
        public Rect Current { get; set; } = bounds;
    }
}

/// <summary>The whiteboard surface. It exists so the board can show a move cursor while panning.</summary>
internal sealed partial class BoardPanSurface : Grid
{
    public void SetPanCursor(bool panning) =>
        ProtectedCursor = panning ? InputSystemCursor.Create(InputSystemCursorShape.SizeAll) : null;
}
