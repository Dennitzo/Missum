using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Missum.App.Controls;
using Missum.App.Services.Meetings;
using Windows.System;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Grid? _notesRoot;
    private StackPanel? _notesBlocks;
    private TextBox? _notesTitle;
    private TextBlock? _notesSubtitle;
    private ScrollViewer? _notesScroll;
    private string? _notesDocumentId;
    private string? _notesFocusedBlockId;
    private string? _notesPendingFocus;
    private bool _notesPendingFocusAtEnd;
    private readonly Dictionary<string, NoteRow> _noteRows = new(StringComparer.Ordinal);
    private bool _notesSyncing;

    private Grid MeetingNotesRoot()
    {
        if (_notesRoot is not null) return _notesRoot;
        // Meetings use exactly one notes document, so there is no document sidebar.
        _notesRoot = new Grid();

        var page = new StackPanel { MaxWidth = 780, Spacing = 2, Padding = new Thickness(36, 28, 36, 80), HorizontalAlignment = HorizontalAlignment.Stretch };
        _notesTitle = PlainTextBox(30, "Titel");
        _notesTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _notesTitle.Margin = new Thickness(-6, 0, 0, 0);
        AutomationProperties.SetName(_notesTitle, "Dokumenttitel");
        _notesTitle.LostFocus += (_, _) => CommitNotesTitle();
        _notesTitle.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { args.Handled = true; CommitNotesTitle(); FocusBlock(FirstBlockId(), atEnd: false); } };
        page.Children.Add(_notesTitle);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 14) };
        _notesSubtitle = new TextBlock { FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 160), VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(_notesSubtitle);
        meta.Children.Add(LinkButton("Von Missum zusammenfassen lassen", () => Meetings.RequestAgent("summarize")));
        page.Children.Add(meta);
        page.Children.Add(NotesToolbar());
        _notesBlocks = new StackPanel { Spacing = 0, Margin = new Thickness(0, 10, 0, 0) };
        page.Children.Add(_notesBlocks);
        var addBlock = new HyperlinkButton
        {
            Content = new TextBlock { Text = "+ Absatz hinzufügen", FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 150) },
            Margin = new Thickness(-6, 10, 0, 0),
        };
        addBlock.Click += (_, _) => InsertBlockAfter(LastBlockId(), "p");
        page.Children.Add(addBlock);
        _notesScroll = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _notesRoot.Children.Add(_notesScroll);
        return _notesRoot;
    }

    private HyperlinkButton LinkButton(string text, Action onClick)
    {
        var link = new HyperlinkButton { Content = new TextBlock { Text = text, FontSize = 13 }, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        link.Click += (_, _) => RunUiCallback("Notes.Link", onClick);
        return link;
    }

    private StackPanel NotesToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (kind, label, glyph) in new[]
                 {
                     ("p", "Text", ""), ("h2", "Überschrift", ""), ("bullet", "Liste", ""), ("todo", "Aufgabe", ""),
                 })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = NativeIconPalette.BrushFor("documents") });
            content.Children.Add(new TextBlock { Text = label, FontSize = 12 });
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(9, 4, 10, 5),
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrush("MissumLayerStrongBrush", 40),
                BorderThickness = new Thickness(0),
                IsTabStop = false,
                AllowFocusOnInteraction = false,
            };
            button.Resources["ButtonBackgroundPointerOver"] = ThemeBrush("MissumHoverBrush", 60);
            ToolTipService.SetToolTip(button, $"Aktuellen Block in „{label}“ umwandeln");
            button.Click += (_, _) => ChangeFocusedBlockKind(kind);
            bar.Children.Add(button);
        }
        return bar;
    }

    private MeetingDocument? CurrentNotesDocument(MeetingRoomState state)
    {
        var document = state.Documents.FirstOrDefault();
        _notesDocumentId = document?.Id;
        return document;
    }

    private void RenderMeetingNotes()
    {
        MeetingNotesRoot();
        var room = Meetings.Room;
        if (room is null) return;
        var snapshot = room.Snapshot();
        var presence = room.Presence();
        var document = CurrentNotesDocument(snapshot);
        if (document is null) return;
        var canEdit = Meetings.CanEdit;
        _notesSyncing = true;
        try
        {
            if (_notesTitle!.FocusState == FocusState.Unfocused && _notesTitle.Text != document.Title) _notesTitle.Text = document.Title;
            _notesTitle.IsReadOnly = !canEdit;
            var aiBlocks = document.Blocks.Count(b => b.AiOwned && !string.IsNullOrWhiteSpace(b.Text) && b.Section?.EndsWith(".h", StringComparison.Ordinal) != true);
            _notesSubtitle!.Text = (canEdit ? "Alle Teilnehmer können bearbeiten" : "Nur lesen – nicht verbunden")
                + (aiBlocks > 0 ? $" · Missum hat {aiBlocks} Einträge geschrieben" : " · Missum schreibt live mit");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < document.Blocks.Count; index++)
            {
                var block = document.Blocks[index];
                seen.Add(block.Id);
                if (!_noteRows.TryGetValue(block.Id, out var row) || row.Kind != block.Kind)
                {
                    if (row is not null) _notesBlocks!.Children.Remove(row.Root);
                    row = CreateNoteRow(block);
                    _noteRows[block.Id] = row;
                }
                UpdateNoteRow(row, block, presence, snapshot, canEdit);
                var current = _notesBlocks!.Children.IndexOf(row.Root);
                if (current != index)
                {
                    if (current >= 0) _notesBlocks.Children.RemoveAt(current);
                    _notesBlocks.Children.Insert(Math.Min(index, _notesBlocks.Children.Count), row.Root);
                }
            }
            foreach (var stale in _noteRows.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                _notesBlocks!.Children.Remove(_noteRows[stale].Root);
                _noteRows.Remove(stale);
            }
        }
        finally { _notesSyncing = false; }

        if (_notesPendingFocus is { } focus && _noteRows.ContainsKey(focus))
        {
            _notesPendingFocus = null;
            FocusBlock(focus, _notesPendingFocusAtEnd);
        }
    }

    private void CommitNotesTitle()
    {
        if (_notesSyncing || _notesTitle is null || _notesDocumentId is null || !Meetings.CanEdit) return;
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.DocumentRename, DocumentId = _notesDocumentId, Text = _notesTitle.Text });
    }

    private NoteRow CreateNoteRow(MeetingNoteBlock block)
    {
        var (size, weight, top) = block.Kind switch
        {
            "h1" => (28.0, Microsoft.UI.Text.FontWeights.SemiBold, 10.0),
            "h2" => (21.0, Microsoft.UI.Text.FontWeights.SemiBold, 14.0),
            "h3" => (16.0, Microsoft.UI.Text.FontWeights.SemiBold, 8.0),
            _ => (15.0, Microsoft.UI.Text.FontWeights.Normal, 0.0),
        };
        var root = new Grid { Margin = new Thickness(0, top, 0, 0), ColumnSpacing = 2 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var accent = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 0, 4) };
        root.Children.Add(accent);
        FrameworkElement? marker = null;
        CheckBox? check = null;
        if (block.Kind == "bullet")
            marker = new TextBlock { Text = "•", FontSize = 18, Width = 20, TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 1, 0, 0), Foreground = ThemeBrush("MissumMutedTextBrush", 170) };
        else if (block.Kind == "todo")
            marker = check = new CheckBox { MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        if (marker is not null)
        {
            Grid.SetColumn(marker, 1);
            root.Children.Add(marker);
        }
        var box = PlainTextBox(size, block.Kind switch { "h1" or "h2" or "h3" => "Überschrift", "todo" => "Aufgabe", _ => "Schreibe etwas …" });
        box.FontWeight = weight;
        box.AcceptsReturn = true;
        Grid.SetColumn(box, 2);
        root.Children.Add(box);
        var tag = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 6, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetColumn(tag, 3);
        root.Children.Add(tag);

        var row = new NoteRow(block.Id, block.Kind, root, box, accent, tag, check);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => CommitNoteRow(row);
        row.Mention = new MentionAutocomplete(box, MeetingMentionCandidates);
        row.Debounce = timer;
        box.TextChanged += (_, _) => { if (!_notesSyncing) { row.Dirty = true; timer.Stop(); timer.Start(); } };
        box.GotFocus += (_, _) =>
        {
            _notesFocusedBlockId = row.Id;
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.Presence, DocumentId = _notesDocumentId, BlockId = row.Id });
        };
        box.LostFocus += (_, _) =>
        {
            CommitNoteRow(row, explicitCommit: true);
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.Presence });
        };
        box.PreviewKeyDown += (_, args) => OnNoteKeyDown(row, args);
        if (check is not null)
            check.Click += (_, _) => { row.Done = check.IsChecked == true; row.Dirty = true; CommitNoteRow(row); };
        return row;
    }

    private void UpdateNoteRow(NoteRow row, MeetingNoteBlock block, IReadOnlyDictionary<Guid, (string? DocumentId, string? BlockId)> presence,
        MeetingRoomState snapshot, bool canEdit)
    {
        // Never overwrite what the user is typing right now; their edit wins and is sent shortly.
        if (row.Box.FocusState == FocusState.Unfocused && !row.Dirty && row.Box.Text != block.Text) row.Box.Text = block.Text;
        row.Box.IsReadOnly = !canEdit;
        row.Done = block.Done;
        if (row.Check is not null) { row.Check.IsChecked = block.Done; row.Check.IsEnabled = canEdit; }
        row.Box.Foreground = block.Done ? ThemeBrush("MissumMutedTextBrush", 150) : ThemeBrush("MissumTextBrush", 240);
        var aiFresh = block.AiOwned && !block.HumanEdited && block.Section?.EndsWith(".h", StringComparison.Ordinal) != true && block.Section != MeetingAgentMerge.RootSection;
        row.Accent.Background = aiFresh ? new SolidColorBrush(Windows.UI.Color.FromArgb(160, MeetingAgentVisual.AccentColor.R, MeetingAgentVisual.AccentColor.G, MeetingAgentVisual.AccentColor.B)) : null;
        ToolTipService.SetToolTip(row.Accent, aiFresh ? "Von Missum geschrieben" : null);

        var editor = presence.FirstOrDefault(p => p.Key != Meetings.LocalParticipantId && p.Value.BlockId == block.Id && p.Value.DocumentId == _notesDocumentId).Key;
        var person = editor == Guid.Empty ? null : snapshot.Participants.FirstOrDefault(p => p.Id == editor);
        if (person is not null)
        {
            row.Tag.Background = ParticipantBrush(person.ColorIndex);
            row.Tag.Child = new TextBlock { Text = person.Name, FontSize = 11, Foreground = new SolidColorBrush(Colors.White) };
            row.Tag.Visibility = Visibility.Visible;
        }
        else if (block.Section == MeetingAgentMerge.RootSection)
        {
            row.Tag.Background = MeetingAgentVisual.AccentBrush;
            row.Tag.Child = new TextBlock { Text = "Missum", FontSize = 11, Foreground = new SolidColorBrush(Colors.White) };
            row.Tag.Visibility = Visibility.Visible;
        }
        else row.Tag.Visibility = Visibility.Collapsed;
    }

    private void CommitNoteRow(NoteRow row, bool explicitCommit = false)
    {
        row.Debounce?.Stop();
        // "@Missum <Frage>" in a note becomes a task once the person finishes the line.
        if (explicitCommit) AskMissumFromNote(row);
        if (!row.Dirty || _notesDocumentId is null || !Meetings.CanEdit) return;
        row.Dirty = false;
        Meetings.Send(new MeetingOp
        {
            Type = MeetingOpTypes.BlockUpsert,
            DocumentId = _notesDocumentId,
            Block = new MeetingNoteBlock { Id = row.Id, Kind = row.Kind, Text = row.Box.Text.Replace("\r", "\n", StringComparison.Ordinal).TrimEnd('\n'), Done = row.Done },
        });
    }

    private void AskMissumFromNote(NoteRow row)
    {
        if (_notesDocumentId is null || !Meetings.CanEdit) return;
        var question = MissumQuestion(row.Box.Text);
        if (question is null || question == row.AskedQuestion) return;
        row.AskedQuestion = question;
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.AgentRequest, Text = question, DocumentId = _notesDocumentId, BlockId = row.Id });
    }

    /// <summary>The text after "@Missum", if the note addresses Missum with an actual request.</summary>
    internal static string? MissumQuestion(string text)
    {
        var index = text.IndexOf("@" + MeetingRoom.AgentName, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var question = text[(index + MeetingRoom.AgentName.Length + 1)..].Trim().TrimStart(',', ':').Trim();
        return question.Length >= 2 ? question : null;
    }

    private void OnNoteKeyDown(NoteRow row, KeyRoutedEventArgs args)
    {
        if (row.Mention?.HandleKey(args) == true) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (args.Key == VirtualKey.Enter && !shift)
        {
            args.Handled = true;
            CommitNoteRow(row, explicitCommit: true);
            InsertBlockAfter(row.Id, row.Kind is "bullet" or "todo" ? row.Kind : "p");
        }
        else if (args.Key == VirtualKey.Back && row.Box.Text.Length == 0)
        {
            args.Handled = true;
            var previous = PreviousBlockId(row.Id);
            if (previous is null || _notesDocumentId is null) return;
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.BlockDelete, DocumentId = _notesDocumentId, BlockId = row.Id });
            FocusBlock(previous, atEnd: true);
        }
        else if (args.Key == VirtualKey.Up && row.Box.SelectionStart == 0 && PreviousBlockId(row.Id) is { } up)
        {
            args.Handled = true;
            FocusBlock(up, atEnd: true);
        }
        else if (args.Key == VirtualKey.Down && row.Box.SelectionStart >= row.Box.Text.Length && NextBlockId(row.Id) is { } down)
        {
            args.Handled = true;
            FocusBlock(down, atEnd: false);
        }
    }

    private void InsertBlockAfter(string? afterId, string kind)
    {
        if (_notesDocumentId is null || !Meetings.CanEdit) return;
        var block = new MeetingNoteBlock { Kind = kind, Text = "" };
        _notesPendingFocus = block.Id;
        _notesPendingFocusAtEnd = false;
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, DocumentId = _notesDocumentId, Block = block, AfterId = afterId });
    }

    private void ChangeFocusedBlockKind(string kind)
    {
        if (_notesFocusedBlockId is not { } id || !_noteRows.TryGetValue(id, out var row) || _notesDocumentId is null || !Meetings.CanEdit) return;
        if (row.Kind == kind) return;
        Meetings.Send(new MeetingOp
        {
            Type = MeetingOpTypes.BlockUpsert,
            DocumentId = _notesDocumentId,
            Block = new MeetingNoteBlock { Id = id, Kind = kind, Text = row.Box.Text, Done = row.Done },
        });
        _notesPendingFocus = id;
        _notesPendingFocusAtEnd = true;
    }

    private void FocusBlock(string? blockId, bool atEnd)
    {
        if (blockId is null || !_noteRows.TryGetValue(blockId, out var row)) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            row.Box.Focus(FocusState.Programmatic);
            row.Box.SelectionStart = atEnd ? row.Box.Text.Length : 0;
        });
    }

    private string? BlockIdAt(Func<List<string>, int, int> pick, string? relativeTo = null)
    {
        var ids = _notesBlocks?.Children.OfType<Grid>()
            .Select(child => _noteRows.Values.FirstOrDefault(row => ReferenceEquals(row.Root, child))?.Id)
            .Where(id => id is not null).Select(id => id!).ToList() ?? [];
        var index = pick(ids, relativeTo is null ? -1 : ids.IndexOf(relativeTo));
        return index >= 0 && index < ids.Count ? ids[index] : null;
    }

    private string? FirstBlockId() => BlockIdAt((ids, _) => 0);
    private string? LastBlockId() => BlockIdAt((ids, _) => ids.Count - 1);
    private string? PreviousBlockId(string id) => BlockIdAt((_, index) => index - 1, id);
    private string? NextBlockId(string id) => BlockIdAt((ids, index) => index < 0 ? -1 : index + 1, id);

    private sealed class NoteRow(string id, string kind, Grid root, TextBox box, Border accent, Border tag, CheckBox? check)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public Grid Root { get; } = root;
        public TextBox Box { get; } = box;
        public Border Accent { get; } = accent;
        public Border Tag { get; } = tag;
        public CheckBox? Check { get; } = check;
        public bool Dirty { get; set; }
        public bool Done { get; set; }
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? Debounce { get; set; }
        public MentionAutocomplete? Mention { get; set; }
        public string? AskedQuestion { get; set; }
    }
}
