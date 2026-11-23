using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Storage.Streams;

namespace Missum.App.Services.Meetings;

public sealed record MeetingDevice(string Id, string Name);

/// <summary>
/// Captures the local camera and emits small JPEG frames (about 640 px wide,
/// up to 12 per second). JPEG over the LAN keeps the transport simple and
/// robust; a slow network only drops frames, it never builds up delay.
/// </summary>
public sealed class MeetingCameraService : IAsyncDisposable
{
    private const int TargetWidth = 640;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 12);
    private readonly ILogger<MeetingCameraService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private DateTimeOffset _lastFrame;
    private int _encoding;

    public MeetingCameraService(ILogger<MeetingCameraService> logger) => _logger = logger;

    public bool IsRunning => _reader is not null;
    public event Action<byte[]>? FrameEncoded;

    public static async Task<IReadOnlyList<MeetingDevice>> ListAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups.Where(g => g.SourceInfos.Any(IsColorVideo))
            .Select(g => new MeetingDevice(g.Id, g.DisplayName))
            .ToList();
    }

    private static bool IsColorVideo(MediaFrameSourceInfo info) =>
        info.SourceKind == MediaFrameSourceKind.Color
        && info.MediaStreamType is Windows.Media.Capture.MediaStreamType.VideoPreview or Windows.Media.Capture.MediaStreamType.VideoRecord;

    public async Task StartAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_reader is not null) return;
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var group = groups.FirstOrDefault(g => g.Id == deviceId && g.SourceInfos.Any(IsColorVideo))
                ?? groups.FirstOrDefault(g => g.SourceInfos.Any(IsColorVideo))
                ?? throw new InvalidOperationException("Es wurde keine Kamera gefunden.");
            var sourceInfo = group.SourceInfos
                .Where(IsColorVideo)
                .OrderBy(info => info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview ? 0 : 1)
                .First();
            var capture = new MediaCapture();
            var exclusive = true;
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });
            }
            catch (UnauthorizedAccessException exception)
            {
                capture.Dispose();
                throw new InvalidOperationException(
                    "Windows erlaubt Missum keinen Kamerazugriff. Aktiviere unter Einstellungen › Datenschutz › Kamera den Zugriff für Desktop-Apps.", exception);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                // Another app may hold the camera exclusively; fall back to shared read-only.
                capture.Dispose();
                capture = new MediaCapture();
                exclusive = false;
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });
                _ = exception;
            }

            var source = capture.FrameSources[sourceInfo.Id];
            var format = source.SupportedFormats
                .Where(f => f.VideoFormat.Width >= 320)
                .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - TargetWidth))
                .ThenByDescending(f => f.FrameRate.Denominator == 0 ? 0 : f.FrameRate.Numerator / (double)f.FrameRate.Denominator)
                .FirstOrDefault();
            if (format is not null && exclusive)
            {
                try { await source.SetFormatAsync(format); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { _ = exception; }
            }
            var reader = await capture.CreateFrameReaderAsync(source);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                capture.Dispose();
                throw new InvalidOperationException(status == MediaFrameReaderStartStatus.DeviceNotAvailable
                    ? "Die Kamera wird gerade von einer anderen App verwendet."
                    : "Die Kamera konnte nicht gestartet werden.");
            }
            _capture = capture;
            _reader = reader;
        }
        finally { _gate.Release(); }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastFrame < FrameInterval || Interlocked.Exchange(ref _encoding, 1) == 1) return;
        _lastFrame = now;
        SoftwareBitmap? copy = null;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) { Volatile.Write(ref _encoding, 0); return; }
            copy = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            copy?.Dispose();
            Volatile.Write(ref _encoding, 0);
            MeetingLog.CameraFrameDropped(_logger, exception);
            return;
        }
        _ = EncodeAsync(copy);
    }

    private async Task EncodeAsync(SoftwareBitmap bitmap)
    {
        try
        {
            using (bitmap)
            {
                var jpeg = await EncodeJpegAsync(bitmap, TargetWidth).ConfigureAwait(false);
                FrameEncoded?.Invoke(jpeg);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MeetingLog.CameraFrameDropped(_logger, exception);
        }
        finally { Volatile.Write(ref _encoding, 0); }
    }

    internal static async Task<byte[]> EncodeJpegAsync(SoftwareBitmap bitmap, int width)
    {
        using var stream = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(0.62, Windows.Foundation.PropertyType.Single),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, properties);
        encoder.SetSoftwareBitmap(bitmap);
        if (bitmap.PixelWidth > width)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)width;
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, bitmap.PixelHeight * width / bitmap.PixelWidth);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var reader = _reader;
            var capture = _capture;
            _reader = null;
            _capture = null;
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                try { await reader.StopAsync(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { _ = exception; }
                reader.Dispose();
            }
            capture?.Dispose();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>
/// Microphone capture (PCM16, 16 kHz, mono, 100 ms frames) and a small mixer
/// that plays every other participant's audio through the default output.
/// </summary>
public sealed class MeetingAudioService : IDisposable
{
    public static readonly WaveFormat PcmFormat = new(16_000, 16, 1);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, BufferedWaveProvider> _voices = [];
    private readonly HashSet<Guid> _muted = [];
    private WaveInEvent? _capture;
    private WaveOutEvent? _output;
    private MixingSampleProvider? _mixer;

    public bool IsCapturing => _capture is not null;
    public event Action<byte[]>? Captured;
    /// <summary>Microphone level between 0 and 1, for the speaking indicator.</summary>
    public event Action<double>? Level;

    public static IReadOnlyList<MeetingDevice> ListMicrophones()
    {
        var devices = new List<MeetingDevice> { new("-1", "Standardmikrofon") };
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
            devices.Add(new MeetingDevice(index.ToString(System.Globalization.CultureInfo.InvariantCulture), WaveInEvent.GetCapabilities(index).ProductName));
        return devices;
    }

    public void StartCapture(string? deviceId)
    {
        lock (_gate)
        {
            if (_capture is not null) return;
            var device = int.TryParse(deviceId, out var number) && number >= -1 && number < WaveInEvent.DeviceCount ? number : -1;
            var capture = new WaveInEvent { DeviceNumber = device, WaveFormat = PcmFormat, BufferMilliseconds = 100 };
            capture.DataAvailable += OnData;
            try { capture.StartRecording(); }
            catch (NAudio.MmException exception)
            {
                capture.DataAvailable -= OnData;
                capture.Dispose();
                throw new InvalidOperationException("Das Mikrofon konnte nicht geöffnet werden. Prüfe in Windows den Mikrofonzugriff für Desktop-Apps.", exception);
            }
            _capture = capture;
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0) return;
        var frame = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
        Captured?.Invoke(frame);
        Level?.Invoke(Math.Clamp(UtteranceSegmenter.Rms(frame) * 6, 0, 1));
    }

    public void StopCapture()
    {
        WaveInEvent? capture;
        lock (_gate) { capture = _capture; _capture = null; }
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        try { capture.StopRecording(); }
        catch (NAudio.MmException) { }
        capture.Dispose();
    }

    public void SetMuted(Guid participant, bool muted)
    {
        lock (_gate)
        {
            if (muted) _muted.Add(participant); else _muted.Remove(participant);
            if (muted && _voices.TryGetValue(participant, out var voice)) voice.ClearBuffer();
        }
    }

    public bool IsMuted(Guid participant)
    {
        lock (_gate) return _muted.Contains(participant);
    }

    public void Play(Guid participant, byte[] pcm)
    {
        lock (_gate)
        {
            if (_muted.Contains(participant)) return;
            EnsureOutput();
            if (!_voices.TryGetValue(participant, out var voice))
            {
                voice = new BufferedWaveProvider(PcmFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                _voices[participant] = voice;
                _mixer!.AddMixerInput(voice.ToSampleProvider());
            }
            // Keep latency low: a voice that fell far behind restarts from now.
            if (voice.BufferedDuration > TimeSpan.FromMilliseconds(600)) voice.ClearBuffer();
            voice.AddSamples(pcm, 0, pcm.Length);
        }
    }

    private void EnsureOutput()
    {
        if (_output is not null) return;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(PcmFormat.SampleRate, 1)) { ReadFully = true };
        var output = new WaveOutEvent { DesiredLatency = 150 };
        output.Init(_mixer);
        output.Play();
        _output = output;
    }

    public void StopPlayback()
    {
        WaveOutEvent? output;
        lock (_gate)
        {
            output = _output;
            _output = null;
            _mixer = null;
            _voices.Clear();
        }
        if (output is null) return;
        try { output.Stop(); }
        catch (NAudio.MmException) { }
        output.Dispose();
    }

    public void Dispose()
    {
        StopCapture();
        StopPlayback();
    }
}
