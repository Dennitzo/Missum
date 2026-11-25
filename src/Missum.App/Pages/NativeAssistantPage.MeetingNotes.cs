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
    private TextBlock? _notesTitle;
    private HyperlinkButton? _notesAddBlock;
    private FloatingToolbar? _notesToolbar;
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

        var page = new StackPanel { MaxWidth = 780, Spacing = 2, Padding = new Thickness(36, 28, 36, 120), HorizontalAlignment = HorizontalAlignment.Stretch };
        // The title is the session name: Missum keeps it current, only the host renames it in the sidebar.
        _notesTitle = new TextBlock
        {
            FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        };
        AutomationProperties.SetName(_notesTitle, "Titel der Sitzung");
        ToolTipService.SetToolTip(_notesTitle, "Name der Sitzung · Missum hält ihn aktuell, der Host benennt ihn in der Seitenleiste um");
        page.Children.Add(_notesTitle);
        _notesBlocks = new StackPanel { Spacing = 0 };
        page.Children.Add(_notesBlocks);
        _notesAddBlock = new HyperlinkButton
        {
            Content = new TextBlock { Text = "+ Absatz hinzufügen", FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 150) },
            Margin = new Thickness(-6, 10, 0, 0),
        };
        _notesAddBlock.Click += (_, _) => InsertBlockAfter(LastBlockId(), "p");
        page.Children.Add(_notesAddBlock);
        _notesScroll = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _notesRoot.Children.Add(_notesScroll);
        _notesToolbar = new FloatingToolbar("Werkzeuge der Notizen", NotesToolbar(), "\uE70F", "Notizen");
        _notesRoot.Children.Add(_notesToolbar);
        return _notesRoot;
    }

    private HyperlinkButton LinkButton(string text, Action onClick)
    {
        var link = new HyperlinkButton { Content = new TextBlock { Text = text, FontSize = 13 }, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        link.Click += (_, _) => RunUiCallback("Notes.Link", onClick);
        return link;
    }

    /// <summary>Block kinds and the compact "Zusammenfassen" in the floating toolbar.</summary>
    private StackPanel NotesToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (kind, label, glyph) in new[]
                 {
                     ("p", "Text", "\uE8D2"), ("h2", "Überschrift", "\uE8E9"), ("bullet", "Liste", "\uE8FD"), ("todo", "Aufgabe", "\uE73A"),
                 })
        {
            var button = FloatingToolbar.Tool(glyph, label, $"Aktuellen Block in „{label}“ umwandeln", () => RunUiCallback("Notes.Kind", () => ChangeFocusedBlockKind(kind)));
            button.IsTabStop = false;
            button.AllowFocusOnInteraction = false;
            bar.Children.Add(button);
        }
        bar.Children.Add(FloatingToolbar.Separator());
        bar.Children.Add(FloatingToolbar.Tool("", "Zusammenfassen", "Missum fasst die Notizen zusammen",
            () => RunUiCallback("Notes.Summarize", () => Meetings.RequestAgent("summarize")),
            new MeetingAgentVisual { Height = 16, Width = 16 }));
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
            var title = MeetingSessionTitle(snapshot);
            if (_notesTitle!.Text != title) _notesTitle.Text = title;
            if (canEdit) PruneEmptyBlocks(document, presence);

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
        // At most one empty field is offered: the add link appears only when none exists.
        if (_notesAddBlock is not null)
            _notesAddBlock.Visibility = canEdit && !document.Blocks.Any(IsEmptyBlock) ? Visibility.Visible : Visibility.Collapsed;

        if (_notesPendingFocus is { } focus && _noteRows.ContainsKey(focus))
        {
            _notesPendingFocus = null;
            FocusBlock(focus, _notesPendingFocusAtEnd);
        }
    }

    private static bool IsEmptyBlock(MeetingNoteBlock block) => string.IsNullOrWhiteSpace(block.Text) && !block.AiOwned;

    /// <summary>
    /// Empty fields nobody is in disappear, so the notes never fill up with blank lines. One empty
    /// field stays when the document would otherwise be empty, or while someone is typing in it.
    /// </summary>
    private void PruneEmptyBlocks(MeetingDocument document, IReadOnlyDictionary<Guid, (string? DocumentId, string? BlockId)> presence)
    {
        var occupied = presence.Values.Where(value => value.DocumentId == document.Id).Select(value => value.BlockId).ToHashSet(StringComparer.Ordinal);
        if (_notesFocusedBlockId is { } focused) occupied.Add(focused);
        if (_notesPendingFocus is { } pending) occupied.Add(pending);
        var empties = document.Blocks.Where(IsEmptyBlock).ToList();
        var keep = empties.FirstOrDefault(block => occupied.Contains(block.Id))
            ?? (document.Blocks.Count == empties.Count ? empties.LastOrDefault() : null);
        foreach (var block in empties.Where(block => !ReferenceEquals(block, keep)))
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.BlockDelete, DocumentId = document.Id, BlockId = block.Id });
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
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The author bar: one segment per author in their colour; hovering the block names them.
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
        var row = new NoteRow(block.Id, block.Kind, root, box, accent, check);
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
            if (_notesFocusedBlockId == row.Id) _notesFocusedBlockId = null;
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.Presence });
            MarkMeetingsDirty(notes: true);
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
        // Someone typing right now shows in the bar before the text arrives.
        var editor = presence.FirstOrDefault(p => p.Key != Meetings.LocalParticipantId && p.Value.BlockId == block.Id && p.Value.DocumentId == _notesDocumentId).Key;
        row.AiOwned = block.AiOwned;
        RenderBlockAuthors(row, block, snapshot, editor);
    }

    /// <summary>
    /// The coloured bar in front of a block replaces an author column: one segment per author in
    /// the participant's colour (Missum in its own), and hovering the block shows nickname and time.
    /// </summary>
    private static void RenderBlockAuthors(NoteRow row, MeetingNoteBlock block, MeetingRoomState snapshot, Guid liveEditor)
    {
        var authors = NoteBlockAuthors(block, snapshot);
        var live = liveEditor == Guid.Empty || authors.Any(a => a.Author.Id == liveEditor) ? null : snapshot.Participants.FirstOrDefault(p => p.Id == liveEditor);
        var signature = string.Join(",", authors.Select(a => a.Author.Id + ":" + a.Author.Name + ":" + a.Author.ColorIndex + ":" + a.At?.ToUnixTimeSeconds())) + "|" + live?.Id;
        if (row.AuthorSignature == signature) return;
        row.AuthorSignature = signature;
        var colours = authors.Select(a => AuthorBrush(a.Author)).ToList();
        if (live is not null) colours.Add(ParticipantBrush(live.ColorIndex));
        if (colours.Count == 0) { row.Accent.Child = null; row.Accent.Background = null; }
        else if (colours.Count == 1) { row.Accent.Child = null; row.Accent.Background = colours[0]; }
        else
        {
            row.Accent.Background = null;
            var bar = new Grid { RowSpacing = 1 };
            for (var i = 0; i < colours.Count; i++)
            {
                bar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var segment = new Border { Background = colours[i], CornerRadius = new CornerRadius(2) };
                Grid.SetRow(segment, i);
                bar.Children.Add(segment);
            }
            row.Accent.Child = bar;
        }
        var lines = authors.Select(a => a.At is { } at ? a.Author.Name + " · " + at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture) : a.Author.Name).ToList();
        if (live is not null) lines.Add(live.Name + " · schreibt gerade");
        var tip = lines.Count == 0 ? null : string.Join("\n", lines);
        row.AuthorTooltip = tip;
        ToolTipService.SetToolTip(row.Root, tip);
        ToolTipService.SetToolTip(row.Box, tip);
    }

    private static SolidColorBrush AuthorBrush(MeetingParticipant author) =>
        author.IsAgent ? MeetingAgentVisual.AccentBrush : ParticipantBrush(author.ColorIndex);

    /// <summary>Authors of a block in writing order, each with the time they last wrote into it.</summary>
    internal static List<(MeetingParticipant Author, DateTimeOffset? At)> NoteBlockAuthors(MeetingNoteBlock block, MeetingRoomState snapshot)
    {
        var ids = new List<Guid>(block.Authors);
        if (block.AiOwned && !ids.Contains(MeetingRoom.AgentId)) ids.Insert(0, MeetingRoom.AgentId);
        var result = new List<(MeetingParticipant, DateTimeOffset?)>();
        foreach (var id in ids.Distinct())
        {
            var participant = snapshot.Participants.FirstOrDefault(p => p.Id == id);
            if (participant is null && id == MeetingRoom.AgentId)
                participant = new MeetingParticipant { Id = id, Name = MeetingRoom.AgentName, Role = "agent", ColorIndex = 7 };
            if (participant is null) continue;
            DateTimeOffset? at = block.AuthorTimes.TryGetValue(id, out var stamp) ? stamp
                : ids.Count == 1 || id == MeetingRoom.AgentId ? block.UpdatedAt : null;
            result.Add((participant, at));
        }
        // Older blocks only know the last editor's name.
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(block.EditedBy) && !string.IsNullOrWhiteSpace(block.Text))
            result.Add((snapshot.Participants.FirstOrDefault(p => p.Name == block.EditedBy) ?? new MeetingParticipant { Name = block.EditedBy!, ColorIndex = 0 }, block.UpdatedAt));
        return result;
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
            if (string.IsNullOrWhiteSpace(row.Box.Text)) return;
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
        var existing = _noteRows.Values.FirstOrDefault(row => row.Id != afterId && string.IsNullOrWhiteSpace(row.Box.Text) && !row.AiOwned);
        if (existing is not null) { FocusBlock(existing.Id, atEnd: false); return; }
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

    private sealed class NoteRow(string id, string kind, Grid root, TextBox box, Border accent, CheckBox? check)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public Grid Root { get; } = root;
        public TextBox Box { get; } = box;
        public Border Accent { get; } = accent;
        public string? AuthorTooltip { get; set; }
        public CheckBox? Check { get; } = check;
        public bool Dirty { get; set; }
        public bool Done { get; set; }
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? Debounce { get; set; }
        public MentionAutocomplete? Mention { get; set; }
        public string? AskedQuestion { get; set; }
        public string? AuthorSignature { get; set; }
        public bool AiOwned { get; set; }
    }
}
