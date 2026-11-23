using System.Text.Json;
using System.Text.Json.Serialization;

namespace Missum.App.Services.Meetings;

/// <summary>
/// Shared, serializable state of one Claude Meetings room. The host owns the
/// authoritative copy; every client (including the host's own window) keeps a
/// replica that is advanced with the same <see cref="MeetingRoom"/> reducer.
/// </summary>
public sealed class MeetingRoomState
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "Meeting";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Revision { get; set; }
    public bool IsLive { get; set; }
    public List<MeetingParticipant> Participants { get; set; } = [];
    public List<MeetingChatMessage> Chat { get; set; } = [];
    public List<MeetingTranscriptEntry> Transcript { get; set; } = [];
    public List<MeetingDocument> Documents { get; set; } = [];
    public MeetingWhiteboard Whiteboard { get; set; } = new();
    public MeetingAgentStatus Agent { get; set; } = new();
}

public sealed class MeetingParticipant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Gast";
    /// <summary>host, guest or agent.</summary>
    public string Role { get; set; } = "guest";
    public int ColorIndex { get; set; }
    public bool MicOn { get; set; }
    public bool CameraOn { get; set; }
    public bool Connected { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public bool IsAgent => Role == "agent";
}

public sealed record MeetingChatMessage(
    Guid Id,
    Guid AuthorId,
    string AuthorName,
    string Text,
    DateTimeOffset At,
    bool IsAgent = false);

public sealed record MeetingTranscriptEntry(
    Guid Id,
    Guid SpeakerId,
    string SpeakerName,
    string Text,
    DateTimeOffset At);

