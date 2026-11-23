using Missum.App.Services.Meetings;

namespace Missum.Tests;

public sealed class MeetingCanvasTests
{
    private static readonly Guid Ines = Guid.NewGuid();

    private static MeetingRoom NewRoom() => new(new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" });

    private static MeetingOp Upsert(WhiteboardElement element) =>
        new() { Type = MeetingOpTypes.ElementUpsert, ActorId = Ines, ActorName = "Ines", Element = element };

    [Fact]
    public void ElementsAreSanitizedPlacedAndOwnedByTheirAuthor()
    {
        var room = NewRoom();
        var table = new WhiteboardElement { Kind = "table", X = -50, Y = 99_999, Width = 5, Rows = [["A", "B", "C"], ["1"]] };
        Assert.True(room.Apply(Upsert(table)));
        var stored = room.Snapshot().Whiteboard.Elements.Single();
        Assert.Equal(0, stored.X);
        Assert.Equal(MeetingRoom.CanvasHeight - 40, stored.Y);
        Assert.Equal(40, stored.Width);
        Assert.Equal(["1", "", ""], stored.Rows![1]);
        Assert.Equal("Ines", stored.Author);
        Assert.True(stored.Z > 0);
        Assert.False(room.Apply(Upsert(new WhiteboardElement { Kind = "video" })));
    }

    [Fact]
    public void MoveClampsAndArrowsFollowTheirEndpointsIntoDeletion()
    {
        var room = NewRoom();
        var a = new WhiteboardElement { Kind = "text", Text = "A" };
        var b = new WhiteboardElement { Kind = "shape", Shape = "triangle", X = 400 };
        Assert.True(room.Apply(Upsert(a)));
        Assert.True(room.Apply(Upsert(b)));
        Assert.Equal("rect", room.Snapshot().Whiteboard.Elements.Single(e => e.Id == b.Id).Shape);
        Assert.False(room.Apply(Upsert(new WhiteboardElement { Kind = "arrow", FromId = a.Id, ToId = "missing" })));
        var arrow = new WhiteboardElement { Kind = "arrow", FromId = a.Id, ToId = b.Id };
        Assert.True(room.Apply(Upsert(arrow)));

        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.ElementMove, ActorId = Ines, NoteId = a.Id, X = 120, Y = -5, Width = 9999 }));
        var moved = room.Snapshot().Whiteboard.Elements.Single(e => e.Id == a.Id);
        Assert.Equal((120d, 0d, 2400d), (moved.X, moved.Y, moved.Width));
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.ElementMove, ActorId = Ines, NoteId = arrow.Id, X = 5 }));

        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.ElementDelete, ActorId = Ines, NoteId = b.Id }));
        Assert.Equal([a.Id], room.Snapshot().Whiteboard.Elements.Select(e => e.Id));
    }

    [Fact]
    public void ArrowsMayConnectStickyNotesAndDisappearWithThem()
    {
        var room = NewRoom();
        var column = room.Snapshot().Whiteboard.Columns[0].Id;
        var note = new WhiteboardNote { Text = "Idee", ColumnId = column };
        room.Apply(new MeetingOp { Type = MeetingOpTypes.NoteUpsert, ActorId = Ines, ActorName = "Ines", Note = note });
        var text = new WhiteboardElement { Kind = "text", Text = "Detail" };
        room.Apply(Upsert(text));
        Assert.True(room.Apply(Upsert(new WhiteboardElement { Kind = "arrow", FromId = note.Id, ToId = text.Id })));
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.NoteDelete, ActorId = Ines, NoteId = note.Id }));
        Assert.DoesNotContain(room.Snapshot().Whiteboard.Elements, e => e.Kind == "arrow");
    }

    [Fact]
    public void ImagesAreLimitedAndAgentUpdatesKeepPeoplesElements()
    {
        var room = NewRoom();
        Assert.False(room.Apply(Upsert(new WhiteboardElement { Kind = "image", ImageData = new string('A', MeetingRoom.MaximumImageBase64Length + 4) })));
        var image = new WhiteboardElement { Kind = "image", ImageData = Convert.ToBase64String([1, 2, 3]) };
        Assert.True(room.Apply(Upsert(image)));
        // An edit without picture data (e.g. a resize from the menu) keeps the image.
        var edit = MeetingJson.Clone(image);
        edit.ImageData = null;
        edit.Width = 500;
        Assert.True(room.Apply(Upsert(edit)));
        Assert.NotNull(room.Snapshot().Whiteboard.Elements.Single().ImageData);

        var agentBoard = MeetingAgentMerge.MergeWhiteboard(room.Snapshot().Whiteboard, new AgentResult(null, null, [], [], []));
        agentBoard.Elements.Clear();
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.WhiteboardSet, ActorId = MeetingRoom.AgentId, Whiteboard = agentBoard }));
        Assert.Single(room.Snapshot().Whiteboard.Elements);
    }

    [Fact]
    public async Task ImagesAreStoredAsProjectAssetsAndRestored()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-canvas-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Bilder" };
            MeetingRoom.Normalize(state);
            var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
            state.Whiteboard.Elements.Add(new WhiteboardElement { Id = "img1", Kind = "image", ImageData = Convert.ToBase64String(bytes), Width = 320, Height = 200 });
            state.Whiteboard.Elements.Add(new WhiteboardElement { Id = "tbl1", Kind = "table", Rows = [["Aufgabe", "Wer"], ["Location", "Vinny"]] });
            var session = Guid.NewGuid();
            var folder = MeetingProjectStore.CreateFolderPath(root, session, state.Title, state.CreatedAt);
            var info = new MeetingFolderInfo(session, state.Id, "host", null, state.Title, state.CreatedAt);
            await MeetingProjectStore.SaveAsync(folder, info, state);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder, "assets", "img1.jpg")));
            Assert.DoesNotContain(Convert.ToBase64String(bytes), await File.ReadAllTextAsync(Path.Combine(folder, "whiteboard.json")), StringComparison.Ordinal);
            var loaded = (await MeetingProjectStore.LoadAsync(folder))!.Value.State;
            Assert.Equal(Convert.ToBase64String(bytes), loaded.Whiteboard.Elements.Single(e => e.Kind == "image").ImageData);
            Assert.Equal("Vinny", loaded.Whiteboard.Elements.Single(e => e.Kind == "table").Rows![1][1]);

            loaded.Whiteboard.Elements.RemoveAll(e => e.Kind == "image");
            await MeetingProjectStore.SaveAsync(folder, info, loaded);
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "assets")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MeetingsRequireAProjectFolder()
    {
        Assert.Null(MeetingProjectStore.ResolveRoot(null));
        Assert.Null(MeetingProjectStore.ResolveRoot(Path.Combine(Path.GetTempPath(), "missum-missing-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "Meetings"), MeetingProjectStore.ResolveRoot(Path.GetTempPath()));
    }

    [Fact]
    public void AgentIsCalledMissumAndKeepsTheLegacyAlias()
    {
        Assert.Equal("Missum", MeetingRoom.AgentName);
        Assert.True(MeetingHostService.MentionsAgent("@Missum, was ist offen?"));
        Assert.True(MeetingHostService.MentionsAgent("@claude bitte"));
        Assert.False(MeetingHostService.MentionsAgent("Missum ist toll"));

        var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" };
        MeetingRoom.Normalize(state);
        state.Whiteboard.Elements.Add(new WhiteboardElement { Kind = "table", Rows = [["Ziel", "Termin"], ["Launch", "März"]] });
        var prompt = MeetingAgentService.BuildPrompt(state, []);
        Assert.StartsWith("Du bist Missum", prompt.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("Tabelle: Ziel, Termin | Launch, März", prompt, StringComparison.Ordinal);

        var renamed = MeetingAgentMerge.MergeNotes(
            [new MeetingNoteBlock { Kind = "h2", Text = "Claudes Live-Notizen", Section = MeetingAgentMerge.RootSection, AiOwned = true }],
            new AgentNotes("Kurz", [], [], []));
        Assert.Equal("Missums Live-Notizen", renamed[0].Text);
    }

    [Fact]
    public void AgentStatusKeepsTheLastContextUsage()
    {
        var room = NewRoom();
        room.Apply(new MeetingOp { Type = MeetingOpTypes.AgentStatus, ActorId = MeetingRoom.AgentId, Agent = new MeetingAgentStatus { State = "thinking", ContextUsed = 1200, ContextLimit = 32768 } });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.AgentStatus, ActorId = MeetingRoom.AgentId, Agent = new MeetingAgentStatus { State = "done" } });
        var agent = room.Snapshot().Agent;
        Assert.Equal(("done", 1200, 32768), (agent.State, agent.ContextUsed, agent.ContextLimit));
        Assert.Equal(3, MeetingAgentService.EstimateTokens("abcdefghij"));
    }
}
