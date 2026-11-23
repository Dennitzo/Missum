namespace Missum.App.Services.Meetings;

/// <summary>One thing that happened in a meeting area since Missum's last pass.</summary>
public sealed record MeetingChange(string Area, string Key, string Actor, string Text, DateTimeOffset At, bool IsEdit);

/// <summary>
/// Turns meeting operations into short, readable change entries. Missum receives
/// them as shared context, so a change in one area (meeting, chat, speech, notes,
/// whiteboard) is carried over into all the others in the next pass.
/// </summary>
public static class MeetingChangeLog
{
    public const string Meeting = "Meeting";
    public const string Chat = "Chat";
    public const string Speech = "Gespräch";
    public const string Notes = "Notizen";
    public const string Whiteboard = "Whiteboard";
    public const int MaximumEntries = 80;

    /// <summary>Describes a human operation, or returns null for things that carry no content (presence, media, agent).</summary>
    public static MeetingChange? Describe(MeetingOp op, Func<string?, string?> documentTitle, Func<string?, string?> columnTitle, Func<string?, string?> noteText)
    {
        if (op.ActorId == MeetingRoom.AgentId) return null;
        var actor = string.IsNullOrWhiteSpace(op.ActorName) ? "Jemand" : op.ActorName!;
        var now = DateTimeOffset.UtcNow;
        MeetingChange Make(string area, string key, string text, bool edit) => new(area, key, actor, text, now, edit);
        switch (op.Type)
        {
            case MeetingOpTypes.ChatAdd when op.Chat is { } chat:
                return Make(Chat, "chat:" + chat.Id, $"schrieb im Chat: „{Short(chat.Text)}“", false);
            case MeetingOpTypes.TranscriptAdd when op.Transcript is { } entry:
                return new(Speech, "speech:" + entry.Id, entry.SpeakerName, $"sagte: „{Short(entry.Text)}“", now, false);
            case MeetingOpTypes.ParticipantJoin when op.Participant is { } joined:
                return new(Meeting, "join:" + joined.Id, joined.Name, "ist dem Meeting beigetreten", now, false);
            case MeetingOpTypes.ParticipantLeave:
                return Make(Meeting, "leave:" + op.ParticipantId, "hat das Meeting verlassen", false);
            case MeetingOpTypes.RoomLive:
                return Make(Meeting, "live", op.Flag == true ? "hat das Meeting gestartet" : "hat das Meeting beendet", false);
            case MeetingOpTypes.RoomTitle:
                return Make(Meeting, "title", $"nannte das Meeting „{Short(op.Text)}“", false);
            case MeetingOpTypes.BlockUpsert when op.Block is { } block:
                return Make(Notes, "block:" + block.Id,
                    $"schrieb in „{documentTitle(op.DocumentId) ?? "Notizen"}“: „{Short(block.Text)}“" + (block.Kind == "todo" && block.Done ? " (erledigt)" : ""), true);
            case MeetingOpTypes.BlockDelete:
                return Make(Notes, "block-delete:" + op.BlockId, $"löschte eine Zeile in „{documentTitle(op.DocumentId) ?? "Notizen"}“", true);
            case MeetingOpTypes.DocumentCreate when op.Document is { } document:
                return Make(Notes, "doc:" + document.Id, $"legte das Dokument „{Short(document.Title)}“ an", true);
            case MeetingOpTypes.DocumentRename:
                return Make(Notes, "doc:" + op.DocumentId, $"benannte ein Dokument in „{Short(op.Text)}“ um", true);
            case MeetingOpTypes.DocumentDelete:
                return Make(Notes, "doc-delete:" + op.DocumentId, "löschte ein Dokument", true);
            case MeetingOpTypes.NoteUpsert when op.Note is { } note:
                return Make(Whiteboard, "note:" + note.Id,
                    $"schrieb die Haftnotiz „{Short(note.Text)}“ in „{columnTitle(note.ColumnId) ?? "Sammlung"}“", true);
            case MeetingOpTypes.NoteMove:
                return Make(Whiteboard, "note:" + op.NoteId,
                    $"verschob „{Short(noteText(op.NoteId))}“ nach „{columnTitle(op.ColumnId) ?? "Spalte"}“", true);
            case MeetingOpTypes.NoteDelete:
                return Make(Whiteboard, "note-delete:" + op.NoteId, "löschte eine Haftnotiz", true);
            case MeetingOpTypes.NoteReact:
                return Make(Whiteboard, "react:" + op.NoteId + op.Reaction,
                    $"reagierte ({op.Reaction}) auf „{Short(noteText(op.NoteId))}“", true);
            case MeetingOpTypes.ColumnUpsert when op.Column is { } column:
                return Make(Whiteboard, "column:" + column.Id, $"bearbeitete die Spalte „{Short(column.Title)}“", true);
            case MeetingOpTypes.ColumnDelete:
                return Make(Whiteboard, "column-delete:" + op.ColumnId, "löschte eine Spalte", true);
            case MeetingOpTypes.ElementUpsert when op.Element is { } element:
                return Make(Whiteboard, "element:" + element.Id, ElementText(element), true);
            case MeetingOpTypes.ElementDelete:
                return Make(Whiteboard, "element-delete:" + op.ElementId(), "entfernte ein Element vom Whiteboard", true);
            default:
                // Moves on the canvas, presence and media carry no meaning for the content.
                return null;
        }
    }

    private static string ElementText(WhiteboardElement element) => element.Kind switch
    {
        "table" when element.Rows is { } rows => $"bearbeitete die Tabelle „{Short(string.Join(" | ", rows.Take(4).Select(r => string.Join(", ", r.Where(c => c.Length > 0)))))}“",
        "image" => "fügte ein Bild ein",
        "arrow" => "verband zwei Elemente mit einem Pfeil",
        "shape" => $"setzte eine Form „{Short(element.Text)}“",
        _ => $"schrieb das Textfeld „{Short(element.Text)}“",
    };

    /// <summary>Adds an entry; a newer edit of the same item replaces the older one.</summary>
    public static void Record(List<MeetingChange> log, MeetingChange change)
    {
        if (change.IsEdit) log.RemoveAll(c => c.Key == change.Key);
        log.Add(change);
        if (log.Count > MaximumEntries) log.RemoveRange(0, log.Count - MaximumEntries);
    }

    internal static string Short(string? text, int maximum = 160)
    {
        var value = (text ?? "").Replace('\n', ' ').Trim();
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }
}
