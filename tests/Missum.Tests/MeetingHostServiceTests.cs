using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services.Meetings;

namespace Missum.Tests;

public sealed class MeetingHostServiceTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task HostSynchronizesTwoClientsRelaysMediaAndRejectsBadTokens()
    {
        await using var host = new MeetingHostService(NullLogger<MeetingHostService>.Instance);
        var port = FreePort();
        await host.StartAsync(port);
        Assert.True(host.IsRunning);
        var saved = new List<MeetingRoomState>();
        var state = new MeetingRoomState { Id = Guid.NewGuid(), Title = "Planung Kundenveranstaltung" };
        MeetingRoom.Normalize(state);
        var token = host.OpenRoom(state, snapshot => { lock (saved) saved.Add(snapshot); return Task.CompletedTask; });
        var link = new MeetingLink("127.0.0.1", host.Port, state.Id, token);

        var hostId = Guid.NewGuid();
        await using var hostClient = new MeetingClient(link.WebSocketUri("Du", hostId, host.HostKey), hostId);
        await hostClient.ConnectAsync(TimeSpan.FromSeconds(10));

        // Guests cannot enter before the host starts the meeting.
        var early = new MeetingClient(link.WebSocketUri("Ines", Guid.NewGuid()), Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => early.ConnectAsync(TimeSpan.FromSeconds(3)));
        await early.DisposeAsync();

        hostClient.Send(new MeetingOp { Type = MeetingOpTypes.RoomLive, Flag = true });
        await WaitUntilAsync(() => host.Read(state.Id, s => s.IsLive));

        var guestId = Guid.NewGuid();
        await using var guest = new MeetingClient(link.WebSocketUri("Ines", guestId), guestId);
        await guest.ConnectAsync(TimeSpan.FromSeconds(10));
        Assert.True(guest.Room!.Read(s => s.IsLive));

        // A guest may not impersonate the host or send host-only operations.
        guest.Send(new MeetingOp { Type = MeetingOpTypes.RoomLive, Flag = false });
        guest.Send(new MeetingOp { Type = MeetingOpTypes.WhiteboardSet, Whiteboard = new MeetingWhiteboard() });
        var agentRequests = 0;
        host.AgentRequested += (_, _) => Interlocked.Increment(ref agentRequests);
        guest.SendChat(hostId, "Ines", "Hallo zusammen");
        guest.SendChat(hostId, "Ines", "Und eine Frage an @claude");
        await WaitUntilAsync(() => hostClient.Room!.Read(s => s.Chat.Count) == 2);
        var chat = hostClient.Room!.Read(s => s.Chat.ToList());
        Assert.All(chat, message => Assert.Equal(guestId, message.AuthorId));
        Assert.All(chat, message => Assert.Equal("Ines", message.AuthorName));
        Assert.True(hostClient.Room.Read(s => s.IsLive));
        await WaitUntilAsync(() => Volatile.Read(ref agentRequests) == 1);

        var note = new WhiteboardNote { Text = "Location buchen", ColumnId = state.Whiteboard.Columns[0].Id };
        hostClient.Send(new MeetingOp { Type = MeetingOpTypes.NoteUpsert, Note = note });
        await WaitUntilAsync(() => guest.Room!.Read(s => s.Whiteboard.Notes.Any(n => n.Id == note.Id)));
        Assert.Equal(hostClient.Room.Read(s => s.Revision), guest.Room!.Read(s => s.Revision));

        var video = new TaskCompletionSource<(Guid, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostClient.VideoReceived += (participant, bytes) => video.TrySetResult((participant, bytes));
        var audio = new TaskCompletionSource<(Guid, string, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AudioReceived += (_, participant, name, pcm) => audio.TrySetResult((participant, name, pcm));
        guest.SendMedia(MeetingMediaFrame.Video, [0xFF, 0xD8, 0xFF]);
        guest.SendMedia(MeetingMediaFrame.Audio, new byte[320]);
        var (from, frame) = await video.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(guestId, from);
        Assert.Equal([0xFF, 0xD8, 0xFF], frame);
        var heard = await audio.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(guestId, heard.Item1);
        Assert.Equal("Ines", heard.Item2);

        using (var intruder = new ClientWebSocket())
        {
            var bad = new MeetingLink("127.0.0.1", host.Port, state.Id, "falsch-falsch-falsch").WebSocketUri("Mallory", Guid.NewGuid());
            await Assert.ThrowsAnyAsync<WebSocketException>(() => intruder.ConnectAsync(bad, CancellationToken.None));
        }

        await guest.DisposeAsync();
        await WaitUntilAsync(() => !hostClient.Room.Read(s => s.Participants.Single(p => p.Id == guestId).Connected));

        await host.StopAsync();
        Assert.False(host.IsRunning);
        lock (saved) Assert.NotEmpty(saved);
        Assert.Contains(saved.Last().Whiteboard.Notes, n => n.Id == note.Id);
        // The port is released again.
        using var reuse = new TcpListener(IPAddress.Any, port);
        reuse.Start();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Bedingung wurde nicht erreicht.");
            await Task.Delay(25);
        }
    }
}

internal static class MeetingClientTestExtensions
{
    public static void SendChat(this MeetingClient client, Guid claimedAuthor, string claimedName, string text) =>
        client.Send(new MeetingOp
        {
            Type = MeetingOpTypes.ChatAdd,
            ActorId = claimedAuthor,
            ActorName = "Gefälscht",
            Chat = new MeetingChatMessage(Guid.NewGuid(), claimedAuthor, claimedName, text, DateTimeOffset.UtcNow),
        });
}
