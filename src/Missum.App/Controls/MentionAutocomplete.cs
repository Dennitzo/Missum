using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Missum.App.Controls;

internal sealed record MentionCandidate(string Name, string Detail, Brush Color, bool IsAgent);

/// <summary>
/// "@" suggestions for a TextBox: typing @ lists Missum and the participants,
/// arrow keys or the mouse pick one, Tab or Enter inserts it. Focus never leaves
/// the TextBox, so typing simply continues and narrows the list.
/// </summary>
internal sealed class MentionAutocomplete
{
    private const int MaximumQueryLength = 30;
    private readonly TextBox _box;
    private readonly Func<IReadOnlyList<MentionCandidate>> _source;
    private readonly Popup _popup = new() { IsLightDismissEnabled = false, ShouldConstrainToRootBounds = true };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private List<MentionCandidate> _items = [];
    private int _selected;
    private int _start = -1;
    private bool _inserting;

    public MentionAutocomplete(TextBox box, Func<IReadOnlyList<MentionCandidate>> source)
    {
        _box = box;
        _source = source;
        _popup.Child = new Border
        {
            MinWidth = 220,
            MaxWidth = 300,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(12),
            Background = NativeThemeBrushes.Resource("MissumLayerStrongBrush", 43),
            BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 60),
            BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Child = _list,
        };
        AutomationProperties.SetName(_popup, "Erwähnungen");
        box.TextChanged += (_, _) => Update();
        box.SelectionChanged += (_, _) => { if (IsOpen) Update(); };
        box.LostFocus += (_, _) => Close();
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Call first from the box's PreviewKeyDown; returns true when the key was used.</summary>
    public bool HandleKey(KeyRoutedEventArgs args)
    {
        if (!IsOpen || _items.Count == 0) return false;
        switch (args.Key)
        {
            case VirtualKey.Down:
                _selected = (_selected + 1) % _items.Count;
                Render();
                break;
            case VirtualKey.Up:
                _selected = (_selected - 1 + _items.Count) % _items.Count;
                Render();
                break;
            case VirtualKey.Tab:
            case VirtualKey.Enter:
                Insert(_items[_selected]);
                break;
            case VirtualKey.Escape:
                Close();
                break;
            default:
                return false;
        }
        args.Handled = true;
        return true;
    }

    /// <summary>The "@query" right before the caret, if the caret is inside a mention being typed.</summary>
    internal static (int Start, string Query)? FindQuery(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        for (var index = caret - 1; index >= 0 && caret - index <= MaximumQueryLength + 1; index--)
        {
            var character = text[index];
            if (character == '@')
            {
                if (index > 0 && !char.IsWhiteSpace(text[index - 1])) return null;
                return (index, text[(index + 1)..caret]);
            }
            if (char.IsWhiteSpace(character)) return null;
        }
        return null;
    }

    private void Update()
    {
        if (_inserting) return;
        var found = FindQuery(_box.Text, _box.SelectionStart);
        if (found is not { } query || _box.FocusState == FocusState.Unfocused) { Close(); return; }
        var matches = _source()
            .Where(c => query.Query.Length == 0
                || c.Name.StartsWith(query.Query, StringComparison.OrdinalIgnoreCase)
                || c.Name.Split(' ').Any(part => part.StartsWith(query.Query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0) { Close(); return; }
        var keep = _selected < _items.Count ? _items[_selected].Name : null;
        _items = matches;
        _start = query.Start;
        _selected = Math.Max(0, keep is null ? 0 : _items.FindIndex(i => i.Name == keep));
        Render();
        if (!_popup.IsOpen)
        {
            _popup.XamlRoot = _box.XamlRoot;
            _popup.PlacementTarget = _box;
            _popup.DesiredPlacement = PopupPlacementMode.TopEdgeAlignedLeft;
            _popup.IsOpen = true;
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var selected = index == _selected;
            var row = new Grid { ColumnSpacing = 9 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = item.Color,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.IsAgent ? "M" : item.Name[..1].ToUpperInvariant(),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(new TextBlock { Text = "@" + item.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 11, Foreground = NativeThemeBrushes.MutedText, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var border = new Border
            {
                Padding = new Thickness(8, 5, 10, 6),
                CornerRadius = new CornerRadius(8),
                Background = selected ? NativeThemeBrushes.Resource("MissumSelectionBrush", 60) : new SolidColorBrush(Colors.Transparent),
                Child = row,
            };
            var choice = item;
            var position = index;
            // Borders are not focusable, so a click picks the entry while the TextBox keeps focus.
            border.PointerEntered += (_, _) => { if (_selected != position) { _selected = position; Render(); } };
            border.PointerPressed += (_, args) => { args.Handled = true; Insert(choice); };
            AutomationProperties.SetName(border, "@" + item.Name);
            _list.Children.Add(border);
        }
    }

    private void Insert(MentionCandidate item)
    {
        if (_start < 0) return;
        var caret = Math.Clamp(_box.SelectionStart, _start, _box.Text.Length);
        var before = _box.Text[.._start];
        var after = _box.Text[caret..];
        var inserted = "@" + item.Name + " ";
        _inserting = true;
        try
        {
            _box.Text = before + inserted + after.TrimStart(' ');
            _box.SelectionStart = before.Length + inserted.Length;
        }
        finally { _inserting = false; }
        Close();
        _box.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        _popup.IsOpen = false;
        _start = -1;
    }
}
