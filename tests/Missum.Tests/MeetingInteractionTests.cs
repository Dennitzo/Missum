using Missum.App.Controls;
using Missum.App.Pages;
using Missum.App.Services.Meetings;

namespace Missum.Tests;

public sealed class MeetingInteractionTests
{
    [Theory]
    [InlineData("@", 1, 0, "")]
    [InlineData("Hallo @Mi", 9, 6, "Mi")]
    [InlineData("@Ines und", 4, 0, "Ine")]
    public void MentionQueryIsFoundRightBeforeTheCaret(string text, int caret, int start, string query)
    {
        var found = MentionAutocomplete.FindQuery(text, caret);
        Assert.NotNull(found);
        Assert.Equal((start, query), found.Value);
    }

    [Theory]
    [InlineData("mail@example", 12)]
    [InlineData("@Ines und", 9)]
    [InlineData("kein At", 7)]
    public void MentionQueryIgnoresEmailsAndFinishedWords(string text, int caret) =>
        Assert.Null(MentionAutocomplete.FindQuery(text, caret));

    [Theory]
    [InlineData("@Missum Was ergibt 1+1?", "Was ergibt 1+1?")]
    [InlineData("Notiz: @missum, fasse zusammen", "fasse zusammen")]
    [InlineData("@Missum", null)]
    [InlineData("Ohne Anrede", null)]
    public void NoteQuestionsAreTheTextAfterTheMention(string text, string? expected) =>
        Assert.Equal(expected, NativeAssistantPage.MissumQuestion(text));

    [Fact]
    public void ParticipantsCanRenameThemselvesButNotMissum()
    {
        var room = new MeetingRoom(new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" });
        var id = Guid.NewGuid();
        room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantJoin, ActorId = id, Participant = new MeetingParticipant { Id = id, Name = "AMD" } });
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantRename, ActorId = id, Text = "  Dennis  " }));
        Assert.Equal("Dennis", room.Snapshot().Participants.Single(p => p.Id == id).Name);
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantRename, ActorId = id, Text = "Dennis" }));
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantRename, ActorId = MeetingRoom.AgentId, Text = "Claude" }));

        // New chat messages carry the new name.
        room.Apply(new MeetingOp { Type = MeetingOpTypes.ChatAdd, ActorId = id, Chat = new MeetingChatMessage(Guid.NewGuid(), id, "alt", "Hallo", DateTimeOffset.UtcNow) });
        Assert.Equal("Dennis", room.Snapshot().Chat.Single().AuthorName);
    }

    [Fact]
    public void LoadingIsAValidAgentState()
    {
        var room = new MeetingRoom(new MeetingRoomState { Id = Guid.NewGuid() });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.AgentStatus, ActorId = MeetingRoom.AgentId, Agent = new MeetingAgentStatus { State = "loading", Status = "AI-Modell wird geladen …" } });
        Assert.Equal(("loading", "AI-Modell wird geladen …"), (room.Snapshot().Agent.State, room.Snapshot().Agent.Status));
    }

    [Fact]
    public void NoteQuestionsAreMarkedInThePrompt()
    {
        var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" };
        MeetingRoom.Normalize(state);
        var prompt = MeetingAgentService.BuildPrompt(state, [new MeetingAgentService.AgentRequest("Was ergibt 1+1?", "Ines", "note", state.Documents[0].Id, "b1")]);
        Assert.Contains("Frage an Missum (in den Notizen", prompt, StringComparison.Ordinal);
        Assert.Contains("Ines: Was ergibt 1+1?", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentAnswerBlocksStayWhenNotesAreMerged()
    {
        var question = new MeetingNoteBlock { Id = "q", Text = "@Missum Was ergibt 1+1?" };
        var answer = new MeetingNoteBlock { Id = "a", Text = "2", Section = MeetingAgentService.AnswerSection, AiOwned = true };
        var merged = MeetingAgentMerge.MergeNotes([question, answer], new AgentNotes("Kurz", ["X"], [], []));
        Assert.Equal(["q", "a"], merged.Take(2).Select(b => b.Id));
    }

    [Fact]
    public void MeetingRunsMayUseCodexServerToolsAndShowWhatTheyDo()
    {
        Assert.Contains("web.search", MeetingAgentService.MeetingServerTools);
        Assert.Contains("web.fetch", MeetingAgentService.MeetingServerTools);
        using var search = System.Text.Json.JsonDocument.Parse("""{"tool":"web.search","callId":"1"}""");
        Assert.Equal("Suche im Web …", MeetingAgentService.ToolStatus(search.RootElement));
        using var other = System.Text.Json.JsonDocument.Parse("""{"tool":"something.else"}""");
        Assert.Equal("Nutze ein Werkzeug …", MeetingAgentService.ToolStatus(other.RootElement));
        var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung" };
        MeetingRoom.Normalize(state);
        Assert.Contains("web.search", MeetingAgentService.BuildPrompt(state, []), StringComparison.Ordinal);
    }
}
