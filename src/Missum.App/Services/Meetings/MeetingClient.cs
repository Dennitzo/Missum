using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Missum.App.Services.Meetings;

public enum MeetingConnectionState { Disconnected, Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// One participant's connection to a meeting host. The host's own window uses
/// it over loopback too, so there is exactly one code path for every replica.
/// Lost connections reconnect with backoff and resynchronize from a snapshot.
/// </summary>
public sealed class MeetingClient : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    private readonly Uri _endpoint;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<byte[]> _control = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _media = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(24)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private Task? _run;
    private TaskCompletionSource _firstSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MeetingClient(Uri endpoint, Guid participantId)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ParticipantId = participantId;
    }

    public Guid ParticipantId { get; private set; }
    public MeetingRoom? Room { get; private set; }
    public MeetingConnectionState State { get; private set; } = MeetingConnectionState.Disconnected;
    public string? LastError { get; private set; }

    /// <summary>A full replica arrived (first connect or resynchronization).</summary>
    public event Action<MeetingRoomState>? SnapshotReceived;
    /// <summary>An operation was applied to the local replica.</summary>
    public event Action<MeetingOp>? OperationApplied;
    public event Action<Guid, byte[]>? VideoReceived;
    public event Action<Guid, byte[]>? AudioReceived;
    public event Action<MeetingConnectionState, string?>? StateChanged;

    /// <summary>Connects and waits until the first snapshot is applied.</summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _run ??= Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(timeout);
        try
        {
            await _firstSnapshot.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(LastError ?? "Der Meeting-Host antwortet nicht.");
        }
    }

    public void Send(MeetingOp op) =>
        _control.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new MeetingEnvelope { Kind = "op", Op = op }, MeetingJson.Options));

    public void SendMedia(byte kind, ReadOnlySpan<byte> payload)
    {
        if (State != MeetingConnectionState.Connected) return;
        _media.Writer.TryWrite(MeetingMediaFrame.Create(kind, ParticipantId, payload));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(attempt == 0 ? MeetingConnectionState.Connecting : MeetingConnectionState.Reconnecting, null);
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    await socket.ConnectAsync(_endpoint, connectTimeout.Token).ConfigureAwait(false);
                }
                attempt = 0;
                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var writer = WriteLoopAsync(socket, session.Token);
                try { await ReadLoopAsync(socket, session.Token).ConfigureAwait(false); }
                finally
                {
                    await session.CancelAsync().ConfigureAwait(false);
                    try { await writer.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                if (socket.CloseStatusDescription is { Length: > 0 } reason && reason.StartsWith("Ersetzt", StringComparison.Ordinal))
                {
                    SetState(MeetingConnectionState.Disconnected, reason);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
            {
                LastError = Describe(exception);
            }

            if (cancellationToken.IsCancellationRequested) break;
            var delay = Backoff[Math.Min(attempt, Backoff.Length - 1)];
            attempt++;
            SetState(MeetingConnectionState.Reconnecting, LastError);
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        SetState(MeetingConnectionState.Disconnected, null);
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var frame = message.ToArray();
                if (!MeetingMediaFrame.TryRead(frame, out var kind, out var participant, out var payload)) continue;
                var bytes = payload.ToArray();
                if (kind == MeetingMediaFrame.Video) VideoReceived?.Invoke(participant, bytes);
                else AudioReceived?.Invoke(participant, bytes);
                continue;
            }

            MeetingEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<MeetingEnvelope>(message.GetBuffer().AsSpan(0, (int)message.Length), MeetingJson.Options); }
            catch (JsonException) { continue; }
            if (envelope is null) continue;
            switch (envelope.Kind)
            {
                case "snapshot" when envelope.State is { } state:
                    if (envelope.You is { } you) ParticipantId = you;
                    if (Room is null) Room = new MeetingRoom(state);
                    else Room.Replace(state);
                    SetState(MeetingConnectionState.Connected, null);
                    SnapshotReceived?.Invoke(Room.Snapshot());
                    _firstSnapshot.TrySetResult();
                    break;
                case "op" when envelope.Op is { } op && Room is not null:
                    if (Room.Apply(op)) OperationApplied?.Invoke(op);
                    break;
                case "error":
                    LastError = envelope.Message;
                    break;
            }
        }
    }

    private async Task WriteLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            if (_control.Reader.TryRead(out var text))
            {
                await socket.SendAsync(text, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (_media.Reader.TryRead(out var media))
            {
                await socket.SendAsync(media, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var controlReady = _control.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var mediaReady = _media.Reader.WaitToReadAsync(cancellationToken).AsTask();
            if (!await (await Task.WhenAny(controlReady, mediaReady).ConfigureAwait(false)).ConfigureAwait(false)) return;
        }
    }

    private void SetState(MeetingConnectionState state, string? error)
    {
        if (error is not null) LastError = error;
        if (State == state && error is null) return;
        State = state;
        StateChanged?.Invoke(state, error);
    }

    private static string Describe(Exception exception) => exception switch
    {
        WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket } =>
            "Der Host hat die Verbindung abgelehnt. Prüfe den Link oder ob das Meeting gestartet ist.",
        WebSocketException => "Der Meeting-Host ist nicht erreichbar. Prüfe Netzwerk und Windows-Firewall auf dem Host.",
        OperationCanceledException => "Zeitüberschreitung beim Verbinden mit dem Meeting-Host.",
        _ => exception.Message,
    };

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _control.Writer.TryComplete();
        _media.Writer.TryComplete();
        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        _lifetime.Dispose();
    }
}
