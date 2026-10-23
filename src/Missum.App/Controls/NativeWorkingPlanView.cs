using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Missum.App.Controls;

/// <summary>A retained, in-layout plan. Opening it reduces the chat viewport instead of covering messages.</summary>
public sealed class NativeWorkingPlanView : ContentControl
{
    private readonly Button _toggle;
    private readonly FontIcon _chevron;
    private readonly StackPanel _rows = new() { Spacing = 3, Margin = new(12, 9, 12, 11) };
    private readonly ScrollViewer _body;
    private readonly Dictionary<string, PlanRow> _items = new(StringComparer.Ordinal);
    private bool _expanded = true;
    private bool _runActive;

    public NativeWorkingPlanView()
    {
        CornerRadius = new(8);
        BorderThickness = new(1);
        BorderBrush = NativeThemeBrushes.Resource("MissumAccentSubtleBrush", 75);
        Background = NativeThemeBrushes.Resource("MissumLayerBrush", 27);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        _chevron = new() { Glyph = "\uE70E", FontSize = 11, Foreground = NativeThemeBrushes.ReadableAccent };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Arbeitsplan", FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = NativeThemeBrushes.ReadableAccent });
        Grid.SetColumn(_chevron, 1);
        header.Children.Add(_chevron);
        _toggle = new Button { Content = header, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(14, 8, 14, 8), BorderThickness = new(0),
            Background = NativeThemeBrushes.Resource("MissumAccentSubtleBrush", 50) };
        _toggle.Click += (_, _) => SetExpanded(!_expanded);
        _body = new ScrollViewer { Content = _rows, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 240, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var content = new Grid();
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.Children.Add(_toggle);
        Grid.SetRow(_body, 1);
        content.Children.Add(_body);
        Content = new Border { Child = content, CornerRadius = CornerRadius, BorderThickness = BorderThickness,
            BorderBrush = BorderBrush, Background = Background };
        Unloaded += (_, _) => UpdateAnimations(false);
        Loaded += (_, _) => UpdateAnimations(true);
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateAnimations(Visibility == Visibility.Visible));
        SetExpanded(true);
    }

    internal bool IsExpanded => _expanded;
    internal int StepCount => _items.Count;
    internal Button Toggle => _toggle;
    internal ScrollViewer Body => _body;
    internal IReadOnlyList<string> VisibleTitles => _rows.Children.OfType<Grid>()
        .Select(row => row.Children.OfType<TextBlock>().Single().Text).ToArray();

    internal void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron.Glyph = expanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(_toggle, expanded ? "Arbeitsplan einklappen" : "Arbeitsplan ausklappen");
        UpdateAnimations(Visibility == Visibility.Visible);
    }

    internal void SetMaximumBodyHeight(double availableHeight) => _body.MaxHeight = Math.Clamp(availableHeight * .32, 72, 300);

    internal void SetRunActive(bool active)
    {
        if (_runActive == active) return;
        _runActive = active;
        UpdateAnimations(Visibility == Visibility.Visible);
    }

    internal void Update(NativeWorkingPlanProjection.Snapshot plan)
    {
        var keep = plan.Steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _items.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            var removed = _items[id];
            removed.Ring.IsActive = false;
            _rows.Children.Remove(removed.Root);
            _items.Remove(id);
        }
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            if (!_items.TryGetValue(step.Id, out var row))
            {
                row = CreateRow();
                _items.Add(step.Id, row);
            }
            if (_rows.Children.IndexOf(row.Root) != index)
            {
                _rows.Children.Remove(row.Root);
                _rows.Children.Insert(index, row.Root);
            }
            if (row.Title.Text != step.Title) row.Title.Text = step.Title;
            row.Status = step.Status;
            row.Check.Visibility = step.Status == "completed" ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateAnimations(Visibility == Visibility.Visible);
    }

    private void UpdateAnimations(bool visible)
    {
        foreach (var row in _items.Values)
        {
            // A saved in-progress stage does not mean a model is still running.
            // Preserve its status and show a stationary outline until a real run resumes.
            var running = _runActive && row.Status == "in_progress";
            row.Pending.Visibility = row.Status != "completed" && !running ? Visibility.Visible : Visibility.Collapsed;
            row.Ring.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            row.Ring.IsActive = visible && IsLoaded && _expanded && running;
            // Status names remain available to assistive technology, never as suffixes in the plan text.
            AutomationProperties.SetName(row.Root, row.Title.Text + ", " + (row.Status switch
            {
                "completed" => "erledigt",
                "in_progress" => _runActive ? "in Bearbeitung" : "in Bearbeitung, Lauf angehalten",
                _ => "ausstehend",
            }));
        }
    }

    private static PlanRow CreateRow()
    {
        var root = new Grid { ColumnSpacing = 10, MinHeight = 23 };
        root.ColumnDefinitions.Add(new() { Width = new(16) });
        root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var icon = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 3, 0, 0) };
        var pending = new Ellipse { Width = 12, Height = 12, Stroke = NativeThemeBrushes.ReadableAccent, StrokeThickness = 1.25 };
        var check = new FontIcon { Glyph = "\uE73E", FontSize = 13, Foreground = NativeThemeBrushes.ReadableAccent };
        var ring = new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0,
            Foreground = NativeThemeBrushes.ReadableAccent, IsActive = false };
        icon.Children.Add(pending); icon.Children.Add(check); icon.Children.Add(ring);
        root.Children.Add(icon);
        var title = new TextBlock { FontFamily = new FontFamily("Cascadia Mono"), FontSize = 13, LineHeight = 23,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = NativeThemeBrushes.Text };
        Grid.SetColumn(title, 1);
        root.Children.Add(title);
        return new(root, title, pending, check, ring);
    }

    private sealed class PlanRow(Grid root, TextBlock title, Ellipse pending, FontIcon check, ProgressRing ring)
    {
        internal Grid Root { get; } = root;
        internal TextBlock Title { get; } = title;
        internal Ellipse Pending { get; } = pending;
        internal FontIcon Check { get; } = check;
        internal ProgressRing Ring { get; } = ring;
        internal string Status { get; set; } = "pending";
    }
}
