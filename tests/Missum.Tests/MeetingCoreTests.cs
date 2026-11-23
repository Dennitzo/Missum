using Missum.App.Services.Meetings;

namespace Missum.Tests;

public sealed class MeetingCoreTests
{
    private static readonly Guid Ines = Guid.NewGuid();
    private static readonly Guid Vinny = Guid.NewGuid();

    private static MeetingRoom NewRoom()
    {
        var room = new MeetingRoom(new MeetingRoomState { Id = Guid.NewGuid(), Title = "Kundenveranstaltung" });
        Assert.True(room.Apply(Join(Ines, "Ines")));
        Assert.True(room.Apply(Join(Vinny, "Vinny")));
        return room;
    }

    private static MeetingOp Join(Guid id, string name) => new()
    {
        Type = MeetingOpTypes.ParticipantJoin,
        ActorId = id,
        Participant = new MeetingParticipant { Id = id, Name = name },
    };

    [Fact]
    public void NewRoomHasMissumNotesDocumentAndInboxColumn()
    {
        var room = new MeetingRoom(new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" });
        var state = room.Snapshot();
        Assert.Contains(state.Participants, p => p.Id == MeetingRoom.AgentId && p.IsAgent);
        Assert.Single(state.Documents);
        Assert.Single(state.Whiteboard.Columns);
    }

    [Fact]
    public void ParticipantsGetDistinctColorsAndLeaveKeepsHistory()
    {
        var room = NewRoom();
        var state = room.Snapshot();
        var colors = state.Participants.Select(p => p.ColorIndex).ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantLeave, ParticipantId = Vinny }));
        Assert.False(room.Snapshot().Participants.Single(p => p.Id == Vinny).Connected);
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantLeave, ParticipantId = Vinny }));
    }

    [Fact]
    public void ChatUsesTrustedAuthorAndIgnoresDuplicates()
    {
        var room = NewRoom();
        var message = new MeetingChatMessage(Guid.NewGuid(), Vinny, "Mallory", "Hallo", DateTimeOffset.UtcNow);
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.ChatAdd, ActorId = Ines, Chat = message }));
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.ChatAdd, ActorId = Ines, Chat = message }));
        var stored = Assert.Single(room.Snapshot().Chat);
        Assert.Equal(Ines, stored.AuthorId);
        Assert.Equal("Ines", stored.AuthorName);
        Assert.False(stored.IsAgent);
    }

    [Fact]
    public void HumanEditPinsAnAiBlock()
    {
        var room = NewRoom();
        var document = room.Snapshot().Documents[0];
        var aiBlock = new MeetingNoteBlock { Kind = "bullet", Text = "Budget 180.000 €", Section = "ai.decisions", AiOwned = true };
        Assert.True(room.Apply(new MeetingOp
        {
            Type = MeetingOpTypes.DocumentSetBlocks,
            ActorId = MeetingRoom.AgentId,
            DocumentId = document.Id,
            Blocks = [.. document.Blocks, aiBlock],
        }));
        Assert.True(room.Apply(new MeetingOp
        {
            Type = MeetingOpTypes.BlockUpsert,
            ActorId = Ines,
            ActorName = "Ines",
            DocumentId = document.Id,
            Block = new MeetingNoteBlock { Id = aiBlock.Id, Kind = "bullet", Text = "Budget 160.000 €" },
        }));
        var stored = room.Snapshot().Documents[0].Blocks.Single(b => b.Id == aiBlock.Id);
        Assert.True(stored.AiOwned);
        Assert.True(stored.HumanEdited);
        Assert.Equal("ai.decisions", stored.Section);
        Assert.Equal("Ines", stored.EditedBy);
    }

    [Fact]
    public void BlocksInsertAfterTheirAnchor()
    {
        var room = NewRoom();
        var document = room.Snapshot().Documents[0];
        var first = document.Blocks[0].Id;
        var a = new MeetingNoteBlock { Text = "A" };
        var b = new MeetingNoteBlock { Text = "B" };
        room.Apply(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = Ines, DocumentId = document.Id, Block = a, AfterId = first });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = Ines, DocumentId = document.Id, Block = b, AfterId = first });
        Assert.Equal(["B", "A"], room.Snapshot().Documents[0].Blocks.Skip(1).Select(x => x.Text));
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.BlockDelete, ActorId = Ines, DocumentId = document.Id, BlockId = b.Id }));
        Assert.Equal(["A"], room.Snapshot().Documents[0].Blocks.Skip(1).Select(x => x.Text));
    }

    [Fact]
    public void WhiteboardNotesSurviveColumnDeletionAndToggleReactions()
    {
        var room = NewRoom();
        var inbox = room.Snapshot().Whiteboard.Columns[0].Id;
        var column = new WhiteboardColumn { Title = "Schwierig" };
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.ColumnUpsert, ActorId = Ines, Column = column }));
        var note = new WhiteboardNote { Text = "Location buchen", ColumnId = column.Id };
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.NoteUpsert, ActorId = Vinny, ActorName = "Vinny", Note = note }));
        Assert.Equal("Vinny", room.Snapshot().Whiteboard.Notes.Single().Author);

        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.NoteReact, ActorId = Ines, ActorName = "Ines", NoteId = note.Id, Reaction = "heart" }));
        Assert.Equal(["Ines"], room.Snapshot().Whiteboard.Notes.Single().Reactions["heart"]);
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.NoteReact, ActorId = Ines, ActorName = "Ines", NoteId = note.Id, Reaction = "heart" }));
        Assert.Empty(room.Snapshot().Whiteboard.Notes.Single().Reactions);

        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.ColumnDelete, ActorId = Ines, ColumnId = column.Id }));
        Assert.Equal(inbox, room.Snapshot().Whiteboard.Notes.Single().ColumnId);
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.ColumnDelete, ActorId = Ines, ColumnId = inbox }));
    }

    [Fact]
    public void RevisionAdvancesOnlyForRealChanges()
    {
        var room = NewRoom();
        var before = room.Snapshot().Revision;
        Assert.False(room.Apply(new MeetingOp { Type = "unknown" }));
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.Presence, ActorId = Ines, DocumentId = "d", BlockId = "b" }));
        Assert.Equal(before, room.Snapshot().Revision);
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomLive, Flag = true }));
        Assert.Equal(before + 1, room.Snapshot().Revision);
    }

    [Fact]
    public void HostOnlyOperationsAreClassified()
    {
        Assert.True(MeetingOpTypes.IsHostOnly(MeetingOpTypes.WhiteboardSet));
        Assert.True(MeetingOpTypes.IsHostOnly(MeetingOpTypes.TranscriptAdd));
        Assert.False(MeetingOpTypes.IsHostOnly(MeetingOpTypes.NoteUpsert));
        Assert.False(MeetingOpTypes.IsHostOnly(MeetingOpTypes.ChatAdd));
    }

    [Fact]
    public void MediaFramesRoundTripAndRejectOversizedPayloads()
    {
        var id = Guid.NewGuid();
        var frame = MeetingMediaFrame.Create(MeetingMediaFrame.Audio, id, [1, 2, 3]);
        Assert.True(MeetingMediaFrame.TryRead(frame, out var kind, out var participant, out var payload));
        Assert.Equal(MeetingMediaFrame.Audio, kind);
        Assert.Equal(id, participant);
        Assert.Equal([1, 2, 3], payload.ToArray());
        var other = Guid.NewGuid();
        MeetingMediaFrame.Stamp(frame, other);
        Assert.True(MeetingMediaFrame.TryRead(frame, out _, out participant, out _));
        Assert.Equal(other, participant);
        var oversized = MeetingMediaFrame.Create(MeetingMediaFrame.Audio, id, new byte[MeetingMediaFrame.MaximumAudioBytes + 1]);
        Assert.False(MeetingMediaFrame.TryRead(oversized, out _, out _, out _));
    }
}

