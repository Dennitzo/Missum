using Missum.App.Services.Meetings;
using NAudio.Wave;

namespace Missum.Tests;

public sealed class MeetingVirtualMediaTests
{
    [Fact]
    public void TestPictureChangesFromFrameToFrame()
    {
        var first = new byte[640 * 360 * 4];
        var second = new byte[640 * 360 * 4];
        MeetingVirtualMedia.Paint(first, 1);
        MeetingVirtualMedia.Paint(second, 2);
        Assert.NotEqual(first, second);
        Assert.All(Enumerable.Range(0, 100), i => Assert.Equal(255, first[i * 4 + 3]));
    }

    [Fact]
    public void RecordingIsReadAsPcm16MonoAt16Kilohertz()
    {
        var path = Path.Combine(Path.GetTempPath(), "missum-virtual-mic-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(path, MeetingAudioService.PcmFormat))
                writer.Write(new byte[32_000], 0, 32_000);
            Assert.Equal(32_000, MeetingVirtualMedia.LoadRecording(path).Length);
            Assert.Empty(MeetingVirtualMedia.LoadRecording(null));
            Assert.Empty(MeetingVirtualMedia.LoadRecording(path + ".missing"));
        }
        finally { File.Delete(path); }
    }
}

public sealed class MeetingAgentPromptTests
{
    [Fact]
    public void PlaceholderTitleAsksMissumToNameTheMeeting()
    {
        var state = new MeetingRoomState { Title = "Neue Sitzung" };
        var prompt = MeetingAgentService.BuildPrompt(state, []);
        Assert.Contains("noch ohne Titel", prompt, StringComparison.Ordinal);
        state.Title = "Kundenevent März";
        Assert.DoesNotContain("noch ohne Titel", MeetingAgentService.BuildPrompt(state, []), StringComparison.Ordinal);
        // A placeholder locked by an early sync is still no real title.
        state.Title = "Neue Sitzung";
        state.TitleLocked = true;
        Assert.Contains("noch ohne Titel", MeetingAgentService.BuildPrompt(state, []), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Neue Sitzung", true)]
    [InlineData("Meeting", true)]
    [InlineData("", true)]
    [InlineData("Kundenevent März", false)]
    public void RecognizesPlaceholderTitles(string title, bool placeholder) =>
        Assert.Equal(placeholder, MeetingAgentService.IsPlaceholderTitle(title));
}

public sealed class MeetingPlaceholderTitleTests
{
    [Fact]
    public void PlaceholderFromTheHostDoesNotLockTheTitle()
    {
        var host = Guid.NewGuid();
        var room = new MeetingRoom(new MeetingRoomState { Id = Guid.NewGuid() });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.ParticipantJoin, ActorId = host, Participant = new MeetingParticipant { Id = host, Name = "Dennis", Role = "host" } });
        room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = host, Text = "Neue Sitzung" });
        Assert.False(room.Snapshot().TitleLocked);
        // Missum may name it, but never back to a placeholder.
        Assert.True(room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = MeetingRoom.AgentId, Text = "Kundenevent März" }));
        Assert.False(room.Apply(new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = MeetingRoom.AgentId, Text = "Meeting" }));
        Assert.Equal("Kundenevent März", room.Snapshot().Title);
    }
}
