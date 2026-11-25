namespace Missum.App.Services.Meetings;

/// <summary>
/// The single reducer for meeting state. The host validates and applies an
/// operation, stamps a revision and forwards the very same operation to every
/// client, which applies it with this class again. Keeping one reducer keeps
/// all replicas identical without a second synchronization protocol.
/// </summary>
public sealed class MeetingRoom
{
    public static readonly Guid AgentId = new("c1a0de00-0000-4000-8000-00000000c1de");
    public const string AgentName = "Missum";
    public const int MaximumChat = 2_000;
    public const int MaximumTranscript = 5_000;
    public const int MaximumTextLength = 4_000;
    public const int MaximumBlocksPerDocument = 800;
    public const int MaximumNotes = 600;
    public const int MaximumColumns = 24;
    public const int MaximumDocuments = 40;
    public const int MaximumElements = 300;
    public const int MaximumImages = 24;
    public const int MaximumImageBase64Length = 800 * 1024;
    public const int MaximumTableRows = 30;
    public const int MaximumTableColumns = 12;
    public const double CanvasWidth = 6000;
    public const double CanvasHeight = 4000;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string? DocumentId, string? BlockId)> _presence = [];

    public MeetingRoom(MeetingRoomState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Normalize(State);
    }

    public MeetingRoomState State { get; private set; }

    /// <summary>Locks the replica for reads that must observe one consistent state.</summary>
    public T Read<T>(Func<MeetingRoomState, T> reader)
    {
        lock (_gate) return reader(State);
    }

    public MeetingRoomState Snapshot()
    {
        lock (_gate) return MeetingJson.Clone(State);
    }

    public void Replace(MeetingRoomState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            Normalize(state);
            State = state;
            _presence.Clear();
        }
    }

    public IReadOnlyDictionary<Guid, (string? DocumentId, string? BlockId)> Presence()
    {
        lock (_gate) return new Dictionary<Guid, (string?, string?)>(_presence);
    }

    /// <summary>
    /// Applies <paramref name="op"/>. Returns false when the operation is invalid
    /// or changes nothing; the host then neither stores nor forwards it.
    /// </summary>
    public bool Apply(MeetingOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        lock (_gate)
        {
            var changed = ApplyCore(op);
            if (changed && op.Type != MeetingOpTypes.Presence)
                State.Revision = Math.Max(State.Revision + 1, op.Revision);
            return changed;
        }
    }

    private bool ApplyCore(MeetingOp op)
    {
        var state = State;
        var now = DateTimeOffset.UtcNow;
        var human = op.ActorId != AgentId;
        switch (op.Type)
        {
            case MeetingOpTypes.ParticipantJoin:
            {
                if (op.Participant is not { } incoming || incoming.Id == Guid.Empty) return false;
                var copy = MeetingJson.Clone(incoming);
                copy.Name = Clip(copy.Name, 60, "Gast");
                copy.Connected = true;
                var index = state.Participants.FindIndex(p => p.Id == copy.Id);
                if (index >= 0)
                {
                    copy.ColorIndex = state.Participants[index].ColorIndex;
                    copy.JoinedAt = state.Participants[index].JoinedAt;
                    state.Participants[index] = copy;
                }
                else
                {
                    copy.ColorIndex = NextColor(state);
                    state.Participants.Add(copy);
                }
                return true;
            }
            case MeetingOpTypes.ParticipantLeave:
            {
                var participant = state.Participants.FirstOrDefault(p => p.Id == op.ParticipantId);
                if (participant is null || !participant.Connected) return false;
                participant.Connected = false;
                participant.MicOn = false;
                participant.CameraOn = false;
                _presence.Remove(participant.Id);
                return true;
            }
            case MeetingOpTypes.ParticipantMedia:
            {
                var participant = state.Participants.FirstOrDefault(p => p.Id == op.ActorId);
                if (participant is null || op.Participant is null) return false;
                if (participant.MicOn == op.Participant.MicOn && participant.CameraOn == op.Participant.CameraOn) return false;
                participant.MicOn = op.Participant.MicOn;
                participant.CameraOn = op.Participant.CameraOn;
                return true;
            }
            case MeetingOpTypes.ParticipantRename:
            {
                var participant = state.Participants.FirstOrDefault(p => p.Id == op.ActorId);
                var name = Clip(op.Text, 60, "");
                if (participant is null || participant.IsAgent || name.Length == 0 || participant.Name == name) return false;
                participant.Name = name;
                return true;
            }
            case MeetingOpTypes.ChatAdd:
            {
                if (op.Chat is not { } chat || string.IsNullOrWhiteSpace(chat.Text)) return false;
                if (state.Chat.Any(item => item.Id == chat.Id)) return false;
                var author = state.Participants.FirstOrDefault(p => p.Id == op.ActorId);
                state.Chat.Add(chat with
                {
                    AuthorId = op.ActorId,
                    AuthorName = op.ActorId == AgentId ? AgentName : author?.Name ?? Clip(chat.AuthorName, 60, "Gast"),
                    Text = Clip(chat.Text, MaximumTextLength, ""),
                    IsAgent = op.ActorId == AgentId,
                });
                Trim(state.Chat, MaximumChat);
                return true;
            }
            case MeetingOpTypes.TranscriptAdd:
            {
                if (op.Transcript is not { } entry || string.IsNullOrWhiteSpace(entry.Text)) return false;
                if (state.Transcript.Any(item => item.Id == entry.Id)) return false;
                state.Transcript.Add(entry with { Text = Clip(entry.Text, MaximumTextLength, "") });
                Trim(state.Transcript, MaximumTranscript);
                return true;
            }
            case MeetingOpTypes.DocumentCreate:
            {
                // A meeting has exactly one notes document.
                if (state.Documents.Count >= 1) return false;
                if (op.Document is not { } document) return false;
                if (state.Documents.Any(d => d.Id == document.Id)) return false;
                var copy = MeetingJson.Clone(document);
                copy.Title = Clip(copy.Title, 120, "Notizen");
                copy.Blocks = copy.Blocks.Take(MaximumBlocksPerDocument).ToList();
                foreach (var block in copy.Blocks) SanitizeBlock(block);
                copy.UpdatedAt = now;
                state.Documents.Add(copy);
                return true;
            }
            case MeetingOpTypes.DocumentRename:
            {
                var document = FindDocument(op.DocumentId);
                var title = Clip(op.Text, 120, "");
                if (document is null || title.Length == 0 || document.Title == title) return false;
                document.Title = title;
                document.UpdatedAt = now;
                return true;
            }
            case MeetingOpTypes.DocumentDelete:
            {
                // The first document holds Missum's live notes and stays.
                var index = state.Documents.FindIndex(d => d.Id == op.DocumentId);
                if (index <= 0) return false;
                state.Documents.RemoveAt(index);
                return true;
            }
            case MeetingOpTypes.DocumentSetBlocks:
            {
                var document = FindDocument(op.DocumentId);
                if (document is null || op.Blocks is null) return false;
                document.Blocks = op.Blocks.Take(MaximumBlocksPerDocument).Select(MeetingJson.Clone).ToList();
                foreach (var block in document.Blocks) SanitizeBlock(block);
                document.UpdatedAt = now;
                return true;
            }
            case MeetingOpTypes.BlockUpsert:
            {
                var document = FindDocument(op.DocumentId);
                if (document is null || op.Block is not { } incoming || string.IsNullOrWhiteSpace(incoming.Id)) return false;
                var block = MeetingJson.Clone(incoming);
                SanitizeBlock(block);
                block.UpdatedAt = now;
                var index = document.Blocks.FindIndex(b => b.Id == block.Id);
                if (index >= 0)
                {
                    var existing = document.Blocks[index];
                    if (existing.Text == block.Text && existing.Kind == block.Kind && existing.Done == block.Done) return false;
                    // Ownership never changes through an edit; a human edit pins the block.
                    block.AiOwned = existing.AiOwned;
                    block.Section = existing.Section;
                    block.HumanEdited = existing.HumanEdited || (human && existing.AiOwned);
                    block.EditedBy = human ? Clip(op.ActorName, 60, existing.EditedBy ?? "") : existing.EditedBy;
                    block.Authors = [.. existing.Authors];
                    if (human && op.ActorId != Guid.Empty && existing.Text != block.Text && !string.IsNullOrWhiteSpace(block.Text)
                        && !block.Authors.Contains(op.ActorId) && block.Authors.Count < 32)
                        block.Authors.Add(op.ActorId);
                    block.AuthorTimes = new Dictionary<Guid, DateTimeOffset>(existing.AuthorTimes);
                    if (existing.Text != block.Text) StampAuthor(block, op.ActorId, now);
                    document.Blocks[index] = block;
                }
                else
                {
                    if (document.Blocks.Count >= MaximumBlocksPerDocument) return false;
                    if (human) { block.AiOwned = false; block.Section = null; block.HumanEdited = false; }
                    block.EditedBy = human ? Clip(op.ActorName, 60, "") : null;
                    block.Authors = human && op.ActorId != Guid.Empty && !string.IsNullOrWhiteSpace(block.Text) ? [op.ActorId] : [];
                    block.AuthorTimes = [];
                    StampAuthor(block, op.ActorId, now);
                    var after = op.AfterId is null ? -1 : document.Blocks.FindIndex(b => b.Id == op.AfterId);
                    if (after >= 0) document.Blocks.Insert(after + 1, block);
                    else if (op.AfterId == "") document.Blocks.Insert(0, block);
                    else document.Blocks.Add(block);
                }
                document.UpdatedAt = now;
                return true;
            }
            case MeetingOpTypes.BlockDelete:
            {
                var document = FindDocument(op.DocumentId);
                if (document is null) return false;
                var removed = document.Blocks.RemoveAll(b => b.Id == op.BlockId);
                if (removed == 0) return false;
                document.UpdatedAt = now;
                return true;
            }
            case MeetingOpTypes.NoteUpsert:
            {
                if (op.Note is not { } incoming || string.IsNullOrWhiteSpace(incoming.Id)) return false;
                var note = MeetingJson.Clone(incoming);
                note.Text = Clip(note.Text, 600, "");
                note.UpdatedAt = now;
                var board = state.Whiteboard;
                if (board.Columns.All(c => c.Id != note.ColumnId))
                    note.ColumnId = EnsureFirstColumn(board).Id;
                var index = board.Notes.FindIndex(n => n.Id == note.Id);
                if (index >= 0)
                {
                    var existing = board.Notes[index];
                    if (existing.Text == note.Text && existing.ColumnId == note.ColumnId && existing.ColorIndex == note.ColorIndex) return false;
                    note.IsAi = existing.IsAi;
                    note.Author = existing.Author;
                    note.AuthorId = existing.AuthorId;
                    note.Reactions = existing.Reactions;
                    note.HumanEdited = existing.HumanEdited || (human && existing.IsAi && existing.Text != note.Text);
                    board.Notes[index] = note;
                }
                else
                {
                    if (board.Notes.Count >= MaximumNotes) return false;
                    if (human)
                    {
                        note.IsAi = false;
                        note.AuthorId = op.ActorId;
                        note.Author = Clip(op.ActorName, 60, "Gast");
                        note.ColorIndex = state.Participants.FirstOrDefault(p => p.Id == op.ActorId)?.ColorIndex ?? note.ColorIndex;
                    }
                    if (note.Order == 0)
                        note.Order = board.Notes.Where(n => n.ColumnId == note.ColumnId).Select(n => n.Order).DefaultIfEmpty(0).Max() + 1;
                    board.Notes.Add(note);
                }
                return true;
            }
            case MeetingOpTypes.NoteMove:
            {
                var board = state.Whiteboard;
                var note = board.Notes.FirstOrDefault(n => n.Id == op.NoteId);
                if (note is null || board.Columns.All(c => c.Id != op.ColumnId)) return false;
                var order = op.Order ?? board.Notes.Where(n => n.ColumnId == op.ColumnId).Select(n => n.Order).DefaultIfEmpty(0).Max() + 1;
                if (note.ColumnId == op.ColumnId && note.Order.Equals(order)) return false;
                note.ColumnId = op.ColumnId!;
                note.Order = order;
                note.UpdatedAt = now;
                return true;
            }
            case MeetingOpTypes.NoteDelete:
            {
                if (state.Whiteboard.Notes.RemoveAll(n => n.Id == op.NoteId) == 0) return false;
                state.Whiteboard.Elements.RemoveAll(e => e.Kind == "arrow" && (e.FromId == op.NoteId || e.ToId == op.NoteId));
                return true;
            }
            case MeetingOpTypes.NoteReact:
            {
                var note = state.Whiteboard.Notes.FirstOrDefault(n => n.Id == op.NoteId);
                if (note is null || op.Reaction is not ("heart" or "plus" or "thumb" or "smile")) return false;
                var name = Clip(op.ActorName, 60, "Gast");
                if (!note.Reactions.TryGetValue(op.Reaction, out var names)) note.Reactions[op.Reaction] = names = [];
                if (!names.Remove(name)) names.Add(name);
                if (names.Count == 0) note.Reactions.Remove(op.Reaction);
                return true;
            }
            case MeetingOpTypes.ColumnUpsert:
            {
                if (op.Column is not { } incoming || string.IsNullOrWhiteSpace(incoming.Id)) return false;
                var board = state.Whiteboard;
                var column = MeetingJson.Clone(incoming);
                column.Title = Clip(column.Title, 80, "Spalte");
                column.Subtitle = string.IsNullOrWhiteSpace(column.Subtitle) ? null : Clip(column.Subtitle, 140, "");
                var index = board.Columns.FindIndex(c => c.Id == column.Id);
                if (index >= 0)
                {
                    var existing = board.Columns[index];
                    if (existing.Title == column.Title && existing.Subtitle == column.Subtitle) return false;
                    column.IsAi = existing.IsAi && !human;
                    board.Columns[index] = column;
                }
                else
                {
                    if (board.Columns.Count >= MaximumColumns) return false;
                    if (human) column.IsAi = false;
                    var after = op.AfterId is null ? -1 : board.Columns.FindIndex(c => c.Id == op.AfterId);
                    if (after >= 0) board.Columns.Insert(after + 1, column);
                    else board.Columns.Add(column);
                }
                return true;
            }
            case MeetingOpTypes.ColumnDelete:
            {
                var board = state.Whiteboard;
                if (board.Columns.Count <= 1) return false;
                var index = board.Columns.FindIndex(c => c.Id == op.ColumnId);
                if (index < 0) return false;
                board.Columns.RemoveAt(index);
                var target = board.Columns[0].Id;
                // Notes are never lost with their column; they fall back to the first one.
                foreach (var note in board.Notes.Where(n => n.ColumnId == op.ColumnId)) note.ColumnId = target;
                return true;
            }
            case MeetingOpTypes.ElementUpsert:
            {
                if (op.Element is not { } incoming || string.IsNullOrWhiteSpace(incoming.Id)) return false;
                var elements = state.Whiteboard.Elements;
                var element = MeetingJson.Clone(incoming);
                if (!SanitizeElement(element, elements)) return false;
                element.UpdatedAt = now;
                var index = elements.FindIndex(e => e.Id == element.Id);
                if (index >= 0)
                {
                    var existing = elements[index];
                    element.Author = existing.Author;
                    element.AuthorId = existing.AuthorId;
                    // Images are immutable after insertion; an edit without data keeps the picture.
                    if (element.Kind == "image" && string.IsNullOrEmpty(element.ImageData)) element.ImageData = existing.ImageData;
                    elements[index] = element;
                }
                else
                {
                    if (elements.Count >= MaximumElements) return false;
                    if (element.Kind == "image" && elements.Count(e => e.Kind == "image") >= MaximumImages) return false;
                    element.Author = Clip(op.ActorName, 60, human ? "Gast" : AgentName);
                    element.AuthorId = op.ActorId;
                    if (element.Z == 0) element.Z = elements.Select(e => e.Z).DefaultIfEmpty(0).Max() + 1;
                    elements.Add(element);
                }
                return true;
            }
            case MeetingOpTypes.ElementMove:
            {
                var element = state.Whiteboard.Elements.FirstOrDefault(e => e.Id == op.ElementId());
                if (element is null || element.Kind == "arrow") return false;
                var x = Math.Clamp(op.X ?? element.X, 0, CanvasWidth - 40);
                var y = Math.Clamp(op.Y ?? element.Y, 0, CanvasHeight - 40);
                var width = Math.Clamp(op.Width ?? element.Width, 40, 2400);
                var height = Math.Clamp(op.Height ?? element.Height, 30, 2400);
                if (x.Equals(element.X) && y.Equals(element.Y) && width.Equals(element.Width) && height.Equals(element.Height)) return false;
                (element.X, element.Y, element.Width, element.Height) = (x, y, width, height);
                element.UpdatedAt = now;
                return true;
            }
            case MeetingOpTypes.ElementDelete:
            {
                var id = op.ElementId();
                var elements = state.Whiteboard.Elements;
                if (elements.RemoveAll(e => e.Id == id) == 0) return false;
                // Arrows never point into nothing.
                elements.RemoveAll(e => e.Kind == "arrow" && (e.FromId == id || e.ToId == id));
                return true;
            }
            case MeetingOpTypes.WhiteboardSet:
            {
                if (op.Whiteboard is null) return false;
                var board = MeetingJson.Clone(op.Whiteboard);
                // Free canvas elements belong to people; an agent update never replaces them.
                board.Elements = state.Whiteboard.Elements;
                board.Columns = board.Columns.Take(MaximumColumns).ToList();
                board.Notes = board.Notes.Take(MaximumNotes).ToList();
                EnsureFirstColumn(board);
                var columnIds = board.Columns.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var note in board.Notes.Where(n => !columnIds.Contains(n.ColumnId))) note.ColumnId = board.Columns[0].Id;
                state.Whiteboard = board;
                return true;
            }
            case MeetingOpTypes.AgentStatus:
            {
                if (op.Agent is null) return false;
                state.Agent = new MeetingAgentStatus
                {
                    State = op.Agent.State is "idle" or "listening" or "loading" or "thinking" or "writing" or "done" or "error" ? op.Agent.State : "idle",
                    Status = Clip(op.Agent.Status, 120, "Bereit"),
                    // A status update without usage keeps the last known context numbers.
                    ContextUsed = op.Agent.ContextLimit > 0 ? Math.Max(0, op.Agent.ContextUsed) : state.Agent.ContextUsed,
                    ContextLimit = op.Agent.ContextLimit > 0 ? op.Agent.ContextLimit : state.Agent.ContextLimit,
                    UpdatedAt = now,
                };
                return true;
            }
            case MeetingOpTypes.RoomLive:
            {
                var live = op.Flag == true;
                if (state.IsLive == live) return false;
                state.IsLive = live;
                return true;
            }
            case MeetingOpTypes.RoomTitle:
            {
                // Missum names the meeting until the host renames it by hand; then the title is the host's.
                var title = Clip(op.Text, 120, "");
                if (title.Length == 0) return false;
                // A placeholder such as "Neue Sitzung" never counts as the host's name for the meeting.
                if (!human && state.TitleLocked && !IsPlaceholderTitle(state.Title)) return false;
                if (!human && IsPlaceholderTitle(title)) return false;
                if (human && !IsHost(op.ActorId)) return false;
                var locks = human && !IsPlaceholderTitle(title);
                var changed = title != state.Title || locks != state.TitleLocked;
                state.Title = title;
                state.TitleLocked = locks;
                foreach (var document in state.Documents) document.Title = title;
                return changed;
            }
            case MeetingOpTypes.Presence:
            {
                if (op.ActorId == Guid.Empty) return false;
                if (op.BlockId is null && op.DocumentId is null) return _presence.Remove(op.ActorId);
                _presence[op.ActorId] = (op.DocumentId, op.BlockId);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Default names that say nothing about the meeting; Missum may always replace them.</summary>
    public static bool IsPlaceholderTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || title.Trim() is "Neue Sitzung" or "Meeting" or "Neues Meeting" or "Claude Meetings";

    private MeetingDocument? FindDocument(string? id) =>
        id is null ? null : State.Documents.FirstOrDefault(d => d.Id == id);

    private static WhiteboardColumn EnsureFirstColumn(MeetingWhiteboard board)
    {
        if (board.Columns.Count > 0) return board.Columns[0];
        var column = new WhiteboardColumn { Title = "Sammlung", Subtitle = "Neue Ideen landen hier" };
        board.Columns.Add(column);
        return column;
    }

    private bool SanitizeElement(WhiteboardElement element, List<WhiteboardElement> elements)
    {
        if (element.Kind is not ("text" or "table" or "image" or "shape" or "arrow")) return false;
        element.Text = Clip(element.Text, MaximumTextLength, "");
        element.FontSize = Math.Clamp(element.FontSize <= 0 ? 18 : element.FontSize, 10, 72);
        element.X = Math.Clamp(element.X, 0, CanvasWidth - 40);
        element.Y = Math.Clamp(element.Y, 0, CanvasHeight - 40);
        element.Width = Math.Clamp(element.Width, 40, 2400);
        element.Height = Math.Clamp(element.Height, 30, 2400);
        element.ColorIndex = Math.Abs(element.ColorIndex) % 8;
        switch (element.Kind)
        {
            case "table":
                var rows = (element.Rows ?? [["", ""], ["", ""]]).Take(MaximumTableRows).Select(r => (r ?? []).Take(MaximumTableColumns).Select(c => Clip(c, 500, "")).ToList()).ToList();
                if (rows.Count == 0) rows.Add([""]);
                var columns = Math.Max(1, rows.Max(r => r.Count));
                foreach (var row in rows) while (row.Count < columns) row.Add("");
                element.Rows = rows;
                break;
            case "image":
                if (element.ImageData is { Length: > MaximumImageBase64Length }) return false;
                break;
            case "shape":
                element.Shape = element.Shape is "ellipse" or "diamond" ? element.Shape : "rect";
                break;
            case "arrow":
                if (element.FromId is null || element.ToId is null || element.FromId == element.ToId) return false;
                bool Exists(string id) => elements.Any(e => e.Id == id && e.Kind != "arrow") || State.Whiteboard.Notes.Any(n => n.Id == id);
                if (!Exists(element.FromId) || !Exists(element.ToId)) return false;
                break;
        }
        if (element.Kind != "table") element.Rows = null;
        if (element.Kind != "image") element.ImageData = null;
        return true;
    }

    /// <summary>Records when someone (or Missum) last wrote text into a block.</summary>
    private static void StampAuthor(MeetingNoteBlock block, Guid actor, DateTimeOffset now)
    {
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(block.Text)) return;
        if (!block.AuthorTimes.ContainsKey(actor) && block.AuthorTimes.Count >= 32) return;
        block.AuthorTimes[actor] = now;
    }

    private static void SanitizeBlock(MeetingNoteBlock block)
    {
        block.Kind = block.Kind is "h1" or "h2" or "h3" or "p" or "bullet" or "todo" ? block.Kind : "p";
        block.Text = Clip(block.Text, MaximumTextLength, "");
        if (string.IsNullOrWhiteSpace(block.Id)) block.Id = MeetingDocument.NewId();
    }

    private static int NextColor(MeetingRoomState state)
    {
        var used = state.Participants.Select(p => p.ColorIndex).ToHashSet();
        for (var index = 0; index < 8; index++)
            if (!used.Contains(index)) return index;
        return state.Participants.Count % 8;
    }

    private static void Trim<T>(List<T> items, int maximum)
    {
        if (items.Count > maximum) items.RemoveRange(0, items.Count - maximum);
    }

    internal static string Clip(string? value, int maximum, string fallback)
    {
        var text = (value ?? "").Replace("\0", "", StringComparison.Ordinal).Trim();
        if (text.Length == 0) return fallback;
        return text.Length <= maximum ? text : text[..maximum];
    }

    /// <summary>Gives a fresh or loaded state its required defaults.</summary>
    public static void Normalize(MeetingRoomState state)
    {
        state.Participants ??= [];
        state.Chat ??= [];
        state.Transcript ??= [];
        state.Documents ??= [];
        state.Whiteboard ??= new MeetingWhiteboard();
        state.Whiteboard.Columns ??= [];
        state.Whiteboard.Notes ??= [];
        state.Whiteboard.Elements ??= [];
        state.Agent ??= new MeetingAgentStatus();
        foreach (var note in state.Whiteboard.Notes) note.Reactions ??= new(StringComparer.Ordinal);
        foreach (var document in state.Documents) document.Blocks ??= [];
        if (state.Documents.Count > 1)
        {
            // Meetings once allowed several documents; keep their content as sections of the one document.
            var first = state.Documents[0];
            foreach (var extra in state.Documents.Skip(1))
            {
                if (extra.Blocks.All(b => string.IsNullOrWhiteSpace(b.Text))) continue;
                first.Blocks.Add(new MeetingNoteBlock { Kind = "h2", Text = extra.Title });
                first.Blocks.AddRange(extra.Blocks.Where(b => b.Kind != "h1" || b.Text != extra.Title));
            }
            state.Documents.RemoveRange(1, state.Documents.Count - 1);
        }
        // The notes page shows the session title above the blocks; a heading block with the same
        // title would only repeat it (older meetings started with one).
        if (state.Documents.Count > 0 && state.Documents[0].Blocks.Count > 0 && state.Documents[0].Blocks[0] is { Kind: "h1" } heading
            && (heading.Text == state.Title || heading.Text == state.Documents[0].Title))
            state.Documents[0].Blocks.RemoveAt(0);
        if (state.Documents.Count == 0)
            state.Documents.Add(new MeetingDocument
            {
                Title = state.Title,
                Blocks = [new MeetingNoteBlock { Kind = "p", Text = "" }],
            });
        EnsureFirstColumn(state.Whiteboard);
        if (state.Participants.All(p => p.Id != AgentId))
            state.Participants.Insert(0, new MeetingParticipant
            {
                Id = AgentId,
                Name = AgentName,
                Role = "agent",
                ColorIndex = 7,
                Connected = true,
            });
    }

    private bool IsHost(Guid actorId) => State.Participants.Any(p => p.Id == actorId && p.Role == "host");
}
