using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Missum.App.Controls;

/// <summary>Retained, full-height content for the working-plan tab; the tab owns scrolling.</summary>
public sealed class NativeWorkingPlanView : ContentControl
{
    private readonly StackPanel _rows = new() { Spacing = 5, Margin = new(14, 11, 14, 14) };
    private readonly StackPanel _criteriaRows = new() { Spacing = 5, Margin = new(14, 11, 14, 14) };
    private readonly Dictionary<string, PlanRow> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlanRow> _criteria = new(StringComparer.Ordinal);
    private readonly Border _stepsSection;
    private readonly Border _criteriaSection;
    private readonly TextBlock _empty = new()
    {
        Text = "Noch kein Arbeitsplan vorhanden.", TextWrapping = TextWrapping.Wrap,
        Foreground = NativeThemeBrushes.Text, Margin = new(14), FontSize = 13,
    };
    private bool _runActive;
    private bool _viewActive = true;

    public NativeWorkingPlanView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        IsTabStop = false;
        _stepsSection = CreateSection("Arbeitsschritte", _rows);
        _criteriaSection = CreateSection("Annahmekriterien", _criteriaRows);
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(_empty);
        content.Children.Add(_stepsSection);
        content.Children.Add(_criteriaSection);
        Content = content;
        Unloaded += (_, _) => StopAnimations();
        Loaded += (_, _) => UpdateAnimations();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateAnimations());
        Update(NativeWorkingPlanProjection.Snapshot.Empty);
    }

    internal int StepCount => _items.Count;
    internal int AcceptanceCriteriaCount => _criteria.Count;
    internal IReadOnlyList<string> VisibleTitles => _rows.Children.OfType<Grid>()
        .Select(row => row.Children.OfType<TextBlock>().Single().Text).ToArray();

    internal void SetViewActive(bool active)
    {
        if (_viewActive == active) return;
        _viewActive = active;
        UpdateAnimations();
    }

    internal void SetRunActive(bool active)
    {
        if (_runActive == active) return;
        _runActive = active;
        UpdateAnimations();
    }

    internal void Update(NativeWorkingPlanProjection.Snapshot plan)
    {
        UpdateRows(plan.Steps, _items, _rows);
        UpdateRows(plan.AcceptanceCriteria, _criteria, _criteriaRows);
        _empty.Visibility = _items.Count + _criteria.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _stepsSection.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _criteriaSection.Visibility = _criteria.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAnimations();
    }

    private void UpdateRows(IReadOnlyList<NativeWorkingPlanProjection.Item> steps,
        Dictionary<string, PlanRow> items, StackPanel rows)
    {
        var keep = steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in items.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            var removed = items[id];
            removed.Ring.IsActive = false;
            rows.Children.Remove(removed.Root);
            items.Remove(id);
        }
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (!items.TryGetValue(step.Id, out var row))
            {
                row = CreateRow();
                items.Add(step.Id, row);
            }
            if (rows.Children.IndexOf(row.Root) != index)
            {
                rows.Children.Remove(row.Root);
                rows.Children.Insert(index, row.Root);
            }
            if (row.Title.Text != step.Title) row.Title.Text = step.Title;
            row.Status = step.Status;
            row.Check.Visibility = step.Status == "completed" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void StopAnimations()
    {
        foreach (var row in _items.Values.Concat(_criteria.Values)) row.Ring.IsActive = false;
    }

    private void UpdateAnimations()
    {
        var visible = _viewActive && IsLoaded && IsVisibleInTree();
        foreach (var row in _items.Values.Concat(_criteria.Values))
            UpdateAnimation(row, visible);
    }

    private void UpdateAnimation(PlanRow row, bool visible)
    {
        // A saved in-progress stage does not mean a model is still running.
        // Preserve its status and show a stationary outline until a real run resumes.
        var running = _runActive && row.Status == "in_progress";
        row.Pending.Visibility = row.Status != "completed" && !running ? Visibility.Visible : Visibility.Collapsed;
        row.Ring.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        row.Ring.IsActive = visible && row.InViewport && running;
        // Status names remain available to assistive technology, never as suffixes in the plan text.
        AutomationProperties.SetName(row.Root, row.Title.Text + ", " + (row.Status switch
        {
            "completed" => "erledigt",
            "in_progress" => _runActive ? "in Bearbeitung" : "in Bearbeitung, Lauf angehalten",
            _ => "ausstehend",
        }));
    }

    private bool IsVisibleInTree()
    {
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static Border CreateSection(string title, StackPanel rows)
    {
        var header = new Border
        {
            Padding = new(14, 9, 14, 9),
            Background = NativeThemeBrushes.Resource("MissumAccentSubtleBrush", 50),
            Child = new TextBlock { Text = title, FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = NativeThemeBrushes.ReadableAccent },
        };
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(rows);
        return new Border { Child = content, CornerRadius = new(8), BorderThickness = new(1),
            BorderBrush = NativeThemeBrushes.Resource("MissumAccentSubtleBrush", 75),
            Background = NativeThemeBrushes.Resource("MissumLayerBrush", 27) };
    }

    private PlanRow CreateRow()
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
        var row = new PlanRow(root, title, pending, check, ring);
        root.EffectiveViewportChanged += (_, args) =>
        {
            var viewport = args.EffectiveViewport;
            row.InViewport = viewport.Width > 0 && viewport.Height > 0
                && viewport.Right > 0 && viewport.Bottom > 0
                && viewport.X < root.ActualWidth && viewport.Y < root.ActualHeight;
            UpdateAnimation(row, _viewActive && IsLoaded && IsVisibleInTree());
        };
        root.Unloaded += (_, _) => ring.IsActive = false;
        root.Loaded += (_, _) => UpdateAnimation(row, _viewActive && IsLoaded && IsVisibleInTree());
        return row;
    }

    private sealed class PlanRow(Grid root, TextBlock title, Ellipse pending, FontIcon check, ProgressRing ring)
    {
        internal Grid Root { get; } = root;
        internal TextBlock Title { get; } = title;
        internal Ellipse Pending { get; } = pending;
        internal FontIcon Check { get; } = check;
        internal ProgressRing Ring { get; } = ring;
        internal string Status { get; set; } = "pending";
        internal bool InViewport { get; set; } = true;
    }
}
