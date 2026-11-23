using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Missum.App.Services.Meetings;

public sealed record MeetingView(
    Guid SessionId,
    string Role,
    MeetingConnectionState Connection,
    bool IsLive,
    Guid LocalParticipantId,
    bool MicOn,
    bool CameraOn,
    string? ShareLink,
    string? Error,
    string? Folder,
    bool NeedsProject = false);

/// <summary>
/// The window-facing side of Claude Meetings. Each Claude Meetings session owns
/// exactly one meeting stored in its project folder. As host, this service
/// opens the room on the resident <see cref="MeetingHostService"/> and connects
/// the window over loopback; as guest it connects to another PC's host. It
/// also drives the camera, the microphone and the playback mixer.
/// </summary>
public sealed class MeetingSessionService : IAsyncDisposable
{
    private readonly MeetingHostService _host;
    private readonly MeetingCameraService _camera;
    private readonly MeetingAudioService _audio;
    private readonly SettingsCoordinator _settings;
    private readonly AssistantRuntimeProfile _profile;
    private readonly ILogger<MeetingSessionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MeetingClient? _client;
    private MeetingRoom? _offline;
    private MeetingFolderInfo? _info;
    private string? _folder;
    private string? _workspace;
    private string? _hostToken;
    private MeetingLink? _guestLink;
    private Guid _sessionId;
    private string _role = "host";
    private bool _micOn;
    private bool _cameraOn;
    private string? _error;
    private bool _needsProject;
    // Media switches are serialized: rapid clicks can never leave camera or microphone half-open.
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private CancellationTokenSource? _mirrorSave;

    public MeetingSessionService(
        MeetingHostService host,
        MeetingCameraService camera,
        MeetingAudioService audio,
        SettingsCoordinator settings,
        AssistantRuntimeProfile profile,
        ILogger<MeetingSessionService> logger)
    {
        _host = host;
        _camera = camera;
        _audio = audio;
        _settings = settings;
        _profile = profile;
        _logger = logger;
        _camera.FrameEncoded += OnLocalFrame;
        _audio.Captured += OnLocalAudio;
    }

    /// <summary>Connection, role or media changed. Raised on worker threads.</summary>
    public event Action? Changed;
    /// <summary>The replica was replaced entirely (join, reconnect, offline load).</summary>
    public event Action? SnapshotChanged;
    public event Action<MeetingOp>? OperationApplied;
    /// <summary>A JPEG frame for a participant tile, including this PC's own preview.</summary>
    public event Action<Guid, byte[]>? VideoFrame;
    public event Action<double>? MicrophoneLevel;
    /// <summary>Speaking level of a remote participant, derived from the audio they send.</summary>
    public event Action<Guid, double>? ParticipantLevel;

    public Guid SessionId => _sessionId;
    public MeetingRoom? Room => _client?.Room ?? _offline;
    public Guid LocalParticipantId { get; private set; }
    public string DisplayName => string.IsNullOrWhiteSpace(_settings.Current.MeetingDisplayName)
        ? Environment.UserName
        : _settings.Current.MeetingDisplayName!.Trim();

    public MeetingView View => new(
        _sessionId,
        _role,
        _client?.State ?? MeetingConnectionState.Disconnected,
        Room?.Read(static s => s.IsLive) ?? false,
        LocalParticipantId,
        _micOn,
        _cameraOn,
        ShareLink(),
        _error ?? (_client?.State is MeetingConnectionState.Reconnecting or MeetingConnectionState.Failed ? _client.LastError : null),
        _folder,
        _needsProject);

    public const string ProjectRequiredMessage = "Lege zuerst links ein Projekt an. Meetings werden im Projektordner gespeichert.";

    public MeetingAudioService Audio => _audio;

    /// <summary>A join link that arrived through protocol activation and waits for the window.</summary>
    public MeetingLink? PendingJoin { get; private set; }
    public event Action? JoinRequested;

    public void RequestJoin(MeetingLink link)
    {
        PendingJoin = link;
        JoinRequested?.Invoke();
    }

    public MeetingLink? TakePendingJoin()
    {
        var link = PendingJoin;
        PendingJoin = null;
        return link;
    }

