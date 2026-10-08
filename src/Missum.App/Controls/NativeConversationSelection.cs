using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Missum.App.Controls;

/// <summary>Extends native text selection across rendered paragraphs and messages without flattening their layout.</summary>
internal sealed class NativeConversationSelection
{
    private readonly FrameworkElement _host;
    private readonly Panel _messages;
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly List<(Node Node, TextHighlighter Highlight)> _highlights = [];
    private readonly List<(Node Node, FlyoutBase? Context, FlyoutBase? Selection)> _menus = [];
    private List<Node> _nodes = [];
    private Node? _anchor;
    private int _anchorOffset;
    private Point _lastPoint;
    private Point _pressPoint;
    private bool _dragging;
    private bool _crossed;
    private string _selectedText = "";
    internal Func<FrameworkElement, Point, MenuFlyout?>? ReadFromMenuFactory { get; set; }
    internal Action<string, Action>? UiProjectionCallback { get; set; }
    internal string SelectedText => _selectedText;
    internal bool HasSelection => _selectedText.Length > 0;

    public NativeConversationSelection(FrameworkElement host, Panel messages, ScrollViewer scroll)
    {
        _host = host; _messages = messages; _scroll = scroll;
        host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
        host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
        host.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
        host.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(KeyDown), true);
        host.ContextRequested += ContextRequested;
        host.PointerCanceled += (_, _) => StopDrag();
        host.Unloaded += (_, _) => Clear();
        _autoScroll.Tick += (_, _) => OnAutoScrollTick();
    }

    private void OnAutoScrollTick()
    {
        if (!_dragging || !_crossed) return;
        if (UiProjectionCallback is { } projectionCallback)
            projectionCallback("ConversationSelection.AutoScroll.Tick", ProjectAutoScroll);
        else ProjectAutoScroll();
    }

    private void ProjectAutoScroll()
    {
        try
        {
            var local = _host.TransformToVisual(_scroll).TransformPoint(_lastPoint);
            var delta = local.Y < 28 ? -18 : local.Y > _scroll.ActualHeight - 28 ? 18 : 0;
            if (delta == 0) return;
            _scroll.ChangeView(null, Math.Clamp(_scroll.VerticalOffset + delta, 0, _scroll.ScrollableHeight), null, true);
            _scroll.UpdateLayout();
            _lastPoint = _scroll.TransformToVisual(_host).TransformPoint(local);
            Extend(_lastPoint);
        }
        catch
        {
            // End only this text-selection gesture. The page records the
            // original projection failure; no chat/model operation is touched.
            if (UiProjectionCallback is { } projectionCallback)
                projectionCallback("ConversationSelection.AutoScroll.StopDrag", StopDrag);
            else StopDrag();
            throw;
        }
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(_host).Properties.IsRightButtonPressed)
        {
            var node = Collect(_messages).FirstOrDefault(n => ReferenceEquals(n.View, e.OriginalSource) || IsDescendant(e.OriginalSource as DependencyObject, n.View));
            if (node is not null && ReadFromMenuFactory?.Invoke(node.View, e.GetCurrentPoint(node.View).Position) is { } menu)
            {
                if (HasSelection)
                {
                    var copy = new MenuFlyoutItem { Text = "Auswahl kopieren" };
                    copy.Click += (_, _) => Copy(); menu.Items.Insert(0, copy);
                }
                _menus.Add((node, node.View.ContextFlyout, node.SelectionFlyout));
                node.View.ContextFlyout = menu; node.SelectionFlyout = menu;
            }
            return;
        }
        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        Clear();
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        _nodes = Collect(_messages).ToList();
        _pressPoint = _lastPoint = e.GetCurrentPoint(_host).Position;
        _anchor = Hit(_lastPoint, false);
        if (_anchor is null) return;
        _anchorOffset = _anchor.OffsetAt(_host, _lastPoint);
        _dragging = true;
    }

    private static bool IsDescendant(DependencyObject? source, DependencyObject root)
    {
        while (source is not null) { if (source == root) return true; source = VisualTreeHelper.GetParent(source); }
        return false;
    }

    internal static string ReadableSuffix(IEnumerable<FrameworkElement> blocks, FrameworkElement target)
    {
        var nodes = blocks.SelectMany(Collect).ToList();
        var index = nodes.FindIndex(node => node.View == target);
        if (index < 0) return "";
        var result = new List<string>();
        for (var i = index; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var previousStart = node.View is TextBlock text ? text.SelectionStart : ((RichTextBlock)node.View).SelectionStart;
            var previousEnd = node.View is TextBlock textEnd ? textEnd.SelectionEnd : ((RichTextBlock)node.View).SelectionEnd;
            try
            {
                var start = node.Start;
                node.Select(start, node.End);
                var suffix = node.Selected.TrimEnd('\r', '\n');
                if (suffix.Length > 0) result.Add(suffix);
            }
            finally { node.Select(previousStart, previousEnd); }
        }
        return string.Join("\n\n", result);
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) { StopDrag(); return; }
        _lastPoint = e.GetCurrentPoint(_host).Position;
        var target = Hit(_lastPoint, true);
        if (target is null || _anchor is null) return;
        if (!_crossed && target.View == _anchor.View) return; // Retain native word selection and links within one block.
        if (Math.Abs(_lastPoint.Y - _pressPoint.Y) + Math.Abs(_lastPoint.X - _pressPoint.X) < 4) return;
        if (!_crossed)
        {
            _crossed = true;
            // Transfer capture only after crossing a text boundary.
            _anchor.View.ReleasePointerCaptures();
            _host.CapturePointer(e.Pointer);
            _autoScroll.Start();
        }
        Extend(_lastPoint);
        e.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging && _crossed) { Extend(e.GetCurrentPoint(_host).Position); e.Handled = true; }
        StopDrag();
    }

    private void StopDrag() { _dragging = false; _autoScroll.Stop(); _host.ReleasePointerCaptures(); }

    internal void Clear()
    {
        StopDrag(); ClearHighlights(); _nodes.Clear(); _anchor = null; _crossed = false; _selectedText = "";
    }

    private void ClearHighlights()
    {
        foreach (var (node, highlight) in _highlights) node.Highlighters.Remove(highlight);
        _highlights.Clear();
        foreach (var (node, context, selection) in Enumerable.Reverse(_menus)) { node.View.ContextFlyout = context; node.SelectionFlyout = selection; }
        _menus.Clear();
    }

    private void Extend(Point point)
    {
        var target = Hit(point, true);
        if (_anchor is null || target is null) return;
        try { SelectRange(_nodes, _anchor, _anchorOffset, target, target.OffsetAt(_host, point)); }
        catch (ArgumentException) { Clear(); } // Streaming may replace the text container during a drag.
        catch (System.Runtime.InteropServices.COMException) { Clear(); }
    }

    private void SelectRange(List<Node> nodes, Node anchor, int anchorOffset, Node target, int targetOffset)
    {
        ClearHighlights();
        var a = nodes.FindIndex(n => n.View == anchor.View);
        var b = nodes.FindIndex(n => n.View == target.View);
        if (a < 0 || b < 0) return;
        if (a > b || a == b && anchorOffset > targetOffset) { (a, b) = (b, a); (anchorOffset, targetOffset) = (targetOffset, anchorOffset); }
        var selected = new List<string>();
        for (var i = a; i <= b; i++)
        {
            var node = nodes[i];
            var start = i == a ? node.At(anchorOffset) : node.Start;
            var end = i == b ? node.At(targetOffset) : node.End;
            node.Select(node.Start, start);
            var prefixLength = node.Selected.Length;
            node.Select(start, end);
            var text = node.Selected;
            node.Select(node.Start, node.Start);
            if (text.Length == 0) continue;
            selected.Add(node.View is RichTextBlock ? text.TrimEnd('\r', '\n') : text);
            var highlight = new TextHighlighter
            {
                Background = Application.Current.Resources.TryGetValue("MissumAccentBrush", out var accent) && accent is Brush accentBrush
                    ? accentBrush : new SolidColorBrush(Windows.UI.Color.FromArgb(180, 111, 76, 175)),
                Foreground = NativeThemeBrushes.Resource("MissumAccentTextBrush", Microsoft.UI.Colors.White),
            };
            highlight.Ranges.Add(new TextRange { StartIndex = prefixLength, Length = text.Length });
            node.Highlighters.Add(highlight); _highlights.Add((node, highlight));
        }
        _selectedText = string.Join("\n\n", selected);
        foreach (var (node, _) in _highlights)
        {
            _menus.Add((node, node.View.ContextFlyout, node.SelectionFlyout));
            var menu = new MenuFlyout();
            var copy = new MenuFlyoutItem { Text = "Auswahl kopieren" };
            copy.Click += (_, _) => Copy(); menu.Items.Add(copy);
            node.View.ContextFlyout = menu; node.SelectionFlyout = menu;
        }
    }

    internal void SelectForSmoke(FrameworkElement first, int start, FrameworkElement last, int end)
    {
        _nodes = Collect(_messages).ToList();
        SelectRange(_nodes, _nodes.First(n => n.View == first), start, _nodes.First(n => n.View == last), end);
    }

    private void KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!HasSelection || IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (e.Key == VirtualKey.Escape) { Clear(); e.Handled = true; }
        else if (e.Key == VirtualKey.C && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0)
        { Copy(); e.Handled = true; }
    }

    private void ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!HasSelection || IsInteractive(args.OriginalSource as DependencyObject)) return;
        var menu = new MenuFlyout(); var copy = new MenuFlyoutItem { Text = "Auswahl kopieren" };
        copy.Click += (_, _) => Copy(); menu.Items.Add(copy);
        if (args.TryGetPosition(_host, out var point)) menu.ShowAt(_host, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(_host);
        args.Handled = true;
    }

    private void Copy() { var data = new DataPackage(); data.SetText(_selectedText); Clipboard.SetContent(data); }

    private Node? Hit(Point point, bool nearest)
    {
        Node? best = null; var distance = double.MaxValue;
        foreach (var node in _nodes)
        {
            var p = node.View.TransformToVisual(_host).TransformPoint(new Point());
            var rect = new Rect(p, new Size(node.View.ActualWidth, node.View.ActualHeight));
            if (rect.Contains(point)) return node;
            var d = point.Y < rect.Top ? rect.Top - point.Y : point.Y > rect.Bottom ? point.Y - rect.Bottom : 0;
            if (d < distance) { distance = d; best = node; }
        }
        return nearest ? best : null;
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox or Hyperlink or ScrollBar) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private static IEnumerable<Node> Collect(DependencyObject parent)
    {
        if (parent is FrameworkElement element && element.Visibility != Visibility.Visible) yield break;
        if (parent is ButtonBase) yield break;
        if (parent is TextBlock { IsTextSelectionEnabled: true } text) { yield return new Node(text); yield break; }
        if (parent is RichTextBlock { IsTextSelectionEnabled: true } rich) { yield return new Node(rich); yield break; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var node in Collect(VisualTreeHelper.GetChild(parent, i))) yield return node;
    }

    private sealed class Node(FrameworkElement view)
    {
        public FrameworkElement View => view;
        public TextPointer Start => view is TextBlock t ? t.ContentStart : ((RichTextBlock)view).ContentStart;
        public TextPointer End => view is TextBlock t ? t.ContentEnd : ((RichTextBlock)view).ContentEnd;
        public FlyoutBase? SelectionFlyout
        {
            get => view is TextBlock text ? text.SelectionFlyout : ((RichTextBlock)view).SelectionFlyout;
            set { if (view is TextBlock text) text.SelectionFlyout = value; else ((RichTextBlock)view).SelectionFlyout = value; }
        }
        public string Selected => view is TextBlock t ? t.SelectedText : ((RichTextBlock)view).SelectedText;
        public IList<TextHighlighter> Highlighters => view is TextBlock t ? t.TextHighlighters : ((RichTextBlock)view).TextHighlighters;
        public TextPointer At(int offset) => Start.GetPositionAtOffset(Math.Clamp(offset - Start.Offset, 0, End.Offset - Start.Offset), LogicalDirection.Forward) ?? End;
        public void Select(TextPointer start, TextPointer end) { if (view is TextBlock t) t.Select(start, end); else ((RichTextBlock)view).Select(start, end); }
        public int OffsetAt(UIElement host, Point point)
        {
            var local = host.TransformToVisual(view).TransformPoint(point);
            if (view is RichTextBlock rich) return rich.GetPositionFromPoint(local)?.Offset ?? End.Offset;
            // TextBlock has no GetPositionFromPoint; use its native character rectangles.
            var lo = Start.Offset; var hi = End.Offset;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                var rect = At(mid).GetCharacterRect(LogicalDirection.Forward);
                if (local.Y >= rect.Bottom || local.Y >= rect.Top && local.X > rect.Left + rect.Width / 2) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