public sealed class MeetingAgentMergeTests
{
    [Fact]
    public void ParseToleratesProseAroundJsonAndMissingParts()
    {
        var result = MeetingAgentMerge.Parse("""
            Hier ist das Update:
            {"chatReply": "Die Stadt ist noch offen.",
             "notes": {"summary": "Termin steht.", "decisions": ["Save the Date am 8. Dezember"], "actionItems": [{"task": "bucht die Location", "owner": "Vinny", "due": "15. Januar"}], "openQuestions": ["Welche Stadt?"]},
             "whiteboard": {"columns": [{"id": null, "title": "Offen", "subtitle": "Noch zu klären", "noteIds": []}], "newNotes": ["Stadt festlegen"]}}
            """);
        Assert.Equal("Die Stadt ist noch offen.", result.ChatReply);
        Assert.Equal("Termin steht.", result.Notes?.Summary);
        Assert.Equal("Vinny: bucht die Location (bis 15. Januar)", MeetingAgentMerge.FormatAction(result.Notes!.ActionItems![0]));
        Assert.Single(result.Columns);
        Assert.Equal("Stadt festlegen", Assert.Single(result.NewNotes).Text);
        Assert.Empty(result.RemoveNoteIds);
        Assert.Throws<InvalidOperationException>(() => MeetingAgentMerge.Parse("keine Daten"));
    }