    /// <summary>
    /// Opens (or creates) the meeting of a Claude Meetings session. Calling it
    /// again for the active session only refreshes nothing; switching to another
    /// session leaves the previous meeting first.
    /// </summary>
    public async Task ActivateAsync(Guid sessionId, string? title, string? workspacePath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionId == _sessionId && (Room is not null || (_needsProject && workspacePath == _workspace))) return;
            await LeaveCoreAsync().ConfigureAwait(false);
            _sessionId = sessionId;
            _workspace = workspacePath;
            _error = null;
            LocalParticipantId = StableParticipantId(sessionId);
            var root = MeetingProjectStore.ResolveRoot(workspacePath);
            _needsProject = root is null;
            if (root is null)
            {
                // No project: no room, no files. The window shows "Projekt erforderlich".
                SnapshotChanged?.Invoke();
                return;
            }
            var folder = MeetingProjectStore.FindFolder(root, sessionId);
            (MeetingFolderInfo Info, MeetingRoomState State)? loaded = null;
            if (folder is not null)
            {
                try { loaded = await MeetingProjectStore.LoadAsync(folder, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    MeetingLog.LoadFailed(_logger, exception);
                    _error = "Die gespeicherten Meeting-Dateien konnten nicht gelesen werden.";
                }
            }

            if (loaded is { Info.Role: "guest" } guest)
            {
                // A guest's copy is a read-only mirror until the user joins again.
                _role = "guest";
                _info = guest.Info;
                _folder = folder;
                _offline = new MeetingRoom(guest.State);
                _guestLink = MeetingLink.TryParse(guest.Info.JoinLink, out var link) ? link : null;
                SnapshotChanged?.Invoke();
                return;
            }

            var state = loaded?.State ?? new MeetingRoomState
            {
                Id = Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(title) ? "Meeting" : title.Trim(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            MeetingRoom.Normalize(state);
            _role = "host";
            _folder = folder ?? MeetingProjectStore.CreateFolderPath(root, sessionId, state.Title, state.CreatedAt);
            _hostToken = loaded?.Info.HostToken;
            _hostToken = _host.IsRunning ? MeetingHostService.OpenRoomToken(_hostToken) : _hostToken;
            _info = new MeetingFolderInfo(sessionId, state.Id, "host", null, state.Title, state.CreatedAt, _hostToken);
            if (loaded is null || loaded.Value.Info.HostToken != _hostToken) await SaveHostAsync(state).ConfigureAwait(false);
            await OpenHostedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private async Task OpenHostedAsync(MeetingRoomState state, CancellationToken cancellationToken)
    {
        if (!_host.IsRunning)
        {
            // The host normally starts with Missum; if it failed, show the meeting offline.
            _offline = new MeetingRoom(state);
            _error = "Der Meeting-Host läuft nicht. Notes und Whiteboard sind nur lokal bearbeitbar.";
            SnapshotChanged?.Invoke();
            return;
        }
        _hostToken = _host.OpenRoom(state, SaveHostAsync, _hostToken);
        var link = new MeetingLink("127.0.0.1", _host.Port, state.Id, _hostToken);
        await ConnectAsync(link.WebSocketUri(DisplayName, LocalParticipantId, _host.HostKey), cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var client = new MeetingClient(endpoint, LocalParticipantId);
        client.SnapshotReceived += _ => SnapshotChanged?.Invoke();
        client.OperationApplied += OnRemoteOperation;
        client.VideoReceived += (participant, jpeg) => VideoFrame?.Invoke(participant, jpeg);
        client.AudioReceived += (participant, pcm) =>
        {
            ParticipantLevel?.Invoke(participant, Math.Clamp(UtteranceSegmenter.Rms(pcm) * 6, 0, 1));
            if (IsLive) _audio.Play(participant, pcm);
        };
        client.StateChanged += (_, _) => Changed?.Invoke();
        _client = client;
        _offline = null;
        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            LocalParticipantId = client.ParticipantId;
        }
        catch
        {
            _client = null;
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool IsLive => Room?.Read(static s => s.IsLive) ?? false;

    private void OnRemoteOperation(MeetingOp op)
    {
        OperationApplied?.Invoke(op);
        if (op.Type == MeetingOpTypes.RoomLive)
        {
            if (op.Flag != true)
            {
                // The host ended the meeting: media stops everywhere.
                _ = StopMediaAsync();
            }
            Changed?.Invoke();
        }
        if (_role == "guest") ScheduleMirrorSave();
    }

    /// <summary>Joins another PC's meeting from a shared link inside this session.</summary>
    public async Task JoinAsync(Guid sessionId, string? workspacePath, MeetingLink link, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LeaveCoreAsync().ConfigureAwait(false);
            _sessionId = sessionId;
            _workspace = workspacePath;
            _role = "guest";
            _guestLink = link;
            _error = null;
            LocalParticipantId = StableParticipantId(sessionId);
            var root = MeetingProjectStore.ResolveRoot(workspacePath);
            if (root is null)
            {
                _needsProject = true;
                _error = ProjectRequiredMessage;
                throw new InvalidOperationException(ProjectRequiredMessage);
            }
            _needsProject = false;
            var share = link.ToShareString(AssistantSessionUri.ProtocolScheme(_profile.Name));
            try
            {
                await ConnectAsync(link.WebSocketUri(DisplayName, LocalParticipantId), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _error = exception.Message;
                throw;
            }
            var state = Room!.Snapshot();
            _folder = MeetingProjectStore.FindFolder(root, sessionId)
                ?? MeetingProjectStore.CreateFolderPath(root, sessionId, state.Title, state.CreatedAt);
            _info = new MeetingFolderInfo(sessionId, state.Id, "guest", share, state.Title, state.CreatedAt);
            ScheduleMirrorSave();
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Reconnects a guest session to the meeting it was created for.</summary>
    public async Task RejoinAsync(CancellationToken cancellationToken = default)
    {
        if (_role != "guest" || _guestLink is null) throw new InvalidOperationException("Für diese Sitzung ist kein Einladungslink gespeichert.");
        await JoinAsync(_sessionId, _workspace, _guestLink, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartMeetingAsync(bool microphone, bool camera)
    {
        if (_needsProject) throw new InvalidOperationException(ProjectRequiredMessage);
        if (_role != "host" || _client is null) throw new InvalidOperationException("Nur der Host kann das Meeting starten.");
        _client.Send(new MeetingOp { Type = MeetingOpTypes.RoomLive, Flag = true });
        // The live flag arrives through the host; media may only start once the replica agrees.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!IsLive && DateTime.UtcNow < deadline) await Task.Delay(50).ConfigureAwait(false);
        if (!IsLive) throw new InvalidOperationException("Der Meeting-Host hat den Start nicht bestätigt.");
        await SetMicrophoneAsync(microphone).ConfigureAwait(false);
        await SetCameraAsync(camera).ConfigureAwait(false);
    }

    public async Task EndMeetingAsync()
    {
        await StopMediaAsync().ConfigureAwait(false);
        if (_role == "host") _client?.Send(new MeetingOp { Type = MeetingOpTypes.RoomLive, Flag = false });
        else await LeaveAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    public bool MediaBusy => _mediaGate.CurrentCount == 0;

    public async Task SetMicrophoneAsync(bool on)
    {
        await _mediaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _error = null;
            Changed?.Invoke();
            try
            {
                if (on && !IsLive) throw new InvalidOperationException("Das Meeting läuft noch nicht.");
                if (on) { _audio.StopCapture(); _audio.StartCapture(_settings.Current.MeetingMicrophoneDevice); }
                else _audio.StopCapture();
                _micOn = on;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _audio.StopCapture();
                _micOn = false;
                _error = exception.Message;
            }
            PublishMedia();
        }
        finally { _mediaGate.Release(); Changed?.Invoke(); }
    }

    public async Task SetCameraAsync(bool on)
    {
        await _mediaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _error = null;
            Changed?.Invoke();
            try
            {
                if (on && !IsLive) throw new InvalidOperationException("Das Meeting läuft noch nicht.");
                // Always release first so a stale or half-started capture cannot block the device.
                await _camera.StopAsync().ConfigureAwait(false);
                if (on) await _camera.StartAsync(_settings.Current.MeetingCameraDeviceId).ConfigureAwait(false);
                _cameraOn = on;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _cameraOn = false;
                _error = exception.Message;
                try { await _camera.StopAsync().ConfigureAwait(false); } catch (Exception inner) when (inner is not OutOfMemoryException) { _ = inner; }
            }
            PublishMedia();
        }
        finally { _mediaGate.Release(); Changed?.Invoke(); }
    }

    /// <summary>Applies a new name and devices from the settings flyout to the running meeting.</summary>
    public async Task ApplySettingsAsync(string? displayName, string? cameraId, string? microphoneId)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? null : MeetingRoom.Clip(displayName, 60, "");
        var current = _settings.Current;
        var cameraChanged = current.MeetingCameraDeviceId != cameraId;
        var micChanged = current.MeetingMicrophoneDevice != microphoneId;
        await _settings.UpdateAsync(value => value with
        {
            MeetingDisplayName = name,
            MeetingCameraDeviceId = cameraId,
            MeetingMicrophoneDevice = microphoneId,
        }).ConfigureAwait(false);
        _client?.Send(new MeetingOp { Type = MeetingOpTypes.ParticipantRename, Text = DisplayName });
        if (cameraChanged && _cameraOn) await SetCameraAsync(true).ConfigureAwait(false);
        if (micChanged && _micOn) await SetMicrophoneAsync(true).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private void PublishMedia()
    {
        _client?.Send(new MeetingOp
        {
            Type = MeetingOpTypes.ParticipantMedia,
            Participant = new MeetingParticipant { Id = LocalParticipantId, MicOn = _micOn, CameraOn = _cameraOn },
        });
        Changed?.Invoke();
    }

    private async Task StopMediaAsync()
    {
        _audio.StopCapture();
        _audio.StopPlayback();
        try { await _camera.StopAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _ = exception; }
        var changed = _micOn || _cameraOn;
        _micOn = false;
        _cameraOn = false;
        if (changed) PublishMedia();
    }

    /// <summary>Sends an edit. Offline (host not running) edits apply locally and are saved.</summary>
    public void Send(MeetingOp op)
    {
        var stamped = op with { ActorId = LocalParticipantId, ActorName = DisplayName };
        if (_client is not null)
        {
            _client.Send(stamped);
            return;
        }
        if (_offline is not null && _role == "host" && _offline.Apply(stamped))
        {
            OperationApplied?.Invoke(stamped);
            _ = SaveHostAsync(_offline.Snapshot());
        }
    }

    public void SendChat(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return;
        Send(new MeetingOp
        {
            Type = MeetingOpTypes.ChatAdd,
            Chat = new MeetingChatMessage(Guid.NewGuid(), LocalParticipantId, DisplayName, value, DateTimeOffset.UtcNow),
        });
    }

    public void RequestAgent(string reason = "organize") =>
        Send(new MeetingOp { Type = MeetingOpTypes.AgentRequest, Text = reason });

    public bool CanEdit => _client?.State == MeetingConnectionState.Connected || (_offline is not null && _role == "host");

    public async Task LeaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var guest = _role == "guest";
            var sessionId = _sessionId;
            var workspace = _workspace;
            var link = _guestLink;
            var info = _info;
            var folder = _folder;
            var snapshot = Room?.Snapshot();
            await LeaveCoreAsync().ConfigureAwait(false);
            if (guest && snapshot is not null)
            {
                // Keep the guest's mirror visible after leaving.
                _sessionId = sessionId;
                _workspace = workspace;
                _role = "guest";
                _guestLink = link;
                _info = info;
                _folder = folder;
                _offline = new MeetingRoom(snapshot);
                SnapshotChanged?.Invoke();
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private async Task LeaveCoreAsync()
    {
        await StopMediaAsync().ConfigureAwait(false);
        var client = _client;
        _client = null;
        if (client is not null)
        {
            if (_role == "guest" && client.Room is { } room && _folder is not null && _info is not null)
                await SaveMirrorNowAsync(room.Snapshot()).ConfigureAwait(false);
            if (_role == "host" && client.Room?.Read(static s => s.IsLive) == true)
                client.Send(new MeetingOp { Type = MeetingOpTypes.RoomLive, Flag = false });
            await Task.Delay(100).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
        if (_role == "host" && _info is not null) await _host.CloseRoomAsync(_info.MeetingId).ConfigureAwait(false);
        _offline = null;
        _info = null;
        _folder = null;
        _hostToken = null;
        _sessionId = Guid.Empty;
        _needsProject = false;
    }

    private string? ShareLink()
    {
        if (_role == "guest") return _info?.JoinLink;
        if (_info is null || _hostToken is null || !_host.IsRunning) return null;
        return new MeetingLink(MeetingLink.LocalNetworkAddress(), _host.Port, _info.MeetingId, _hostToken)
            .ToShareString(AssistantSessionUri.ProtocolScheme(_profile.Name));
    }

    private void OnLocalFrame(byte[] jpeg)
    {
        VideoFrame?.Invoke(LocalParticipantId, jpeg);
        _client?.SendMedia(MeetingMediaFrame.Video, jpeg);
    }

    private void OnLocalAudio(byte[] pcm)
    {
        _client?.SendMedia(MeetingMediaFrame.Audio, pcm);
        MicrophoneLevel?.Invoke(Math.Clamp(UtteranceSegmenter.Rms(pcm) * 6, 0, 1));
    }

    private async Task SaveHostAsync(MeetingRoomState state)
    {
        var folder = _folder;
        var info = _info;
        if (folder is null || info is null || info.MeetingId != state.Id) return;
        await MeetingProjectStore.SaveAsync(folder, info with { Title = state.Title }, state).ConfigureAwait(false);
    }

    private void ScheduleMirrorSave()
    {
        var next = new CancellationTokenSource();
        Interlocked.Exchange(ref _mirrorSave, next)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), next.Token).ConfigureAwait(false);
                if (Room?.Snapshot() is { } snapshot) await SaveMirrorNowAsync(snapshot).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task SaveMirrorNowAsync(MeetingRoomState state)
    {
        var folder = _folder;
        var info = _info;
        if (folder is null || info is null) return;
        try { await MeetingProjectStore.SaveAsync(folder, info with { Title = state.Title }, state).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MeetingLog.SaveFailed(_logger, exception);
        }
    }

    private static Guid StableParticipantId(Guid sessionId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName + "|" + sessionId.ToString("N")));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await LeaveCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        _camera.FrameEncoded -= OnLocalFrame;
        _audio.Captured -= OnLocalAudio;
        _audio.Dispose();
    }
}
