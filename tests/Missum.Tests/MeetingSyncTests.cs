using Missum.App.Services.Meetings;

namespace Missum.Tests;

public sealed class MeetingSyncTests
{
    private static readonly Guid Ines = Guid.NewGuid();

    private static MeetingChange? Describe(MeetingOp op) =>
        MeetingChangeLog.Describe(op, _ => "Meeting-Notizen", _ => "Programm", _ => "Keynote");

    [Fact]
    public void EveryAreaProducesReadableChanges()
    {
        var chat = Describe(new MeetingOp { Type = MeetingOpTypes.ChatAdd, ActorId = Ines, ActorName = "Ines", Chat = new MeetingChatMessage(Guid.NewGuid(), Ines, "Ines", "Budget 180k", DateTimeOffset.UtcNow) });
        Assert.Equal((MeetingChangeLog.Chat, false), (chat!.Area, chat.IsEdit));

        var block = Describe(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = Ines, ActorName = "Ines", DocumentId = "d", Block = new MeetingNoteBlock { Id = "b", Kind = "todo", Text = "Location buchen", Done = true } });
        Assert.Equal(MeetingChangeLog.Notes, block!.Area);
        Assert.True(block.IsEdit);
        Assert.Contains("„Meeting-Notizen“", block.Text, StringComparison.Ordinal);
        Assert.Contains("(erledigt)", block.Text, StringComparison.Ordinal);

        var note = Describe(new MeetingOp { Type = MeetingOpTypes.NoteMove, ActorId = Ines, ActorName = "Ines", NoteId = "n", ColumnId = "c" });
        Assert.Equal("verschob „Keynote“ nach „Programm“", note!.Text);

        var table = Describe(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, ActorId = Ines, ActorName = "Ines", Element = new WhiteboardElement { Kind = "table", Rows = [["Aufgabe", "Wer"], ["Catering", "June"]] } });
        Assert.Equal(MeetingChangeLog.Whiteboard, table!.Area);
        Assert.Contains("Catering, June", table.Text, StringComparison.Ordinal);

        var speech = Describe(new MeetingOp { Type = MeetingOpTypes.TranscriptAdd, ActorId = Ines, Transcript = new MeetingTranscriptEntry(Guid.NewGuid(), Ines, "Ines", "Wir nehmen Hamburg.", DateTimeOffset.UtcNow) });
        Assert.Equal((MeetingChangeLog.Speech, "Ines"), (speech!.Area, speech.Actor));
    }

    [Fact]
    public void AgentPresenceAndCanvasMovesAreNotChanges()
    {
        Assert.Null(Describe(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = MeetingRoom.AgentId, DocumentId = "d", Block = new MeetingNoteBlock { Text = "x" } }));
        Assert.Null(Describe(new MeetingOp { Type = MeetingOpTypes.Presence, ActorId = Ines }));
        Assert.Null(Describe(new MeetingOp { Type = MeetingOpTypes.ElementMove, ActorId = Ines, NoteId = "e", X = 5 }));
        Assert.Null(Describe(new MeetingOp { Type = MeetingOpTypes.ParticipantMedia, ActorId = Ines }));
    }

    [Fact]
    public void RepeatedEditsOfOneItemKeepOnlyTheLatestText()
    {
        var log = new List<MeetingChange>();
        foreach (var text in new[] { "Bud", "Budget", "Budget 180k" })
            MeetingChangeLog.Record(log, Describe(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = Ines, ActorName = "Ines", DocumentId = "d", Block = new MeetingNoteBlock { Id = "b", Text = text } })!);
        var entry = Assert.Single(log);
        Assert.Contains("Budget 180k", entry.Text, StringComparison.Ordinal);
        Assert.Equal("Übernehme Notizen in die anderen Bereiche …", MeetingAgentService.SyncStatus(log));
        MeetingChangeLog.Record(log, Describe(new MeetingOp { Type = MeetingOpTypes.NoteDelete, ActorId = Ines, ActorName = "Ines", NoteId = "n" })!);
        Assert.Equal("Gleiche Notizen und Whiteboard ab …", MeetingAgentService.SyncStatus(log));
    }

    [Fact]
    public void PromptSharesChangesAndTheOneNotesDocument()
    {
        var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" };
        MeetingRoom.Normalize(state);
        // Older meetings with several documents are folded into the single notes document.
        state.Documents.Add(new MeetingDocument { Title = "Catering-Ideen", Blocks = [new MeetingNoteBlock { Kind = "todo", Text = "Vegane Option", Done = true }] });
        MeetingRoom.Normalize(state);
        Assert.Single(state.Documents);
        Assert.Contains(state.Documents[0].Blocks, b => b.Kind == "h2" && b.Text == "Catering-Ideen");
        Assert.False(new MeetingRoom(state).Apply(new MeetingOp { Type = MeetingOpTypes.DocumentCreate, ActorId = Guid.NewGuid(), Document = new MeetingDocument { Title = "Zweites" } }));
        var changes = new List<MeetingChange>
        {
            new(MeetingChangeLog.Whiteboard, "note:1", "Vinny", "schrieb die Haftnotiz „Hamburg“ in „Rahmen“", DateTimeOffset.UtcNow, true),
        };
        var prompt = MeetingAgentService.BuildPrompt(state, [], changes);
        Assert.Contains("Änderungen seit deinem letzten Durchgang", prompt, StringComparison.Ordinal);
        Assert.Contains("- [Whiteboard] Vinny schrieb die Haftnotiz „Hamburg“ in „Rahmen“", prompt, StringComparison.Ordinal);
        Assert.Contains("[x] Vegane Option", prompt, StringComparison.Ordinal);
        Assert.Contains("Halte sie synchron", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatInfoIsParsedSeparatelyFromAnswers()
    {
        var result = MeetingAgentMerge.Parse("""{"chatReply": null, "chatInfo": "Ich habe die Entscheidung vom Whiteboard übernommen.", "notes": {}, "whiteboard": {}}""");
        Assert.Null(result.ChatReply);
        Assert.Equal("Ich habe die Entscheidung vom Whiteboard übernommen.", result.ChatInfo);
        Assert.Null(MeetingAgentMerge.Parse("""{"chatInfo": "  "}""").ChatInfo);
    }
}
