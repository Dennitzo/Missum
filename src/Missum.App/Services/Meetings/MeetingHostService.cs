using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Missum.App.Services.Meetings;

public sealed record MeetingHostStatus(bool Running, int Port, string? Error, int Rooms, int Connections);

/// <summary>
/// The resident Claude Meetings host. It starts with Missum, stops with Missum
/// and serves every meeting this PC hosts over one Kestrel endpoint:
/// <c>/v1/meetings/{id}/ws</c> carries JSON operations (text frames) and
/// camera/microphone media (binary frames). The host replica is authoritative;
/// clients only send intents, which are validated, applied and fanned out.
/// </summary>
public sealed class MeetingHostService : IAsyncDisposable
{
    public const int DefaultPort = 8090;
    private const int PortAttempts = 10;
    private const int MaximumTextFrameBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1500);

    private readonly ILogger<MeetingHostService> _logger;
    private readonly ConcurrentDictionary<Guid, HostedMeeting> _rooms = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;
    private string? _error;
    private int _port;

    public MeetingHostService(ILogger<MeetingHostService> logger)
    {
        _logger = logger;
        HostKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    /// <summary>Process-local secret that identifies the host's own window as host.</summary>
    internal string HostKey { get; }

    public event EventHandler? StatusChanged;
    /// <summary>Raised after the host replica accepted an operation (on a worker thread).</summary>
    public event Action<Guid, MeetingOp>? OperationApplied;
    /// <summary>Raised for every PCM16/16 kHz audio frame of a live meeting participant.</summary>
    public event Action<Guid, Guid, string, byte[]>? AudioReceived;
    /// <summary>Raised when a participant explicitly asks Missum to work (button or @Missum).</summary>
    public event Action<Guid, MeetingOp>? AgentRequested;

    public bool IsRunning => _app is not null;
    public int Port => _port;

    public MeetingHostStatus Status => new(IsRunning, _port, _error, _rooms.Count,
        _rooms.Values.Sum(room => room.ConnectionCount));

    public async Task StartAsync(int preferredPort, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null) return;
            Exception? last = null;
            for (var attempt = 0; attempt < PortAttempts; attempt++)
            {
                var port = preferredPort + attempt;
                var app = Build(port);
                try
                {
                    await app.StartAsync(cancellationToken).ConfigureAwait(false);
                    _app = app;
                    _port = port;
                    _error = null;
                    MeetingLog.HostStarted(_logger, port);
                    break;
                }
                catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException
                                                      or InvalidOperationException)
                {
                    last = exception;
                    await app.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (_app is null)
            {
                _error = last?.Message ?? "Kein freier Port gefunden.";
                MeetingLog.HostStartFailed(_logger, last);
            }
        }
        finally
        {
            _lifecycle.Release();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var room in _rooms.Values)
            {
                await room.CloseAllAsync().ConfigureAwait(false);
                await room.FlushAsync().ConfigureAwait(false);
            }
            if (_app is { } app)
            {
                _app = null;
                try { await app.StopAsync(cancellationToken).ConfigureAwait(false); }
                finally { await app.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            _lifecycle.Release();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Makes a meeting reachable on this host. Calling it again for the same
    /// meeting keeps the running room and its connections.
    /// </summary>
    public string OpenRoom(MeetingRoomState state, Func<MeetingRoomState, Task> save, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var room = _rooms.GetOrAdd(state.Id, _ => new HostedMeeting(
            new MeetingRoom(state),
            string.IsNullOrWhiteSpace(token) ? NewToken() : token,
            save,
            _logger));
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return room.Token;
    }

    /// <summary>Returns a usable room token: the stored one when valid, otherwise a fresh one.</summary>
    public static string OpenRoomToken(string? stored) =>
        !string.IsNullOrWhiteSpace(stored) && stored.Length is >= 16 and <= 128 ? stored : NewToken();

    public bool HasRoom(Guid meetingId) => _rooms.ContainsKey(meetingId);

    public MeetingRoomState? Snapshot(Guid meetingId) =>
        _rooms.TryGetValue(meetingId, out var room) ? room.Room.Snapshot() : null;

    public T? Read<T>(Guid meetingId, Func<MeetingRoomState, T> reader) =>
        _rooms.TryGetValue(meetingId, out var room) ? room.Room.Read(reader) : default;

    public async Task CloseRoomAsync(Guid meetingId)
    {
        if (!_rooms.TryRemove(meetingId, out var room)) return;
        await room.CloseAllAsync().ConfigureAwait(false);
        await room.FlushAsync().ConfigureAwait(false);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies an operation that originates inside the host process (agent, transcription).</summary>
    public bool ApplyLocal(Guid meetingId, MeetingOp op)
    {
        if (!_rooms.TryGetValue(meetingId, out var room)) return false;
        return Commit(meetingId, room, op);
    }

    private bool Commit(Guid meetingId, HostedMeeting room, MeetingOp op)
    {
        MeetingOp stamped;
        // Apply and enqueue under one gate so every client receives operations
        // in exactly the order the host replica applied them.
        lock (room.CommitGate)
        {
            if (!room.Room.Apply(op)) return false;
            stamped = op with { Revision = room.Room.Read(static s => s.Revision) };
            room.Broadcast(new MeetingEnvelope { Kind = "op", Op = stamped });
        }
        if (stamped.Type != MeetingOpTypes.Presence) room.ScheduleSave();
        try { OperationApplied?.Invoke(meetingId, stamped); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MeetingLog.ObserverFailed(_logger, "operation", exception);
        }
        return true;
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "Missum.Meetings" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port);
            options.Limits.MaxRequestBodySize = 64 * 1024;
            options.AddServerHeader = false;
        });
        builder.Services.AddRouting();
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/v1/meetings/health", () => Results.Ok(new { ok = true, service = "missum-meetings" }));
        app.MapGet("/v1/meetings/{id}/info", (string id, HttpContext context) =>
        {
            if (!TryAuthorize(id, context, out var room, out _, out var failure)) return failure!;
            var info = room!.Room.Read(state => new
            {
                state.Id,
                state.Title,
                state.IsLive,
                participants = state.Participants.Count(p => p.Connected && !p.IsAgent),
            });
            return Results.Ok(info);
        });
        app.Map("/v1/meetings/{id}/ws", HandleSocketAsync);
        return app;
    }

    private bool TryAuthorize(string id, HttpContext context, out HostedMeeting? room, out bool isHost, out IResult? failure)
    {
        room = null;
        isHost = false;
        failure = null;
        if (!Guid.TryParseExact(id, "N", out var meetingId) || !_rooms.TryGetValue(meetingId, out room))
        {
            failure = Results.NotFound(new { error = "Dieses Meeting ist auf dem Host nicht geöffnet." });
            return false;
        }
        var token = context.Request.Query["t"].ToString();
        if (!FixedEquals(token, room.Token))
        {
            failure = Results.Json(new { error = "Der Einladungslink ist ungültig." }, statusCode: StatusCodes.Status403Forbidden);
            room = null;
            return false;
        }
        var hostKey = context.Request.Query["hk"].ToString();
        isHost = hostKey.Length > 0 && FixedEquals(hostKey, HostKey)
            && context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);
        return true;
    }

    private async Task HandleSocketAsync(string id, HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!TryAuthorize(id, context, out var room, out var isHost, out var failure))
        {
            await failure!.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        var meetingId = Guid.ParseExact(id, "N");
        if (!isHost && !room!.Room.Read(static s => s.IsLive))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "Das Meeting hat noch nicht begonnen." }).ConfigureAwait(false);
            return;
        }

        var name = MeetingRoom.Clip(context.Request.Query["name"].ToString(), 60, "Gast");
        var participantId = Guid.TryParseExact(context.Request.Query["pid"].ToString(), "N", out var requested)
                            && requested != Guid.Empty && requested != MeetingRoom.AgentId
            ? requested
            : Guid.NewGuid();
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connection = new MeetingConnection(socket, participantId, name, isHost);
        // A reconnect with the same identity replaces the stale connection.
        await room!.AddAsync(connection).ConfigureAwait(false);
        var writer = connection.RunWriterAsync(context.RequestAborted);
        try
        {
            Commit(meetingId, room, new MeetingOp
            {
                Type = MeetingOpTypes.ParticipantJoin,
                ActorId = participantId,
                ActorName = name,
                Participant = new MeetingParticipant { Id = participantId, Name = name, Role = isHost ? "host" : "guest" },
            });
            connection.Send(new MeetingEnvelope { Kind = "snapshot", State = room.Room.Snapshot(), You = participantId });
            await ReceiveLoopAsync(meetingId, room, connection, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // Network loss is an ordinary leave.
        }
        finally
        {
            var stillCurrent = room.Remove(connection);
            connection.Complete();
            try { await writer.ConfigureAwait(false); } catch (Exception exception) when (exception is not OutOfMemoryException) { }
            if (stillCurrent)
                Commit(meetingId, room, new MeetingOp
                {
                    Type = MeetingOpTypes.ParticipantLeave,
                    ActorId = participantId,
                    ParticipantId = participantId,
                });
        }
    }

    private async Task ReceiveLoopAsync(Guid meetingId, HostedMeeting room, MeetingConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (connection.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await connection.Socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaximumTextFrameBytes) return;
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                HandleMedia(meetingId, room, connection, message.ToArray());
                continue;
            }

            MeetingEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<MeetingEnvelope>(message.GetBuffer().AsSpan(0, (int)message.Length), MeetingJson.Options); }
            catch (JsonException) { continue; }
            if (envelope?.Kind == "ping") { connection.Send(new MeetingEnvelope { Kind = "pong" }); continue; }
            if (envelope?.Op is not { } op) continue;
            HandleClientOperation(meetingId, room, connection, op);
        }
    }

    private void HandleClientOperation(Guid meetingId, HostedMeeting room, MeetingConnection connection, MeetingOp op)
    {
        if (MeetingOpTypes.IsHostOnly(op.Type)) return;
        // Identity is the connection's, never the sender's claim.
        var trusted = op with { ActorId = connection.ParticipantId, ActorName = connection.Name, Revision = 0 };
        if (trusted.Type is MeetingOpTypes.RoomLive or MeetingOpTypes.RoomTitle && !connection.IsHost) return;
        if (trusted.Type == MeetingOpTypes.AgentRequest)
        {
            AgentRequested?.Invoke(meetingId, trusted);
            return;
        }
        if (!Commit(meetingId, room, trusted)) return;
        if (trusted.Type == MeetingOpTypes.ParticipantRename)
            connection.Name = room.Room.Read(s => s.Participants.FirstOrDefault(p => p.Id == connection.ParticipantId)?.Name) ?? connection.Name;
        if (trusted.Type == MeetingOpTypes.ChatAdd && trusted.Chat is { } chat && MentionsAgent(chat.Text))
            AgentRequested?.Invoke(meetingId, trusted);
    }

    private void HandleMedia(Guid meetingId, HostedMeeting room, MeetingConnection connection, byte[] frame)
    {
        if (!MeetingMediaFrame.TryRead(frame, out var kind, out _, out var payload)) return;
        MeetingMediaFrame.Stamp(frame, connection.ParticipantId);
        room.Relay(frame, connection);
        if (kind == MeetingMediaFrame.Audio && room.Room.Read(s => s.IsLive))
        {
            var pcm = payload.ToArray();
            try { AudioReceived?.Invoke(meetingId, connection.ParticipantId, connection.Name, pcm); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                MeetingLog.ObserverFailed(_logger, "audio", exception);
            }
        }
    }

    internal static bool MentionsAgent(string? text) =>
        text is not null && (text.Contains("@missum", StringComparison.OrdinalIgnoreCase)
            // Legacy alias from before the agent was renamed.
            || text.Contains("@claude", StringComparison.OrdinalIgnoreCase));

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left ?? "");
        var b = Encoding.UTF8.GetBytes(right ?? "");
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private sealed class HostedMeeting(MeetingRoom room, string token, Func<MeetingRoomState, Task> save, ILogger logger)
    {
        private readonly List<MeetingConnection> _connections = [];
        private readonly object _saveGate = new();
        private CancellationTokenSource? _pendingSave;
        private Task _lastSave = Task.CompletedTask;

        public MeetingRoom Room { get; } = room;
        public string Token { get; } = token;
        public object CommitGate { get; } = new();

        public int ConnectionCount { get { lock (_connections) return _connections.Count; } }

        public async Task AddAsync(MeetingConnection connection)
        {
            MeetingConnection[] stale;
            lock (_connections)
            {
                stale = _connections.Where(c => c.ParticipantId == connection.ParticipantId).ToArray();
                foreach (var item in stale) _connections.Remove(item);
                _connections.Add(connection);
            }
            foreach (var item in stale) await item.CloseAsync("Ersetzt durch neue Verbindung").ConfigureAwait(false);
        }

        public bool Remove(MeetingConnection connection)
        {
            lock (_connections) return _connections.Remove(connection);
        }

        public void Broadcast(MeetingEnvelope envelope)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, MeetingJson.Options);
            foreach (var connection in Snapshot()) connection.SendText(bytes);
        }

        public void Relay(byte[] frame, MeetingConnection sender)
        {
            foreach (var connection in Snapshot())
                if (!ReferenceEquals(connection, sender)) connection.SendMedia(frame);
        }

        public async Task CloseAllAsync()
        {
            foreach (var connection in Snapshot()) await connection.CloseAsync("Host beendet").ConfigureAwait(false);
        }

        public void ScheduleSave()
        {
            CancellationTokenSource next;
            lock (_saveGate)
            {
                _pendingSave?.Cancel();
                _pendingSave = next = new CancellationTokenSource();
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SaveDelay, next.Token).ConfigureAwait(false);
                    await SaveNowAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });
        }

        public async Task FlushAsync()
        {
            lock (_saveGate) { _pendingSave?.Cancel(); _pendingSave = null; }
            await SaveNowAsync().ConfigureAwait(false);
        }

        private async Task SaveNowAsync()
        {
            Task previous;
            TaskCompletionSource current = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_saveGate) { previous = _lastSave; _lastSave = current.Task; }
            try
            {
                await previous.ConfigureAwait(false);
                await save(Room.Snapshot()).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                MeetingLog.SaveFailed(logger, exception);
            }
            finally { current.TrySetResult(); }
        }

        private MeetingConnection[] Snapshot()
        {
            lock (_connections) return _connections.ToArray();
        }
    }

    private sealed class MeetingConnection(WebSocket socket, Guid participantId, string name, bool isHost)
    {
        private readonly Channel<byte[]> _control = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        // Media is lossy by design: a slow receiver drops old frames instead of growing a backlog.
        private readonly Channel<byte[]> _media = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        public WebSocket Socket { get; } = socket;
        public Guid ParticipantId { get; } = participantId;
        public string Name { get; set; } = name;
        public bool IsHost { get; } = isHost;

        public void Send(MeetingEnvelope envelope) => SendText(JsonSerializer.SerializeToUtf8Bytes(envelope, MeetingJson.Options));
        public void SendText(byte[] bytes) => _control.Writer.TryWrite(bytes);
        public void SendMedia(byte[] frame) => _media.Writer.TryWrite(frame);

        public void Complete()
        {
            _control.Writer.TryComplete();
            _media.Writer.TryComplete();
        }

        public async Task CloseAsync(string reason)
        {
            Complete();
            try
            {
                if (Socket.State == WebSocketState.Open)
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException) { }
        }

        public async Task RunWriterAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (Socket.State == WebSocketState.Open)
                {
                    if (_control.Reader.TryRead(out var text))
                    {
                        await Socket.SendAsync(text, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (_media.Reader.TryRead(out var media))
                    {
                        await Socket.SendAsync(media, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var controlReady = _control.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    var mediaReady = _media.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    var finished = await Task.WhenAny(controlReady, mediaReady).ConfigureAwait(false);
                    // Both channels complete together when the connection ends.
                    if (!await finished.ConfigureAwait(false)) return;
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The receive loop observes the same failure and performs the leave.
            }
        }
    }
}
