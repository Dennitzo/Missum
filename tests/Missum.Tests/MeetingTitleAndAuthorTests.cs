using Missum.App.Services.Meetings;

namespace Missum.Tests;

public sealed class MeetingTitleAndAuthorTests
{
    private static readonly Guid Ines = Guid.NewGuid();
    private static readonly Guid Vinny = Guid.NewGuid();

    private static MeetingRoom NewRoom()
    {
        var room = new MeetingRoom(new MeetingRoomState { Id = Guid.NewGuid(), Title = "Kundenveranstaltung" });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantJoin, ActorId = Ines, Participant = new MeetingParticipant { Id = Ines, Name = "Ines" } });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantJoin, ActorId = Vinny, Participant = new MeetingParticipant { Id = Vinny, Name = "Vinny" } });
        return room;
    }

    [Fact]
    public void BlocksRememberEveryAuthorWhoWroteText()
    {
        var room = NewRoom();
        var document = room.Snapshot().Documents[0].Id;
        var block = new MeetingNoteBlock { Kind = "p", Text = "" };
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = Ines, ActorName = "Ines", DocumentId = document, Block = block }));
        Assert.Empty(room.Snapshot().Documents[0].Blocks.Single(b => b.Id == block.Id).Authors);
        room.Apply(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = Ines, ActorName = "Ines", DocumentId = document, Block = new MeetingNoteBlock { Id = block.Id, Text = "Budget klären" } });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, ActorId = Vinny, ActorName = "Vinny", DocumentId = document, Block = new MeetingNoteBlock { Id = block.Id, Text = "Budget bis Freitag klären", Authors = [Guid.NewGuid()] } });
        var saved = room.Snapshot().Documents[0].Blocks.Single(b => b.Id == block.Id);
        // The sender cannot inject authors; the room records who actually wrote.
        Assert.Equal([Ines, Vinny], saved.Authors);
        var authors = Missum.App.Pages.NativeAssistantPage.NoteBlockAuthors(saved, room.Snapshot());
        Assert.Equal(["Ines", "Vinny"], authors.Select(a => a.Author.Name));
        // Every author carries the time they last wrote, shown in the hover tooltip.
        Assert.All(authors, a => Assert.NotNull(a.At));
        Assert.True(saved.AuthorTimes.ContainsKey(Ines) && saved.AuthorTimes.ContainsKey(Vinny));
    }

    [Fact]
    public void MissumNamesTheMeetingUntilTheHostRenamesIt()
    {
        var host = Guid.NewGuid();
        var room = NewRoom();
        room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantJoin, ActorId = host, Participant = new MeetingParticipant { Id = host, Name = "Dennis", Role = "host" } });
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = MeetingRoom.AgentId, Text = "Budgetplanung Q3" }));
        var state = room.Snapshot();
        Assert.Equal("Budgetplanung Q3", state.Title);
        Assert.Equal("Budgetplanung Q3", state.Documents[0].Title);
        Assert.False(state.TitleLocked);
        // Guests cannot rename; the host can, and then Missum keeps the host's name.
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = Ines, Text = "Mein Titel" }));
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = host, Text = "Kundentag 2027" }));
        Assert.True(room.Snapshot().TitleLocked);
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = MeetingRoom.AgentId, Text = "Etwas anderes" }));
        Assert.Equal("Kundentag 2027", room.Snapshot().Title);
    }

    [Theory]
    [InlineData("\"Budgetplanung Q3.\"", "Budgetplanung Q3")]
    [InlineData("**Kick-off**", "Kick-off")]
    [InlineData("ab", null)]
    [InlineData("Ein sehr langer Titel über die Planung der Kundenveranstaltung im nächsten Frühjahr", "Ein sehr langer Titel über die Planung der")]
    public void TitlesFromMissumStayCompact(string raw, string? expected) =>
        Assert.Equal(expected, MeetingAgentMerge.CompactTitle(raw));
}
