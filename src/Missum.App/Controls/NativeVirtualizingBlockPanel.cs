using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Missum.App.Controls;

/// <summary>
/// A native block window. Only the viewport and two screens on either side own
/// text, image, formula and tool controls; measured spacers retain scroll geometry.
/// The logical rows, IDs and original source offsets survive recycling.
/// </summary>
internal sealed class NativeVirtualizingBlockPanel : StackPanel
{
    internal sealed record Row(string Key, object Version, double EstimatedHeight,
        Func<FrameworkElement> Create, Action<FrameworkElement>? Update = null, string? ReplacesKey = null, string? Kind = null);

    private readonly Border _before = new() { IsHitTestVisible = false };
    private readonly Border _after = new() { IsHitTestVisible = false };
    private readonly Dictionary<string, (object Version, string? Kind, FrameworkElement View)> _realized = new(StringComparer.Ordinal);
    private readonly Dictionary<FrameworkElement, string> _viewKeys = [];
    private readonly Dictionary<string, double> _heights = new(StringComparer.Ordinal);
    private IReadOnlyList<Row> _rows = [];
    private double[] _offsets = [0];
    private readonly Dictionary<string, int> _rowIndices = new(StringComparer.Ordinal);
    private bool _refreshQueued;
    private bool _refreshing;
    private double _width;
    internal Func<bool>? PinSelection { get; set; }
    internal Func<bool>? FollowLatest { get; set; }
    internal Action<string, FrameworkElement?>? RealizationChanged { get; set; }
    internal Action? EarlierRequested { get; set; }
    internal Action? LayoutChanged { get; set; }
    internal Action<Action>? Project { get; set; }
    internal ScrollViewer? ScrollOwner { get; set; }
    internal int LogicalCount => _rows.Count;
    internal int RealizedCount => _realized.Count;
    internal IReadOnlyCollection<FrameworkElement> RealizedViews => _realized.Values.Select(value => value.View).ToArray();

    internal NativeVirtualizingBlockPanel()
    {
        Spacing = 0;
        Loaded += (_, _) => QueueRefresh();
        SizeChanged += (_, args) =>
        {
            if (Math.Abs(_width - args.NewSize.Width) > 2)
            { _width = args.NewSize.Width; _heights.Clear(); Reindex(); QueueRefresh(); }
        };
        EffectiveViewportChanged += (_, _) => QueueRefresh();
    }