    [Fact]
    public void NotesMergeReplacesOnlyUntouchedMissumItems()
    {
        var first = MeetingAgentMerge.MergeNotes(
            [new MeetingNoteBlock { Kind = "h1", Text = "Meeting" }],
            new AgentNotes("Erste Zusammenfassung", ["Entscheidung A", "Entscheidung B"], [], ["Frage 1"]));
        Assert.Equal("Meeting", first[0].Text);
        Assert.Contains(first, b => b.Section == MeetingAgentMerge.RootSection);
        Assert.Equal(["Entscheidung A", "Entscheidung B"], first.Where(b => b.Section == "ai.decisions").Select(b => b.Text));

        // A participant corrects decision B; Missum must keep that wording.
        var edited = first.Select(MeetingJson.Clone).ToList();
        var b = edited.Single(x => x.Text == "Entscheidung B");
        b.Text = "Entscheidung B (geändert)";
        b.HumanEdited = true;
        edited.Add(new MeetingNoteBlock { Kind = "p", Text = "Eigene Notiz von Ines" });

        var second = MeetingAgentMerge.MergeNotes(edited,
            new AgentNotes("Neue Zusammenfassung", ["Entscheidung C", "entscheidung b (geändert)"], null, []));
        var decisions = second.Where(x => x.Section == "ai.decisions").Select(x => x.Text).ToList();
        Assert.Contains("Entscheidung B (geändert)", decisions);
        Assert.Contains("Entscheidung C", decisions);
        Assert.DoesNotContain("Entscheidung A", decisions);
        Assert.Single(decisions, d => d.StartsWith("Entscheidung B", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Neue Zusammenfassung"], second.Where(x => x.Section == "ai.summary").Select(x => x.Text));
        Assert.DoesNotContain(second, x => x.Section == "ai.questions");
        Assert.Contains(second, x => x.Text == "Eigene Notiz von Ines");
        // Null list keeps what was there (actions were never filled).
        Assert.Single(second, x => x.Section == "ai.actions.h");
        // Section order stays stable.
        var headings = second.Where(x => x.Section?.EndsWith(".h", StringComparison.Ordinal) == true).Select(x => x.Text).ToList();
        Assert.Equal(["Zusammenfassung", "Entscheidungen", "Aufgaben", "Offene Fragen"], headings);
    }

    [Fact]
    public void WhiteboardMergeSortsButNeverRewritesOrDeletesHumanNotes()
    {
        var inbox = new WhiteboardColumn { Title = "Sammlung" };
        var human = new WhiteboardNote { Text = "Caramel apple", Author = "Marcus", ColumnId = inbox.Id };
        var staleAi = new WhiteboardNote { Text = "Alter Punkt", Author = "Missum", IsAi = true, ColumnId = inbox.Id };
        var pinnedAi = new WhiteboardNote { Text = "Von Ines korrigiert", IsAi = true, HumanEdited = true, ColumnId = inbox.Id };
        var board = new MeetingWhiteboard { Columns = [inbox], Notes = [human, staleAi, pinnedAi] };

        var result = new AgentResult(null, null,
            [new AgentColumn(null, "Einfach", "Baut auf Bestehendem auf", [human.Id, pinnedAi.Id])],
            [new AgentNewNote("Chai pumpkin", "Einfach"), new AgentNewNote("caramel APPLE", "Einfach")],
            [staleAi.Id, pinnedAi.Id, human.Id]);
        var merged = MeetingAgentMerge.MergeWhiteboard(board, result);

        var easy = merged.Columns.First();
        Assert.Equal("Einfach", easy.Title);
        Assert.True(easy.IsAi);
        var kept = merged.Notes.Single(n => n.Id == human.Id);
        Assert.Equal("Caramel apple", kept.Text);
        Assert.Equal(easy.Id, kept.ColumnId);
        Assert.Contains(merged.Notes, n => n.Id == pinnedAi.Id);
        Assert.DoesNotContain(merged.Notes, n => n.Id == staleAi.Id);
        var added = Assert.Single(merged.Notes, n => n.Text == "Chai pumpkin");
        Assert.True(added.IsAi);
        Assert.Equal(easy.Id, added.ColumnId);
        // The duplicate idea was not added again.
        Assert.Single(merged.Notes, n => MeetingAgentMerge.Normalize(n.Text) == "caramelapple");
        // The untouched default inbox disappears once it is empty.
        Assert.DoesNotContain(merged.Columns, c => c.Id == inbox.Id);
    }

    [Fact]
    public void EmptyAgentColumnsLeaveTheBoardLayoutAlone()
    {
        var a = new WhiteboardColumn { Title = "A" };
        var b = new WhiteboardColumn { Title = "B" };
        var note = new WhiteboardNote { Text = "x", ColumnId = b.Id, Order = 5 };
        var merged = MeetingAgentMerge.MergeWhiteboard(new MeetingWhiteboard { Columns = [a, b], Notes = [note] },
            new AgentResult(null, null, [], [], []));
        Assert.Equal([a.Id, b.Id], merged.Columns.Select(c => c.Id));
        Assert.Equal(b.Id, merged.Notes.Single().ColumnId);
        Assert.Equal(1, merged.Notes.Single().Order);
    }

    [Fact]
    public void PromptMarksHumanEditsAndContainsQuestion()
    {
        var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" };
        MeetingRoom.Normalize(state);
        state.Documents[0].Blocks.Add(new MeetingNoteBlock { Text = "Budget 160k", AiOwned = true, HumanEdited = true });
        state.Transcript.Add(new MeetingTranscriptEntry(Guid.NewGuid(), Guid.NewGuid(), "Vinny", "Die Location ist das größte Risiko.", DateTimeOffset.UtcNow));
        var prompt = MeetingAgentService.BuildPrompt(state, [new MeetingAgentService.AgentRequest("Was ist noch offen?", "Vinny", null)]);
        Assert.Contains("Budget 160k [von Teilnehmer bearbeitet]", prompt, StringComparison.Ordinal);
        Assert.Contains("Vinny: Die Location ist das größte Risiko.", prompt, StringComparison.Ordinal);
        Assert.Contains("Frage an Missum:\nVinny: Was ist noch offen?", prompt, StringComparison.Ordinal);
    }
}

public sealed class MeetingStorageTests
{
    [Fact]
    public async Task ProjectFolderRoundTripKeepsNotesBoardChatAndTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-meeting-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = Guid.NewGuid();
            var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Kunden-Event: Planung", CreatedAt = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero) };
            MeetingRoom.Normalize(state);
            state.Documents[0].Blocks.AddRange(
            [
                new MeetingNoteBlock { Kind = "h2", Text = "Missums Live-Notizen", Section = MeetingAgentMerge.RootSection, AiOwned = true },
                new MeetingNoteBlock { Kind = "todo", Text = "Vinny bucht die Location", Section = "ai.actions", AiOwned = true, HumanEdited = true, Done = true, EditedBy = "Ines Müller" },
                new MeetingNoteBlock { Kind = "p", Text = "Zwei Zeilen\nin einem Absatz" },
            ]);
            state.Whiteboard.Notes.Add(new WhiteboardNote { Text = "Churro pie", Author = "June", ColumnId = state.Whiteboard.Columns[0].Id });
            state.Whiteboard.Notes[0].Reactions["heart"] = ["Teo"];
            state.Chat.Add(new MeetingChatMessage(Guid.NewGuid(), Guid.NewGuid(), "June", "Caterer übernehme ich.", DateTimeOffset.UtcNow));
            state.Transcript.Add(new MeetingTranscriptEntry(Guid.NewGuid(), Guid.NewGuid(), "Vinny", "Ja.", DateTimeOffset.UtcNow));
            var folder = MeetingProjectStore.CreateFolderPath(root, session, state.Title, state.CreatedAt);
            var info = new MeetingFolderInfo(session, state.Id, "host", null, state.Title, state.CreatedAt, "token-1234567890abcdef");

            await MeetingProjectStore.SaveAsync(folder, info, state);
            Assert.Equal(folder, MeetingProjectStore.FindFolder(root, session));
            Assert.StartsWith("2026-11-06-Kunden-Event-Planung-", Path.GetFileName(folder), StringComparison.Ordinal);
            var markdown = await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(folder, "notes"), "*.md").Single());
            Assert.Contains("- [x] Vinny bucht die Location", markdown, StringComparison.Ordinal);

