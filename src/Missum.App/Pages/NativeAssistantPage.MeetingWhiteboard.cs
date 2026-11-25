using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Missum.App.Controls;
using Missum.App.Services.Meetings;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private const double StickyWidth = 158;
    private const double StickyHeight = 148;
    private const double StickyGap = 12;
    private const double ColumnWidth = StickyWidth * 2 + StickyGap + 36;
    private static readonly Vector3 StickyLift = new(0, 0, 10);
    private Grid? _boardRoot;
    private TextBlock? _boardTitle;
    private FloatingToolbar? _boardToolbar;
    private TextBlock? _boardHint;
    private ScrollViewer? _boardScroll;
    private BoardPanSurface? _boardContent;
    private Canvas? _boardGridLines;
    private StackPanel? _boardColumns;
    private readonly Dictionary<string, BoardColumnView> _boardColumnViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StickyView> _stickyViews = new(StringComparer.Ordinal);
    private string? _boardDraggingNoteId;
    private string? _boardPendingEdit;
    private string? _boardEditingNoteId;

    private Grid MeetingBoardRoot()
    {
        if (_boardRoot is not null) return _boardRoot;
        _boardRoot = new Grid();
        _boardRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _boardRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Spacing = 6, Padding = new Thickness(28, 20, 280, 12) };
        _boardTitle = new TextBlock { FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        header.Children.Add(_boardTitle);
        // Tools float over the board in the shared translucent pill; the header keeps title and hint.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tools.Children.Add(FloatingToolbar.Tool("", "Notiz", "Neue Haftnotiz in der ersten Spalte", () => RunUiCallback("Board.Tool", () => AddStickyNote(null))));
        tools.Children.Add(FloatingToolbar.Tool("", "Spalte", "Neue Spalte hinzufügen", () => RunUiCallback("Board.Tool", AddBoardColumn)));
        tools.Children.Add(FloatingToolbar.Tool("", "Ordnen", "Missum sortiert, gruppiert und fasst das Whiteboard zusammen",
            () => RunUiCallback("Board.Tool", () => Meetings.RequestAgent("organize")), new MeetingAgentVisual { Height = 16, Width = 16 }));
        tools.Children.Add(FloatingToolbar.Separator());
        tools.Children.Add(FloatingToolbar.Tool("", "Text", "Textfeld auf die Fläche setzen", () => RunUiCallback("Board.Tool", () => AddCanvasElement("text"))));
        tools.Children.Add(FloatingToolbar.Tool("", "Tabelle", "Tabelle einfügen (Rechtsklick für Zeilen und Spalten)", () => RunUiCallback("Board.Tool", () => AddCanvasElement("table"))));
        tools.Children.Add(FloatingToolbar.Tool("", "Bild", "Bild aus Datei einfügen – oder Strg+V auf dem Whiteboard", () => RunUiCallback("Board.Tool", () => _ = PickImageAsync())));
        var shapes = FloatingToolbar.Tool("", "Form", "Rechteck, Ellipse oder Raute einfügen", () => { });
        var shapeMenu = new MenuFlyout();
        foreach (var (label, shape) in new[] { ("Rechteck", "rect"), ("Ellipse", "ellipse"), ("Raute", "diamond") })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => AddCanvasElement("shape", shape);
            shapeMenu.Items.Add(item);
        }
        shapes.Flyout = shapeMenu;
        tools.Children.Add(shapes);
        tools.Children.Add(FloatingToolbar.Tool("", "Pfeil", "Zwei Elemente oder Notizen nacheinander anklicken, um sie zu verbinden", () => RunUiCallback("Board.Tool", StartArrowTool)));
        _boardToolbar = new FloatingToolbar("Werkzeuge des Whiteboards", tools, "", "Whiteboard");
        _boardHint = new TextBlock { FontSize = 12, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrush("MissumMutedTextBrush", 150) };
        header.Children.Add(_boardHint);
        _boardRoot.Children.Add(header);

        _boardContent = new BoardPanSurface { Background = new SolidColorBrush(Colors.Transparent) };
        _boardGridLines = new Canvas { IsHitTestVisible = false };
        _boardContent.Children.Add(_boardGridLines);
        _boardColumns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Padding = new Thickness(28, 8, 28, 40), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        _boardContent.Children.Add(_boardColumns);
        CreateCanvasLayers(_boardContent);
        _boardContent.SizeChanged += (_, _) => DrawBoardGrid();
        _boardScroll = new ScrollViewer
        {
            Content = _boardContent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            // One fixed scale everywhere: zooming only confused people.
            ZoomMode = ZoomMode.Disabled,
        };
        _boardScroll.SizeChanged += (_, _) => SizeBoardContent();
        AutomationProperties.SetName(_boardScroll, "Whiteboard");
        Grid.SetRow(_boardScroll, 1);
        _boardRoot.Children.Add(_boardScroll);
        Grid.SetRowSpan(_boardToolbar!, 2);
        _boardRoot.Children.Add(_boardToolbar!);
        return _boardRoot;
    }

    /// <summary>Removes an element from whatever holds it, logical or visual parent.</summary>
    private static void DetachBoardElement(FrameworkElement element)
    {
        switch (element.Parent ?? VisualTreeHelper.GetParent(element))
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Border border when ReferenceEquals(border.Child, element): border.Child = null; break;
            case ContentControl control when ReferenceEquals(control.Content, element): control.Content = null; break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, element): presenter.Content = null; break;
        }
    }

    private void SizeBoardContent()
    {
        if (_boardScroll is null || _boardContent is null || _boardColumns is null) return;
        _boardContent.MinWidth = _boardScroll.ViewportWidth;
        _boardContent.MinHeight = _boardScroll.ViewportHeight;
    }

    private void DrawBoardGrid()
    {
        if (_boardGridLines is null || _boardContent is null) return;
        var width = Math.Ceiling(_boardContent.ActualWidth / 400) * 400;
        var height = Math.Ceiling(_boardContent.ActualHeight / 400) * 400;
        if (_boardGridLines.Width.Equals(width) && _boardGridLines.Height.Equals(height)) return;
        _boardGridLines.Children.Clear();
        _boardGridLines.Width = width;
        _boardGridLines.Height = height;
        var stroke = ThemeBrush("MissumStrokeBrush", 48);
        for (var x = 0.0; x <= width; x += 32)
            _boardGridLines.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = height, Stroke = stroke, StrokeThickness = 1, Opacity = .45 });
        for (var y = 0.0; y <= height; y += 32)
            _boardGridLines.Children.Add(new Line { X1 = 0, Y1 = y, X2 = width, Y2 = y, Stroke = stroke, StrokeThickness = 1, Opacity = .45 });
    }

    private void RenderMeetingBoard()
    {
        MeetingBoardRoot();
        var room = Meetings.Room;
        if (room is null) return;
        var snapshot = room.Snapshot();
        var canEdit = Meetings.CanEdit;
        _boardTitle!.Text = snapshot.Title;
        UpdateBoardHint();

        // FLIP: remember where every note was, rebuild, then animate from there.
        var before = new Dictionary<string, Windows.Foundation.Point>(StringComparer.Ordinal);
        foreach (var (id, view) in _stickyViews)
            if (view.Root.Parent is not null && view.Root.ActualWidth > 0)
                before[id] = view.Root.TransformToVisual(_boardContent).TransformPoint(new Windows.Foundation.Point(0, 0));

        var columns = snapshot.Whiteboard.Columns;
        var seenColumns = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            seenColumns.Add(column.Id);
            if (!_boardColumnViews.TryGetValue(column.Id, out var view))
                _boardColumnViews[column.Id] = view = CreateColumnView(column.Id);
            UpdateColumnView(view, column, index, canEdit, columns.Count);
            var current = _boardColumns!.Children.IndexOf(view.Root);
            if (current != index)
            {
                if (current >= 0) _boardColumns.Children.RemoveAt(current);
                _boardColumns.Children.Insert(Math.Min(index, _boardColumns.Children.Count), view.Root);
            }
        }
        foreach (var stale in _boardColumnViews.Keys.Where(id => !seenColumns.Contains(id)).ToArray())
        {
            _boardColumns!.Children.Remove(_boardColumnViews[stale].Root);
            _boardColumnViews.Remove(stale);
        }

        var appeared = new List<StickyView>();
        var seenNotes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in snapshot.Whiteboard.Notes.GroupBy(n => n.ColumnId))
        {
            if (!_boardColumnViews.TryGetValue(group.Key, out var columnView)) continue;
            var ordered = group.OrderBy(n => n.Order).ToList();
            var rows = (int)Math.Ceiling(ordered.Count / 2.0);
            while (columnView.Notes.RowDefinitions.Count < rows) columnView.Notes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            while (columnView.Notes.RowDefinitions.Count > rows) columnView.Notes.RowDefinitions.RemoveAt(columnView.Notes.RowDefinitions.Count - 1);
            for (var index = 0; index < ordered.Count; index++)
            {
                var note = ordered[index];
                seenNotes.Add(note.Id);
                var isNew = !_stickyViews.TryGetValue(note.Id, out var sticky);
                if (isNew)
                {
                    sticky = CreateStickyView(note.Id);
                    _stickyViews[note.Id] = sticky;
                    if (before.Count > 0 || _boardRendered) appeared.Add(sticky);
                }
                UpdateStickyView(sticky!, note, canEdit, snapshot.Whiteboard.Columns);
                if (!ReferenceEquals(sticky!.Root.Parent, columnView.Notes))
                {
                    DetachBoardElement(sticky.Root);
                    try { columnView.Notes.Children.Add(sticky.Root); }
                    catch (System.Runtime.InteropServices.COMException exception) when (exception.HResult == unchecked((int)0x800F1000))
                    {
                        // Still attached somewhere XAML does not report as its parent: start the note fresh.
                        ReportUiProjectionFailure("Board.StickyReparent", new InvalidOperationException(
                            $"parent={sticky.Root.Parent?.GetType().Name ?? "null"}, visual={VisualTreeHelper.GetParent(sticky.Root)?.GetType().Name ?? "null"}", exception));
                        sticky = CreateStickyView(note.Id);
                        _stickyViews[note.Id] = sticky;
                        UpdateStickyView(sticky, note, canEdit, snapshot.Whiteboard.Columns);
                        columnView.Notes.Children.Add(sticky.Root);
                    }
                }
                Grid.SetRow(sticky.Root, index / 2);
                Grid.SetColumn(sticky.Root, index % 2);
            }
        }
        foreach (var view in _boardColumnViews.Values)
            if (!snapshot.Whiteboard.Notes.Any(n => n.ColumnId == view.Id))
                view.Notes.RowDefinitions.Clear();
        foreach (var stale in _stickyViews.Keys.Where(id => !seenNotes.Contains(id)).ToArray())
        {
            DetachBoardElement(_stickyViews[stale].Root);
            _stickyViews.Remove(stale);
        }
        foreach (var view in _boardColumnViews.Values)
            view.Empty.Visibility = view.Notes.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _boardContent!.UpdateLayout();
        foreach (var (id, view) in _stickyViews)
        {
            if (!before.TryGetValue(id, out var old) || view.Root.Parent is null) continue;
            var now = view.Root.TransformToVisual(_boardContent).TransformPoint(new Windows.Foundation.Point(0, 0));
            var dx = (float)(old.X - now.X);
            var dy = (float)(old.Y - now.Y);
            if (Math.Abs(dx) > .5 || Math.Abs(dy) > .5) AnimateStickyMove(view.Root, dx, dy);
        }
        foreach (var sticky in appeared) AnimateStickyAppear(sticky.Root);
        _boardRendered = true;
        RenderCanvasElements(snapshot, canEdit);
        OpenPendingCanvasEdit();

        if (_boardPendingEdit is { } pending && _stickyViews.TryGetValue(pending, out var editTarget))
        {
            _boardPendingEdit = null;
            BeginStickyEdit(editTarget);
        }
    }

    private bool _boardRendered;

    private void UpdateBoardHint()
    {
        if (_boardHint is null) return;
        var canEdit = Meetings.CanEdit;
        var empty = Meetings.Room?.Read(static s => s.Whiteboard.Notes.Count == 0 && s.Whiteboard.Elements.Count == 0) ?? true;
        _boardHint.Text = !canEdit ? "Nur lesen – nicht verbunden"
            : _arrowTool ? (_arrowStartId is null ? "Pfeil: Start anklicken · Esc bricht ab" : "Pfeil: jetzt das Ziel anklicken")
            : empty ? "Linke Maustaste auf freier Fläche verschiebt das Board"
            : "Freie Fläche ziehen zum Bewegen · Rechtsklick für mehr";
        _boardHint.Foreground = _arrowTool ? ThemeBrush("MissumAccentReadableBrush", 190) : ThemeBrush("MissumMutedTextBrush", 150);
    }

    private static void AnimateStickyMove(UIElement element, float dx, float dy)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0f, new Vector3(dx, dy, StickyLift.Z + 14));
        animation.InsertKeyFrame(1f, StickyLift, compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(520);
        visual.StartAnimation("Translation", animation);
    }

    private static void AnimateStickyAppear(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)StickyWidth / 2, (float)StickyHeight / 2, 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(.6f, .6f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, compositor.CreateCubicBezierEasingFunction(new Vector2(.3f, 1.4f), new Vector2(.5f, 1f)));
        scale.Duration = TimeSpan.FromMilliseconds(420);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(260);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", fade);
    }

    private BoardColumnView CreateColumnView(string id)
    {
        var number = new TextBlock { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeBrush("MissumMutedTextBrush", 150) };
        var title = new TextBlock { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(number);
        titleRow.Children.Add(title);
        var subtitle = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MissumMutedTextBrush", 160), MaxLines = 2 };
        var menu = new Button
        {
            Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 14, Foreground = NativeIconPalette.BrushFor("navigation") },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(menu, "Spalte bearbeiten");
        var header = new Grid();
        var headerText = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 30, 0) };
        headerText.Children.Add(titleRow);
        headerText.Children.Add(subtitle);
        header.Children.Add(headerText);
        header.Children.Add(menu);
        var notes = new Grid { ColumnSpacing = StickyGap, RowSpacing = StickyGap, Margin = new Thickness(0, 14, 0, 0) };
        notes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StickyWidth) });
        notes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StickyWidth) });
        var empty = new Border
        {
            Height = StickyHeight,
            CornerRadius = new CornerRadius(10),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 60),
            BorderThickness = new Thickness(1.5),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = "Notizen hierher ziehen\noder doppelklicken",
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("MissumMutedTextBrush", 140),
                FontSize = 13,
            },
        };
        var add = new HyperlinkButton
        {
            Content = new TextBlock { Text = "+ Notiz", FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 150) },
            Margin = new Thickness(-8, 8, 0, 0),
        };
        add.Click += (_, _) => AddStickyNote(id);
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(notes);
        stack.Children.Add(empty);
        stack.Children.Add(add);
        var root = new Border
        {
            Width = ColumnWidth,
            Padding = new Thickness(18, 16, 18, 12),
            CornerRadius = new CornerRadius(18),
            Background = ThemeBrush("MissumLayerBrush", 32),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            AllowDrop = true,
            Child = stack,
        };
        var view = new BoardColumnView(id, root, number, title, subtitle, notes, empty, menu);
        root.DragOver += (_, args) =>
        {
            if (_boardDraggingNoteId is null) return;
            args.AcceptedOperation = DataPackageOperation.Move;
            args.DragUIOverride.Caption = "Hierher verschieben";
            args.DragUIOverride.IsGlyphVisible = false;
            root.BorderBrush = ThemeBrush("MissumAccentBrush", 170);
        };
        root.DragLeave += (_, _) => root.BorderBrush = ThemeBrush("MissumStrokeBrush", 48);
        root.Drop += (_, args) =>
        {
            root.BorderBrush = ThemeBrush("MissumStrokeBrush", 48);
            if (_boardDraggingNoteId is not { } noteId) return;
            var point = args.GetPosition(notes);
            DropStickyAt(noteId, id, point);
        };
        root.DoubleTapped += (_, args) =>
        {
            if (args.OriginalSource is FrameworkElement source && FindSticky(source) is not null) return;
            AddStickyNote(id);
        };
        return view;
    }

    private void UpdateColumnView(BoardColumnView view, WhiteboardColumn column, int index, bool canEdit, int columnCount)
    {
        view.Number.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        view.Title.Text = column.Title;
        view.Subtitle.Text = column.Subtitle ?? "";
        view.Subtitle.Visibility = string.IsNullOrWhiteSpace(column.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        view.Menu.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Titel und Untertitel ändern", Icon = new FontIcon { Glyph = "" } };
        rename.Click += (_, _) => _ = EditColumnAsync(column);
        flyout.Items.Add(rename);
        var add = new MenuFlyoutItem { Text = "Notiz hinzufügen", Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => AddStickyNote(column.Id);
        flyout.Items.Add(add);
        if (columnCount > 1)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var delete = new MenuFlyoutItem { Text = "Spalte löschen (Notizen bleiben)", Icon = new FontIcon { Glyph = "" } };
            delete.Click += (_, _) => Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ColumnDelete, ColumnId = column.Id });
            flyout.Items.Add(delete);
        }
        view.Menu.Flyout = flyout;
        view.Root.AllowDrop = canEdit;
    }

    private async Task EditColumnAsync(WhiteboardColumn column)
    {
        var title = new TextBox { Header = "Titel", Text = column.Title, MaxLength = 80 };
        var subtitle = new TextBox { Header = "Untertitel", Text = column.Subtitle ?? "", MaxLength = 140, PlaceholderText = "Optional" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(title);
        panel.Children.Add(subtitle);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Spalte bearbeiten",
            Content = panel,
            PrimaryButtonText = "Speichern",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Meetings.Send(new MeetingOp
        {
            Type = MeetingOpTypes.ColumnUpsert,
            Column = new WhiteboardColumn { Id = column.Id, Title = title.Text, Subtitle = subtitle.Text },
        });
    }

    private StickyView CreateStickyView(string id)
    {
        var text = new TextBlock
        {
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 5,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22)),
        };
        var editor = new TextBox
        {
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22)),
            MaxLength = 600,
        };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            editor.Resources[key] = new SolidColorBrush(Colors.Transparent);
        var dark = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22));
        editor.Resources["TextControlForeground"] = dark;
        editor.Resources["TextControlForegroundFocused"] = dark;
        editor.Resources["TextControlForegroundPointerOver"] = dark;
        var author = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(200, 0x44, 0x44, 0x4A)), VerticalAlignment = VerticalAlignment.Bottom };
        var reactions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 0) };
        var corner = new Polygon
        {
            Points = { new Windows.Foundation.Point(0, 14), new Windows.Foundation.Point(14, 0), new Windows.Foundation.Point(14, 14) },
            Fill = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -12, -10),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(text);
        grid.Children.Add(editor);
        Grid.SetRow(author, 1);
        Grid.SetRow(reactions, 1);
        Grid.SetRow(corner, 1);
        grid.Children.Add(author);
        grid.Children.Add(reactions);
        grid.Children.Add(corner);
        var root = new Border
        {
            Width = StickyWidth,
            Height = StickyHeight,
            Padding = new Thickness(12, 12, 12, 10),
            CornerRadius = new CornerRadius(3),
            Shadow = new ThemeShadow(),
            Translation = StickyLift,
            Child = grid,
        };
        AutomationProperties.SetName(root, "Haftnotiz");
        var view = new StickyView(id, root, text, editor, author, reactions);
        root.DoubleTapped += (_, args) => { args.Handled = true; BeginStickyEdit(view); };
        root.Tapped += (_, args) =>
        {
            if (!_arrowTool) return;
            args.Handled = true;
            PickArrowEndpoint(id);
        };
        root.DragStarting += (_, args) =>
        {
            if (_boardEditingNoteId == id || _arrowTool) { args.Cancel = true; return; }
            _boardDraggingNoteId = id;
            args.Data.SetText(view.Text.Text);
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        root.DropCompleted += (_, _) => _boardDraggingNoteId = null;
        editor.LostFocus += (_, _) => CommitStickyEdit(view);
        editor.PreviewKeyDown += (_, args) =>
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (args.Key == VirtualKey.Enter && !shift) { args.Handled = true; root.Focus(FocusState.Programmatic); CommitStickyEdit(view); }
            else if (args.Key == VirtualKey.Escape) { args.Handled = true; editor.Text = view.Text.Text; EndStickyEdit(view); }
        };
        return view;
    }

    private void UpdateStickyView(StickyView view, WhiteboardNote note, bool canEdit, IReadOnlyList<WhiteboardColumn> columns)
    {
        view.Note = note;
        view.Root.Background = new SolidColorBrush(StickyColors[Math.Abs(note.ColorIndex) % StickyColors.Length]);
        view.Text.Text = note.Text.Length == 0 ? " " : note.Text;
        view.Author.Text = note.IsAi ? "Missum" : note.Author;
        view.Author.Foreground = note.IsAi ? new SolidColorBrush(Color.FromArgb(255, 0xB4, 0x52, 0x34)) : new SolidColorBrush(Color.FromArgb(200, 0x44, 0x44, 0x4A));
        view.Root.CanDrag = canEdit && _boardEditingNoteId != note.Id;
        ToolTipService.SetToolTip(view.Root, note.Text.Length > 90 ? note.Text : null);
        view.Reactions.Children.Clear();
        foreach (var (key, names) in note.Reactions.Where(r => r.Value.Count > 0))
        {
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4, 0, 5, 1),
                Child = new TextBlock { Text = ReactionGlyph(key) + (names.Count > 1 ? names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""), FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x1E, 0x22)) },
            };
            ToolTipService.SetToolTip(chip, string.Join(", ", names));
            view.Reactions.Children.Add(chip);
        }
        view.Root.ContextFlyout = canEdit ? StickyMenu(view, note, columns) : null;
    }

    private static string ReactionGlyph(string key) => key switch
    {
        "heart" => "❤️",
        "plus" => "➕",
        "thumb" => "👍",
        _ => "😊",
    };

    private MenuFlyout StickyMenu(StickyView view, WhiteboardNote note, IReadOnlyList<WhiteboardColumn> columns)
    {
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Bearbeiten", Icon = new FontIcon { Glyph = "" } };
        edit.Click += (_, _) => BeginStickyEdit(view);
        menu.Items.Add(edit);
        var react = new MenuFlyoutSubItem { Text = "Reagieren", Icon = new FontIcon { Glyph = "" } };
        foreach (var (key, label) in new[] { ("heart", "❤️  Herz"), ("plus", "➕  +1"), ("thumb", "👍  Daumen hoch"), ("smile", "😊  Lächeln") })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => Meetings.Send(new MeetingOp { Type = MeetingOpTypes.NoteReact, NoteId = note.Id, Reaction = key });
            react.Items.Add(item);
        }
        menu.Items.Add(react);
        var color = new MenuFlyoutSubItem { Text = "Farbe", Icon = new FontIcon { Glyph = "" } };
        string[] names = ["Blau", "Lila", "Grün", "Rosa", "Gelb", "Mint", "Pfirsich", "Sand"];
        for (var index = 0; index < StickyColors.Length; index++)
        {
            var colorIndex = index;
            var item = new MenuFlyoutItem
            {
                Text = names[index],
                Icon = new FontIcon { Glyph = "", Foreground = new SolidColorBrush(StickyColors[index]) },
            };
            item.Click += (_, _) => Meetings.Send(new MeetingOp
            {
                Type = MeetingOpTypes.NoteUpsert,
                Note = new WhiteboardNote { Id = note.Id, Text = note.Text, ColumnId = note.ColumnId, ColorIndex = colorIndex, Order = note.Order },
            });
            color.Items.Add(item);
        }
        menu.Items.Add(color);
        if (columns.Count > 1)
        {
            var move = new MenuFlyoutSubItem { Text = "Verschieben nach", Icon = new FontIcon { Glyph = "" } };
            foreach (var column in columns.Where(c => c.Id != note.ColumnId))
            {
                var target = column.Id;
                var item = new MenuFlyoutItem { Text = column.Title };
                item.Click += (_, _) => Meetings.Send(new MeetingOp { Type = MeetingOpTypes.NoteMove, NoteId = note.Id, ColumnId = target });
                move.Items.Add(item);
            }
            menu.Items.Add(move);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Löschen", Icon = new FontIcon { Glyph = "" } };
        delete.Click += (_, _) => Meetings.Send(new MeetingOp { Type = MeetingOpTypes.NoteDelete, NoteId = note.Id });
        menu.Items.Add(delete);
        return menu;
    }

    private void BeginStickyEdit(StickyView view)
    {
        if (!Meetings.CanEdit) return;
        _boardEditingNoteId = view.Id;
        view.Root.CanDrag = false;
        view.Editor.Text = view.Note?.Text ?? "";
        view.Text.Visibility = Visibility.Collapsed;
        view.Editor.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            view.Editor.Focus(FocusState.Programmatic);
            view.Editor.SelectionStart = view.Editor.Text.Length;
        });
    }

    private void CommitStickyEdit(StickyView view)
    {
        if (_boardEditingNoteId != view.Id) return;
        var note = view.Note;
        var text = view.Editor.Text.Replace("\r", "\n", StringComparison.Ordinal).Trim();
        EndStickyEdit(view);
        if (note is null) return;
        if (text.Length == 0 && note.Text.Length == 0)
        {
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.NoteDelete, NoteId = note.Id });
            return;
        }
        if (text == note.Text) return;
        view.Text.Text = text;
        Meetings.Send(new MeetingOp
        {
            Type = MeetingOpTypes.NoteUpsert,
            Note = new WhiteboardNote { Id = note.Id, Text = text, ColumnId = note.ColumnId, ColorIndex = note.ColorIndex, Order = note.Order },
        });
    }

    private void EndStickyEdit(StickyView view)
    {
        if (_boardEditingNoteId == view.Id) _boardEditingNoteId = null;
        view.Editor.Visibility = Visibility.Collapsed;
        view.Text.Visibility = Visibility.Visible;
        view.Root.CanDrag = Meetings.CanEdit;
    }

    private void AddStickyNote(string? columnId)
    {
        if (!Meetings.CanEdit) return;
        var room = Meetings.Room;
        var target = columnId ?? room?.Read(s => s.Whiteboard.Columns.FirstOrDefault()?.Id);
        if (target is null) return;
        var color = room?.Read(s => s.Participants.FirstOrDefault(p => p.Id == Meetings.LocalParticipantId)?.ColorIndex) ?? 0;
        var note = new WhiteboardNote { Text = "", ColumnId = target, ColorIndex = color };
        _boardPendingEdit = note.Id;
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.NoteUpsert, Note = note });
    }

    private void AddBoardColumn()
    {
        if (!Meetings.CanEdit) return;
        var count = Meetings.Room?.Read(s => s.Whiteboard.Columns.Count) ?? 0;
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ColumnUpsert, Column = new WhiteboardColumn { Title = $"Spalte {count + 1}" } });
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _boardScroll?.ChangeView(_boardScroll.ScrollableWidth + 400, null, null));
    }

    private void DropStickyAt(string noteId, string columnId, Windows.Foundation.Point point)
    {
        var room = Meetings.Room;
        if (room is null) return;
        var siblings = room.Read(s => s.Whiteboard.Notes.Where(n => n.ColumnId == columnId && n.Id != noteId).OrderBy(n => n.Order).Select(n => n.Order).ToList());
        var row = Math.Max(0, (int)(point.Y / (StickyHeight + StickyGap)));
        var column = point.X > StickyWidth + StickyGap / 2 ? 1 : 0;
        var index = Math.Clamp(row * 2 + column, 0, siblings.Count);
        var previous = index == 0 ? (siblings.Count > 0 ? siblings[0] - 1 : 0) : siblings[index - 1];
        var next = index < siblings.Count ? siblings[index] : previous + 2;
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.NoteMove, NoteId = noteId, ColumnId = columnId, Order = (previous + next) / 2 });
        _boardDraggingNoteId = null;
    }

    private StickyView? FindSticky(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Border border && _stickyViews.Values.FirstOrDefault(v => ReferenceEquals(v.Root, border)) is { } view) return view;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private sealed class BoardColumnView(string id, Border root, TextBlock number, TextBlock title, TextBlock subtitle, Grid notes, Border empty, Button menu)
    {
        public string Id { get; } = id;
        public Border Root { get; } = root;
        public TextBlock Number { get; } = number;
        public TextBlock Title { get; } = title;
        public TextBlock Subtitle { get; } = subtitle;
        public Grid Notes { get; } = notes;
        public Border Empty { get; } = empty;
        public Button Menu { get; } = menu;
    }

    private sealed class StickyView(string id, Border root, TextBlock text, TextBox editor, TextBlock author, StackPanel reactions)
    {
        public string Id { get; } = id;
        public Border Root { get; } = root;
        public TextBlock Text { get; } = text;
        public TextBox Editor { get; } = editor;
        public TextBlock Author { get; } = author;
        public StackPanel Reactions { get; } = reactions;
        public WhiteboardNote? Note { get; set; }
    }
}