    internal void SetRows(IReadOnlyList<Row> rows)
    {
        string? anchor = null;
        double oldOffset = 0;
        if (_rows.Count > 0 && FollowLatest?.Invoke() != true && ScrollOwner is { } scroll && IsLoaded)
        {
            var top = ViewportTop(scroll);
            var index = FindRow(Math.Max(0, top));
            anchor = _rows[index].Key;
            oldOffset = _offsets[index];
        }
        var previousRows = _rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var nextKeys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (previousRows.TryGetValue(row.Key, out var previous) && previous.Kind != row.Kind) _heights.Remove(row.Key);
            if (_realized.ContainsKey(row.Key) || row.ReplacesKey is not { Length: > 0 } replaced
                || nextKeys.Contains(replaced) || row.Update is null || !_realized.Remove(replaced, out var existing)) continue;
            var sameKind = previousRows.TryGetValue(replaced, out var replacedRow) && replacedRow.Kind == row.Kind;
            if (_heights.Remove(replaced, out var height) && sameKind) _heights[row.Key] = height;
            else _heights.Remove(row.Key);
            RealizationChanged?.Invoke(replaced, null);
            _viewKeys[existing.View] = row.Key;
            row.Update(existing.View);
            _realized[row.Key] = (row.Version, row.Kind, existing.View);
            RealizationChanged?.Invoke(row.Key, existing.View);
            if (anchor == replaced) anchor = row.Key;
        }
        _rows = rows;
        _rowIndices.Clear();
        for (var index = 0; index < _rows.Count; index++) _rowIndices[_rows[index].Key] = index;
        Reindex();
        if (anchor is not null && ScrollOwner is { } owner)
        {
            var next = Enumerable.Range(0, _rows.Count).FirstOrDefault(index => _rows[index].Key == anchor, -1);
            if (next >= 0 && Math.Abs(_offsets[next] - oldOffset) > 1)
                owner.ChangeView(null, owner.VerticalOffset + _offsets[next] - oldOffset, null, true);
        }
        RefreshViewport();
    }

    private void Reindex()
    {
        _offsets = new double[_rows.Count + 1];
        for (var index = 0; index < _rows.Count; index++)
        {
            _offsets[index + 1] = _offsets[index] + _heights.GetValueOrDefault(_rows[index].Key,
                Math.Max(1, _rows[index].EstimatedHeight));
        }
    }

    internal double? BlockOffset(string key) => _rowIndices.TryGetValue(key, out var index) ? _offsets[index] : null;

    internal void QueueRefresh()
    {
        if (_refreshQueued || !IsLoaded) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _refreshQueued = false;
            if (IsLoaded)
            {
                if (Project is { } project) project(RefreshViewport);
                else RefreshViewport();
            }
        });
    }

    internal void RefreshViewport()
    {
        if (_refreshing || ScrollOwner is not { } scroll) return;
        _refreshing = true;
        try
        {
            var screen = Math.Max(300, scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight);
            double top;
            top = ViewportTop(scroll);
            // On first layout a newly selected session must start at its live tail.
            if (!IsLoaded) top = FollowLatest?.Invoke() == true ? Math.Max(0, _offsets[^1] - screen) : 0;
            var (first, last) = NativeBlockWindow.Select(_offsets, top, screen);
            var wanted = _rows.Skip(first).Take(last - first).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
            var pin = PinSelection?.Invoke() == true;
            if (pin)
            {
                // Selection owns its original native runs. Add newly reached
                // rows while dragging, but never recycle selected paragraphs.
                foreach (var key in _realized.Keys) wanted.Add(key);
                if (wanted.Count > 0)
                {
                    first = Math.Min(first, FindFirstWanted(wanted));
                    last = Math.Max(last, FindLastWanted(wanted) + 1);
                }
            }
            foreach (var key in _realized.Keys.Where(key => !wanted.Contains(key)).ToArray())
            {
                var view = _realized[key].View;
                Children.Remove(view);
                if (view is IDisposable disposable) disposable.Dispose();
                _realized.Remove(key);
                _viewKeys.Remove(view);
                RealizationChanged?.Invoke(key, null);
            }
            var desired = new List<FrameworkElement> { _before };
            _before.Height = first < _offsets.Length ? _offsets[first] : 0;
            for (var index = first; index < last; index++)
            {
                var row = _rows[index];
                if (!_realized.TryGetValue(row.Key, out var entry))
                {
                    var view = row.Create();
                    view.Margin = new Thickness(view.Margin.Left, view.Margin.Top, view.Margin.Right, view.Margin.Bottom + 12);
                    view.SizeChanged += (_, args) =>
                    {
                        if (_viewKeys.TryGetValue(view, out var key))
                            ObserveHeight(key, args.NewSize.Height + view.Margin.Top + view.Margin.Bottom);
                    };
                    _viewKeys[view] = row.Key;
                    entry = (row.Version, row.Kind, view);
                    _realized[row.Key] = entry;
                    RealizationChanged?.Invoke(row.Key, view);
                }
                else if (!Equals(entry.Version, row.Version) || entry.Kind != row.Kind)
                {
                    row.Update?.Invoke(entry.View);
                    _realized[row.Key] = (row.Version, row.Kind, entry.View);
                }
                desired.Add(entry.View);
            }
            _after.Height = Math.Max(0, _offsets[^1] - _offsets[last]);
            desired.Add(_after);
            var set = desired.ToHashSet();
            foreach (var old in Children.OfType<FrameworkElement>().Where(child => !set.Contains(child)).ToArray()) Children.Remove(old);
            for (var index = 0; index < desired.Count; index++)
            {
                if (index < Children.Count && ReferenceEquals(Children[index], desired[index])) continue;
                Children.Remove(desired[index]); Children.Insert(index, desired[index]);
            }
            if (first == 0 && last > first && top >= 0 && top < screen) EarlierRequested?.Invoke();
        }
        finally { _refreshing = false; }
    }

    private int FindFirstWanted(HashSet<string> keys)
    {
        for (var index = 0; index < _rows.Count; index++) if (keys.Contains(_rows[index].Key)) return index;
        return 0;
    }

    private int FindLastWanted(HashSet<string> keys)
    {
        for (var index = _rows.Count - 1; index >= 0; index--) if (keys.Contains(_rows[index].Key)) return index;
        return 0;
    }

    private int FindRow(double offset)
    {
        var index = Array.BinarySearch(_offsets, offset);
        return Math.Clamp(index >= 0 ? index : ~index - 1, 0, Math.Max(0, _rows.Count - 1));
    }

    private double ViewportTop(ScrollViewer scroll)
    {
        try { return -TransformToVisual(scroll).TransformPoint(new Point()).Y; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { return scroll.VerticalOffset; }
    }

    private void ObserveHeight(string key, double height)
    {
        if (!double.IsFinite(height) || height <= 0 || Math.Abs(_heights.GetValueOrDefault(key) - height) < 1) return;
        var row = Enumerable.Range(0, _rows.Count).FirstOrDefault(index => _rows[index].Key == key, -1);
        if (row < 0) return;
        var previous = _offsets[row + 1] - _offsets[row];
        var compensate = false;
        if (row >= 0 && FollowLatest?.Invoke() != true && ScrollOwner is { } scroll && IsLoaded)
        {
            var top = ViewportTop(scroll);
            compensate = _offsets[row + 1] <= top;
        }
        _heights[key] = height;
        Reindex();
        if (compensate && ScrollOwner is { } owner) owner.ChangeView(null, owner.VerticalOffset + height - previous, null, true);
        QueueRefresh();
        LayoutChanged?.Invoke();
    }
}