            var loaded = await MeetingProjectStore.LoadAsync(folder);
            Assert.NotNull(loaded);
            var (loadedInfo, loadedState) = loaded.Value;
            Assert.Equal("token-1234567890abcdef", loadedInfo.HostToken);
            Assert.Equal(state.Id, loadedState.Id);
            var blocks = loadedState.Documents[0].Blocks;
            Assert.Equal(state.Documents[0].Blocks.Select(b => (b.Id, b.Kind, b.Text, b.Section, b.AiOwned, b.HumanEdited, b.Done, b.EditedBy)),
                blocks.Select(b => (b.Id, b.Kind, b.Text, b.Section, b.AiOwned, b.HumanEdited, b.Done, b.EditedBy)));
            Assert.Equal(["Teo"], loadedState.Whiteboard.Notes.Single().Reactions["heart"]);
            Assert.Single(loadedState.Chat);
            Assert.Single(loadedState.Transcript);
            Assert.DoesNotContain(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories), _ => true);

            // Renaming a document must not leave the old file behind.
            loadedState.Documents[0].Title = "Umbenannt";
            await MeetingProjectStore.SaveAsync(folder, loadedInfo, loadedState);
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "notes"), "*.md"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExternallyWrittenMarkdownIsReadAsBlocks()
    {
        var blocks = MeetingProjectStore.ParseMarkdown("# Titel\n\n- Punkt\n\n- [ ] Aufgabe\n");
        Assert.Equal(["h1", "bullet", "todo"], blocks.Select(b => b.Kind));
        Assert.Equal(["Titel", "Punkt", "Aufgabe"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void JoinLinksRoundTripAndRejectForeignUris()
    {
        var link = new MeetingLink("192.168.1.20", 8090, Guid.NewGuid(), "abc-DEF_123");
        var share = link.ToShareString("missum-stable");
        Assert.True(MeetingLink.TryParse(share, out var parsed));
        Assert.Equal(link, parsed);
        Assert.StartsWith("ws://192.168.1.20:8090/v1/meetings/", parsed!.WebSocketUri("Du", Guid.NewGuid()).AbsoluteUri, StringComparison.Ordinal);
        Assert.False(MeetingLink.TryParse("https://example.com/join/x", out _));
        Assert.False(MeetingLink.TryParse("missum-stable://session/" + Guid.NewGuid(), out _));
        Assert.False(MeetingLink.TryParse(share.Replace("?t=abc-DEF_123", "", StringComparison.Ordinal), out _));
    }

    [Fact]
    public void SegmenterCutsUtterancesAtPausesAndIgnoresSilence()
    {
        var segmenter = new UtteranceSegmenter();
        var silence = new byte[3200];
        var speech = Tone(3200, 6000);
        Assert.Empty(segmenter.Push(silence));
        var finished = new List<byte[]>();
        for (var i = 0; i < 8; i++) finished.AddRange(segmenter.Push(speech));
        Assert.Empty(finished);
        for (var i = 0; i < 9; i++) finished.AddRange(segmenter.Push(silence));
        var utterance = Assert.Single(finished);
        Assert.True(utterance.Length >= 8 * 3200);

        var cough = new UtteranceSegmenter();
        var none = new List<byte[]>();
        none.AddRange(cough.Push(Tone(3200, 6000)));
        for (var i = 0; i < 9; i++) none.AddRange(cough.Push(silence));
        Assert.Empty(none);
    }

    private static byte[] Tone(int bytes, short amplitude)
    {
        var buffer = new byte[bytes];
        for (var i = 0; i < bytes / 2; i++)
        {
            var sample = (short)(Math.Sin(i * 2 * Math.PI * 220 / 16000) * amplitude);
            buffer[i * 2] = (byte)sample;
            buffer[i * 2 + 1] = (byte)(sample >> 8);
        }
        return buffer;
    }
}
