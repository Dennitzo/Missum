using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using System.Text;
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
    // The content moves beneath the mouse. Pointer positions must belong to
    // the fixed viewport, never to the scrolling ConversationContent.
    private Point _lastViewportPoint;
    private Point _pressViewportPoint;
    private bool _dragging;
    private bool _crossed;
    private bool _projectingRange;
    private bool _transferringCapture;
    private bool _autoScrollRequested;
    private bool _manualScroll;
    private bool _handlingScroll;
    private uint? _capturedPointerId;
    private (FrameworkElement? First, int Start, FrameworkElement? Last, int End) _lastRange;
    internal bool IsDragging => _dragging;
    internal bool IsCrossBlockDragging => _dragging && _crossed;
    internal long RangeProjectionCount { get; private set; }
    private string _selectedText = "";
    private readonly ConditionalWeakTable<FrameworkElement, MenuFlyout> _nativeMenus = new();
    internal Func<FrameworkElement, Point, MenuFlyout?>? ReadFromMenuFactory { get; set; }
    internal Action<string, Action>? UiProjectionCallback { get; set; }
    internal Action? SelectionChangedCallback { get; set; }
    internal Action<string>? CopyFailed { get; set; }
    internal Func<string, Task<bool>>? CopySmokeAdapter { get; set; }
    internal Action<string, string, Point>? PointerSmokeObserver { get; set; }
    internal string SelectedText => ResolveSelectedText();
    internal bool HasSelection => SelectedText.Length > 0;
    internal bool IsSelecting => _dragging || HasSelection;

    public NativeConversationSelection(FrameworkElement host, Panel messages, ScrollViewer scroll)
    {
        _host = host; _messages = messages; _scroll = scroll;
        host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
        host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
        host.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
        host.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(CaptureLost), true);
        host.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(WheelChanged), true);
        // Handle copy before TextBlock's built-in handler reaches Clipboard.SetContent.
        host.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(KeyDown), true);
        host.ContextRequested += ContextRequested;
        host.PointerCanceled += (_, args) => CaptureLost(host, args);
        host.Unloaded += (_, _) => GuardSelection("Unloaded", Clear);
        host.BringIntoViewRequested += (_, args) => { if (_projectingRange) args.Handled = true; };
        scroll.ViewChanged += (_, args) => GuardSelection("Scroll.ViewChanged", () => ScrollChanged(args));
        _autoScroll.Tick += (_, _) => OnAutoScrollTick();
    }

    private void OnAutoScrollTick()
    {
        if (!_dragging || !_crossed) return;
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftButton) & CoreVirtualKeyStates.Down) == 0)
        { PointerSmokeObserver?.Invoke("auto.stop.key-up", "", _lastViewportPoint); StopDrag(); return; }
        if (UiProjectionCallback is { } projectionCallback)
            projectionCallback("ConversationSelection.AutoScroll.Tick", ProjectAutoScroll);
        else ProjectAutoScroll();
    }

    private void ProjectAutoScroll()
    {
        try
        {
            if (_manualScroll || _autoScrollRequested) return;
            var local = _lastViewportPoint;
            var delta = local.Y < 28 ? -18 : local.Y > _scroll.ActualHeight - 28 ? 18 : 0;
            if (delta == 0) return;
            var offset = Math.Clamp(_scroll.VerticalOffset + delta, 0, _scroll.ScrollableHeight);
            if (Math.Abs(offset - _scroll.VerticalOffset) < .1) return;
            _autoScrollRequested = true;
            if (!_scroll.ChangeView(null, offset, null, true)) _autoScrollRequested = false;
            // ChangeView is asynchronous. Hit-test only after ViewChanged has
            // applied the new transform, keeping the mouse at the same viewport pixel.
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

    private Point ContentPoint(Point viewportPoint) => _scroll.TransformToVisual(_host).TransformPoint(viewportPoint);

    private void ScrollChanged(ScrollViewerViewChangedEventArgs args)
    {
        if (!_dragging || !_crossed || _handlingScroll || !_autoScrollRequested) return;
        _handlingScroll = true;
        try { Extend(ContentPoint(_lastViewportPoint)); }
        finally { _handlingScroll = false; if (!args.IsIntermediate) _autoScrollRequested = false; }
    }

    private void WheelChanged(object sender, PointerRoutedEventArgs e) => GuardSelection("PointerWheelChanged", () =>
    {
        PointerSmokeObserver?.Invoke("wheel:left=" + e.GetCurrentPoint(_scroll).Properties.IsLeftButtonPressed, e.OriginalSource?.GetType().Name ?? "", e.GetCurrentPoint(_scroll).Position);
        if (!_dragging) return;
        if (!e.GetCurrentPoint(_scroll).Properties.IsLeftButtonPressed) { StopDrag(); return; }
        _lastViewportPoint = e.GetCurrentPoint(_scroll).Position;
        _manualScroll = true; _autoScrollRequested = false;
        if (!_crossed && _anchor is { } anchor && SafeSelected(anchor).Length > 0)
        {
            var start = anchor.View is TextBlock text ? text.SelectionStart.Offset : ((RichTextBlock)anchor.View).SelectionStart.Offset;
            var end = anchor.View is TextBlock textEnd ? textEnd.SelectionEnd.Offset : ((RichTextBlock)anchor.View).SelectionEnd.Offset;
            var forward = Math.Abs(_anchorOffset - start) <= Math.Abs(_anchorOffset - end);
            _anchorOffset = forward ? start : end;
            var target = forward ? end : start;
            // Native TextBlock capture re-hit-tests beneath a stationary mouse
            // after scrolling. Retain the exact character range in our stable
            // highlight projection before the ScrollViewer handles this wheel.
            if (CaptureSelection(e)) SelectRange(_nodes, anchor, _anchorOffset, anchor, target);
        }
        // Scrolling alone must not reinterpret a content-coordinate mouse point
        // as a new range endpoint. The next physical mouse move resumes dragging.
    });

    private void CaptureLost(object sender, PointerRoutedEventArgs e) => GuardSelection("PointerCaptureLost", () =>
    {
        PointerSmokeObserver?.Invoke($"captureLost:transfer={_transferringCapture};cross={_crossed};drag={_dragging};own={_capturedPointerId == e.Pointer.PointerId}", e.OriginalSource?.GetType().Name ?? "", _lastViewportPoint);
        if (_transferringCapture || !_dragging) return;
        if (_crossed && _capturedPointerId == e.Pointer.PointerId && ReferenceEquals(e.OriginalSource, _host)
            || !_crossed && ReferenceEquals(e.OriginalSource, _anchor?.View)) StopDrag();
    });

    private bool CaptureSelection(PointerRoutedEventArgs e)
    {
        _transferringCapture = true;
        bool captured;
        try
        {
            captured = _host.CapturePointer(e.Pointer);
            if (!captured && _anchor is not null)
            {
                _anchor.View.ReleasePointerCaptures();
                captured = _host.CapturePointer(e.Pointer);
            }
        }
        finally { _transferringCapture = false; }
        if (!captured) { StopDrag(); return false; }
        _capturedPointerId = e.Pointer.PointerId; _crossed = true;
        _autoScroll.Start();
        return true;
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
        => GuardSelection("PointerPressed", () => PressedCore(e));

    private void PressedCore(PointerRoutedEventArgs e)
    {
        PointerSmokeObserver?.Invoke("pressed", e.OriginalSource?.GetType().Name ?? "", e.GetCurrentPoint(_scroll).Position);
        if (e.GetCurrentPoint(_host).Properties.IsRightButtonPressed)
        {
            var node = Collect(_messages).FirstOrDefault(n => ReferenceEquals(n.View, e.OriginalSource) || IsDescendant(e.OriginalSource as DependencyObject, n.View));
            if (node is not null && ReadFromMenuFactory?.Invoke(node.View, e.GetCurrentPoint(node.View).Position) is { } menu)
            {
                if (HasSelection)
                {
                    var copy = new MenuFlyoutItem { Text = "Auswahl kopieren" };
                    copy.Click += async (_, _) => await CopySelectionAsync(); menu.Items.Insert(0, copy);
                }
                _menus.Add((node, node.View.ContextFlyout, node.SelectionFlyout));
                node.View.ContextFlyout = menu; node.SelectionFlyout = menu;
            }
            return;
        }
        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var active = Collect(_messages).FirstOrDefault(n => IsDescendant(e.OriginalSource as DependencyObject, n.View));
        Clear(active?.View); // Never collapse the range the native pointer handler has just started.
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        _nodes = Collect(_messages).ToList();
        foreach (var node in _nodes) WatchNativeSelection(node);
        _pressViewportPoint = _lastViewportPoint = e.GetCurrentPoint(_scroll).Position;
        var contentPoint = ContentPoint(_lastViewportPoint);
        _anchor = Hit(contentPoint, false);
        if (_anchor is null) return;
        _anchorOffset = _anchor.OffsetAt(_host, contentPoint);
        _dragging = true;
        SelectionChangedCallback?.Invoke();
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
            // Reading a paragraph must not temporarily select it: WinUI may expand
            // the user's character range when text pointers are restored afterwards.
            var suffix = ReadNodeText(node.View).TrimEnd('\r', '\n');
            if (suffix.Length > 0) result.Add(suffix);
        }
        return string.Join("\n\n", result);
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
        => GuardSelection("PointerMoved", () => MovedCore(e));

    private void MovedCore(PointerRoutedEventArgs e)
    {
        PointerSmokeObserver?.Invoke("moved", e.OriginalSource?.GetType().Name ?? "", e.GetCurrentPoint(_scroll).Position);
        if (!_dragging) return;
        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) { StopDrag(); return; }
        var viewportPoint = e.GetCurrentPoint(_scroll).Position;
        if (Math.Abs(viewportPoint.X - _lastViewportPoint.X) + Math.Abs(viewportPoint.Y - _lastViewportPoint.Y) < .5) return;
        _lastViewportPoint = viewportPoint; _manualScroll = false;
        var contentPoint = ContentPoint(viewportPoint);
        var target = Hit(contentPoint, true);
        if (target is null || _anchor is null) return;
        if (!_crossed && target.View == _anchor.View) return; // Retain native word selection and links within one block.
        if (Math.Abs(viewportPoint.Y - _pressViewportPoint.Y) + Math.Abs(viewportPoint.X - _pressViewportPoint.X) < 4) return;
        if (!_crossed)
        {
            // Use the native range's exact anchor when handing a live single-
            // paragraph selection to our projection across paragraph boundaries.
            var start = _anchor.View is TextBlock text ? text.SelectionStart.Offset : ((RichTextBlock)_anchor.View).SelectionStart.Offset;
            var end = _anchor.View is TextBlock textEnd ? textEnd.SelectionEnd.Offset : ((RichTextBlock)_anchor.View).SelectionEnd.Offset;
            if (start != end) _anchorOffset = Math.Abs(_anchorOffset - start) <= Math.Abs(_anchorOffset - end) ? start : end;
            // CapturePointer transfers ownership itself. Explicitly releasing
            // the child first can end the native gesture and lose its anchor.
            if (!CaptureSelection(e)) return;
        }
        Extend(contentPoint);
        e.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs e)
    {
        GuardSelection("PointerReleased", () =>
        {
            PointerSmokeObserver?.Invoke("released", e.OriginalSource?.GetType().Name ?? "", e.GetCurrentPoint(_scroll).Position);
            if (_dragging && _crossed)
            {
                var point = e.GetCurrentPoint(_scroll).Position;
                if (Math.Abs(point.X - _lastViewportPoint.X) + Math.Abs(point.Y - _lastViewportPoint.Y) >= .5) Extend(ContentPoint(point));
                e.Handled = true;
            }
            StopDrag(); SelectionChangedCallback?.Invoke();
        });
    }

    private void StopDrag()
    {
        var wasDragging = _dragging;
        _dragging = false; _capturedPointerId = null; _autoScrollRequested = false; _manualScroll = false;
        _autoScroll.Stop(); _host.ReleasePointerCaptures();
        if (wasDragging) SelectionChangedCallback?.Invoke();
    }

    internal void Clear() => Clear(null);

    private void Clear(FrameworkElement? keepNativeSelection)
    {
        StopDrag(); ClearHighlights(); _nodes.Clear(); _anchor = null; _crossed = false; _selectedText = "";
        _lastRange = default;
        foreach (var node in Collect(_messages))
            if (node.View != keepNativeSelection && SafeSelected(node).Length > 0)
                TryCleanup(() => node.Select(node.Start, node.Start));
        SelectionChangedCallback?.Invoke();
    }

    private void ClearHighlights()
    {
        foreach (var (node, highlight) in _highlights) TryCleanup(() => node.Highlighters.Remove(highlight));
        _highlights.Clear();
        foreach (var (node, context, selection) in Enumerable.Reverse(_menus)) TryCleanup(() => { node.View.ContextFlyout = context; node.SelectionFlyout = selection; });
        _menus.Clear();
    }

    private void Extend(Point point)
    {
        GuardSelection("Extend", () =>
        {
            var target = Hit(point, true);
            if (_anchor is null || target is null) return;
            SelectRange(_nodes, _anchor, _anchorOffset, target, target.OffsetAt(_host, point));
        });
    }

    private void SelectRange(List<Node> nodes, Node anchor, int anchorOffset, Node target, int targetOffset)
    {
        if (_selectedText.Length > 0 && _lastRange == (anchor.View, anchorOffset, target.View, targetOffset)) return;
        _projectingRange = true;
        try
        {
            SelectRangeCore(nodes, anchor, anchorOffset, target, targetOffset);
            RangeProjectionCount++;
            _lastRange = (anchor.View, anchorOffset, target.View, targetOffset);
        }
        finally { _projectingRange = false; }
    }

    private void SelectRangeCore(List<Node> nodes, Node anchor, int anchorOffset, Node target, int targetOffset)
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
            copy.Click += async (_, _) => await CopySelectionAsync(); menu.Items.Add(copy);
            node.View.ContextFlyout = menu; node.SelectionFlyout = menu;
        }
    }

    internal void SelectForSmoke(FrameworkElement first, int start, FrameworkElement last, int end)
    {
        _nodes = Collect(_messages).ToList();
        SelectRange(_nodes, _nodes.First(n => n.View == first), start, _nodes.First(n => n.View == last), end);
    }

    private void KeyDown(object sender, KeyRoutedEventArgs e) => GuardSelection("PreviewKeyDown", () => KeyDownCore(e));

    private void KeyDownCore(KeyRoutedEventArgs e)
    {
        if (!HasSelection || IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (e.Key == VirtualKey.Escape) { Clear(); e.Handled = true; }
        else if (e.Key == VirtualKey.C && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0)
        { e.Handled = true; _ = CopySelectionAsync(); }
    }

    private void ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        GuardSelection("ContextRequested", () =>
        {
            if (!HasSelection || IsInteractive(args.OriginalSource as DependencyObject)) return;
            var node = Collect(_messages).FirstOrDefault(n => IsDescendant(args.OriginalSource as DependencyObject, n.View));
            var menu = node is not null && args.TryGetPosition(node.View, out var local)
                ? ReadFromMenuFactory?.Invoke(node.View, local) ?? new MenuFlyout() : new MenuFlyout();
            var copy = new MenuFlyoutItem { Text = "Auswahl kopieren" };
            copy.Click += async (_, _) => await CopySelectionAsync(); menu.Items.Insert(0, copy);
            if (args.TryGetPosition(_host, out var point)) menu.ShowAt(_host, new FlyoutShowOptions { Position = point });
            else menu.ShowAt(_host);
            args.Handled = true;
        });
    }

    internal async Task<bool> CopySelectionAsync()
    {
        try
        {
            var text = SelectedText; // Keep exactly the user's range while retrying or while the model streams.
            if (text.Length == 0) return false;
            var copied = CopySmokeAdapter is { } adapter && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY"))
                ? await adapter(text) : await NativeClipboard.WriteTextAsync(text);
            if (!copied) GuardSelection("CopyFailed", () => CopyFailed?.Invoke(NativeClipboard.UnavailableMessage));
            return copied;
        }
        catch (Exception exception) when (IsSelectionException(exception))
        {
            GuardSelection("CopyFailed", () => CopyFailed?.Invoke(NativeClipboard.UnavailableMessage));
            return false;
        }
    }

    internal bool PreserveSelectionWithin(FrameworkElement container)
    {
        if (_dragging && _anchor is not null && IsDescendant(_anchor.View, container)) return true;
        if (_selectedText.Length > 0 && _highlights.Any(item => IsDescendant(item.Node.View, container))) return true;
        foreach (var node in Collect(container))
        {
            WatchNativeSelection(node);
            if (SafeSelected(node).Length > 0) return true;
        }
        return false;
    }

    private string ResolveSelectedText()
    {
        if (_anchor is not null && IsDescendant(_anchor.View, _messages) && SafeSelected(_anchor) is { Length: > 0 } active) return active;
        return Collect(_messages).Select(SafeSelected).FirstOrDefault(text => text.Length > 0) ?? _selectedText;
    }

    private void WatchNativeSelection(Node node)
    {
        if (_nativeMenus.TryGetValue(node.View, out _)) return;
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Auswahl kopieren" };
        copy.Click += async (_, _) => await CopySelectionAsync(); menu.Items.Add(copy);
        node.SelectionFlyout = menu;
        if (node.View is TextBlock text) text.SelectionChanged += (_, _) => OnNativeSelectionChanged(node);
        else ((RichTextBlock)node.View).SelectionChanged += (_, _) => OnNativeSelectionChanged(node);
        _nativeMenus.Add(node.View, menu);
    }

    private void OnNativeSelectionChanged(Node node) => GuardSelection("SelectionChanged", () =>
    {
        if (!_projectingRange && _selectedText.Length > 0 && SafeSelected(node).Length > 0)
        {
            ClearHighlights(); _selectedText = ""; _crossed = false; _anchor = node;
        }
        SelectionChangedCallback?.Invoke();
    });

    private void GuardSelection(string phase, Action action)
    {
        try { action(); }
        catch (Exception exception) when (IsSelectionException(exception))
        {
            // A detached streaming block ends only the gesture, never the AI run.
            TryCleanup(Clear);
            UiProjectionCallback?.Invoke("ConversationSelection." + phase, () => throw exception);
        }
    }

    private static bool IsSelectionException(Exception exception) => exception is System.Runtime.InteropServices.COMException
        or InvalidOperationException or ArgumentException && exception.HResult != unchecked((int)0x8007000E);
    private static void TryCleanup(Action action) { try { action(); } catch (Exception exception) when (IsSelectionException(exception)) { } }
    private static string SafeSelected(Node node) { try { return node.Selected; } catch (Exception exception) when (IsSelectionException(exception)) { return ""; } }

    private static string ReadNodeText(FrameworkElement view)
    {
        var builder = new StringBuilder();
        static void Append(StringBuilder builder, IEnumerable<Inline> inlines)
        {
            foreach (var inline in inlines)
                switch (inline)
                {
                    case Run run: builder.Append(run.Text); break;
                    case LineBreak: builder.Append('\n'); break;
                    case Span span: Append(builder, span.Inlines); break;
                    case InlineUIContainer { Child: NativeFormulaView formula }: builder.Append(formula.Source); break;
                }
        }
        if (view is TextBlock text)
        {
            if (text.Inlines.Count == 0) return text.Text;
            Append(builder, text.Inlines);
        }
        else foreach (var paragraph in ((RichTextBlock)view).Blocks.OfType<Paragraph>())
        {
            if (builder.Length > 0) builder.Append('\n');
            Append(builder, paragraph.Inlines);
        }
        return builder.ToString();
    }

    private Node? Hit(Point point, bool nearest)
    {
        Node? best = null; var distance = double.MaxValue;
        foreach (var node in _nodes)
        {
            if (!IsDescendant(node.View, _messages)) continue;
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
            // WinUI exposes zero-width caret rectangles for TextBlock positions.
            // Choosing the first position to the right rounds every hit up by
            // one character. Compare both adjacent insertion positions instead.
            if (lo > Start.Offset)
            {
                var current = At(lo).GetCharacterRect(LogicalDirection.Forward);
                var previous = At(lo - 1).GetCharacterRect(LogicalDirection.Forward);
                if (current.Width < .01 && previous.Width < .01
                    && Math.Abs(current.Top - previous.Top) < .5
                    && Math.Abs(local.X - previous.Left) < Math.Abs(local.X - current.Left)) lo--;
            }
            return lo;
        }
    }
}