public sealed class MeetingDocument
{
    public string Id { get; set; } = NewId();
    public string Title { get; set; } = "Notizen";
    public List<MeetingNoteBlock> Blocks { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public sealed class MeetingNoteBlock
{
    public string Id { get; set; } = MeetingDocument.NewId();
    /// <summary>h1, h2, h3, p, bullet or todo.</summary>
    public string Kind { get; set; } = "p";
    public string Text { get; set; } = "";
    /// <summary>Optional AI section key such as ai.summary or ai.decisions.</summary>
    public string? Section { get; set; }
    public bool AiOwned { get; set; }
    /// <summary>True once a participant changed an AI block. The agent must keep it.</summary>
    public bool HumanEdited { get; set; }
    public bool Done { get; set; }
    public string? EditedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MeetingWhiteboard
{
    public List<WhiteboardColumn> Columns { get; set; } = [];
    public List<WhiteboardNote> Notes { get; set; } = [];
    /// <summary>Free elements on the canvas: text boxes, tables, images, shapes and arrows.</summary>
    public List<WhiteboardElement> Elements { get; set; } = [];
}

/// <summary>
/// A freely placed canvas element in the spirit of Prezi: text, table, image,
/// shape or arrow. Positions are canvas coordinates in device independent pixels.
/// </summary>
public sealed class WhiteboardElement
{
    public string Id { get; set; } = MeetingDocument.NewId();
    /// <summary>text, table, image, shape or arrow.</summary>
    public string Kind { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 120;
    public double Z { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 18;
    public List<List<string>>? Rows { get; set; }
    /// <summary>Base64 JPEG for images; stored as a file in the meeting's assets folder.</summary>
    public string? ImageData { get; set; }
    /// <summary>rect, ellipse or diamond for shapes.</summary>
    public string? Shape { get; set; }
    public int ColorIndex { get; set; }
    /// <summary>Source and target element or note id of an arrow.</summary>
    public string? FromId { get; set; }
    public string? ToId { get; set; }
    public string Author { get; set; } = "";
    public Guid? AuthorId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WhiteboardColumn
{
    public string Id { get; set; } = MeetingDocument.NewId();
    public string Title { get; set; } = "Sammlung";
    public string? Subtitle { get; set; }
    public bool IsAi { get; set; }
}

public sealed class WhiteboardNote
{
    public string Id { get; set; } = MeetingDocument.NewId();
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public Guid? AuthorId { get; set; }
    public int ColorIndex { get; set; }
    public string ColumnId { get; set; } = "";
    public double Order { get; set; }
    public bool IsAi { get; set; }
    public bool HumanEdited { get; set; }
    /// <summary>Reaction key (heart, plus, thumb, smile) to the names of the people who reacted.</summary>
    public Dictionary<string, List<string>> Reactions { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MeetingAgentStatus
{
    /// <summary>idle, listening, loading (model/service not ready), thinking, writing, done or error.</summary>
    public string State { get; set; } = "idle";
    public string Status { get; set; } = "Bereit";
    /// <summary>Estimated prompt tokens and the context limit of the last agent run.</summary>
    public int ContextUsed { get; set; }
    public int ContextLimit { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One state change. The record is deliberately flat so it serializes as a
/// small JSON object; <see cref="Type"/> decides which fields are meaningful.
/// </summary>
public sealed record MeetingOp
{
    public string Type { get; init; } = "";
    public long Revision { get; init; }
    public Guid ActorId { get; init; }
    public string? ActorName { get; init; }
    public Guid? ParticipantId { get; init; }
    public MeetingParticipant? Participant { get; init; }
    public MeetingChatMessage? Chat { get; init; }
    public MeetingTranscriptEntry? Transcript { get; init; }
    public string? DocumentId { get; init; }
    public MeetingDocument? Document { get; init; }
    public MeetingNoteBlock? Block { get; init; }
    public List<MeetingNoteBlock>? Blocks { get; init; }
    public string? BlockId { get; init; }
    public string? AfterId { get; init; }
    public WhiteboardNote? Note { get; init; }
    public string? NoteId { get; init; }
    public WhiteboardColumn? Column { get; init; }
    public string? ColumnId { get; init; }
    public double? Order { get; init; }
    public WhiteboardElement? Element { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public MeetingWhiteboard? Whiteboard { get; init; }
    public string? Reaction { get; init; }
    public string? Text { get; init; }
    public bool? Flag { get; init; }
    public MeetingAgentStatus? Agent { get; init; }
}

public static class MeetingOpExtensions
{
    /// <summary>Element operations address the element by <see cref="MeetingOp.Element"/> or, for move/delete, by <see cref="MeetingOp.NoteId"/>.</summary>
    public static string? ElementId(this MeetingOp op) => op.Element?.Id ?? op.NoteId;
}

public static class MeetingOpTypes
{
    public const string ParticipantJoin = "participant.join";
    public const string ParticipantLeave = "participant.leave";
    public const string ParticipantMedia = "participant.media";
    public const string ParticipantRename = "participant.rename";
    public const string ChatAdd = "chat.add";
    public const string TranscriptAdd = "transcript.add";
    public const string DocumentCreate = "doc.create";
    public const string DocumentRename = "doc.rename";
    public const string DocumentDelete = "doc.delete";
    public const string DocumentSetBlocks = "doc.setBlocks";
    public const string BlockUpsert = "block.upsert";
    public const string BlockDelete = "block.delete";
    public const string NoteUpsert = "wb.note.upsert";
    public const string NoteMove = "wb.note.move";
    public const string NoteDelete = "wb.note.delete";
    public const string NoteReact = "wb.note.react";
    public const string ColumnUpsert = "wb.column.upsert";
    public const string ColumnDelete = "wb.column.delete";
    public const string WhiteboardSet = "wb.set";
    public const string ElementUpsert = "wb.element.upsert";
    public const string ElementMove = "wb.element.move";
    public const string ElementDelete = "wb.element.delete";
    public const string AgentStatus = "agent.status";
    public const string AgentRequest = "agent.request";
    public const string RoomLive = "room.live";
    public const string RoomTitle = "room.title";
    public const string Presence = "presence";

    /// <summary>Operations only the host process itself may originate.</summary>
    public static bool IsHostOnly(string type) => type is ParticipantJoin or ParticipantLeave or TranscriptAdd
        or DocumentSetBlocks or WhiteboardSet or AgentStatus;
}

/// <summary>Wire envelope for text frames on the meeting WebSocket.</summary>
public sealed record MeetingEnvelope
{
    /// <summary>snapshot, op, error or ping.</summary>
    public string Kind { get; init; } = "op";
    public MeetingOp? Op { get; init; }
    public MeetingRoomState? State { get; init; }
    public Guid? You { get; init; }
    public string? Message { get; init; }
}

public static class MeetingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;
}

/// <summary>Binary frame layout: [kind:1][participant:16][payload].</summary>
public static class MeetingMediaFrame
{
    public const byte Video = 1;
    public const byte Audio = 2;
    public const int HeaderLength = 17;
    public const int MaximumVideoBytes = 512 * 1024;
    public const int MaximumAudioBytes = 64 * 1024;

    public static byte[] Create(byte kind, Guid participant, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderLength + payload.Length];
        frame[0] = kind;
        participant.TryWriteBytes(frame.AsSpan(1, 16));
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static bool TryRead(ReadOnlySpan<byte> frame, out byte kind, out Guid participant, out ReadOnlySpan<byte> payload)
    {
        kind = 0;
        participant = Guid.Empty;
        payload = default;
        if (frame.Length <= HeaderLength || frame[0] is not (Video or Audio)) return false;
        kind = frame[0];
        participant = new Guid(frame.Slice(1, 16));
        payload = frame[HeaderLength..];
        return payload.Length <= (kind == Video ? MaximumVideoBytes : MaximumAudioBytes);
    }

    /// <summary>Overwrites the sender id so a client can never speak for someone else.</summary>
    public static void Stamp(Span<byte> frame, Guid participant) => participant.TryWriteBytes(frame.Slice(1, 16));
}
