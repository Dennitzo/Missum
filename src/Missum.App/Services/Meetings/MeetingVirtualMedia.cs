using System.Runtime.InteropServices.WindowsRuntime;
using NAudio.Wave;
using Windows.Graphics.Imaging;

namespace Missum.App.Services.Meetings;

/// <summary>
/// Stand-in camera and microphone for PCs without devices and for live tests.
/// MISSUM_MEETING_VIRTUAL_MEDIA=1 replaces the real devices: the camera sends a
/// moving test picture, the microphone plays MISSUM_MEETING_VIRTUAL_MIC (a WAV
/// file) once in real time and then sends silence. Everything after capture –
/// transport, tiles, speaking level and transcription – runs unchanged.
/// </summary>
internal static class MeetingVirtualMedia
{
    public const string DeviceId = "virtual";
    private const int Width = 640;
    private const int Height = 360;

    public static bool Enabled => Environment.GetEnvironmentVariable("MISSUM_MEETING_VIRTUAL_MEDIA") == "1";

    public static MeetingDevice Camera => new(DeviceId, "Virtuelle Kamera (Testbild)");
    public static MeetingDevice Microphone => new(DeviceId, "Virtuelles Mikrofon (Testaufnahme)");

    /// <summary>Sends test frames until cancelled, at the camera's frame rate.</summary>
    public static async Task RunCameraAsync(Action<byte[]> frame, TimeSpan interval, CancellationToken cancellationToken)
    {
        var pixels = new byte[Width * Height * 4];
        var tick = 0;
        using var timer = new PeriodicTimer(interval);
        do
        {
            Paint(pixels, tick++);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, Width, Height, BitmapAlphaMode.Premultiplied);
            frame(await MeetingCameraService.EncodeJpegAsync(bitmap, Width).ConfigureAwait(false));
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A calm gradient with a sweeping bar and a running frame counter of blocks.</summary>
    internal static void Paint(byte[] pixels, int tick)
    {
        var bar = tick * 9 % (Width + 120) - 60;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 4;
                var inBar = x >= bar && x < bar + 60;
                pixels[i] = (byte)(inBar ? 255 : 120 + y * 100 / Height);      // B
                pixels[i + 1] = (byte)(inBar ? 255 : 60 + x * 80 / Width);     // G
                pixels[i + 2] = (byte)(inBar ? 255 : 40 + (x + y) * 60 / (Width + Height)); // R
                pixels[i + 3] = 255;
            }
        }
        // 16 blocks along the bottom show the frame number in binary: frames visibly change.
        for (var bit = 0; bit < 16; bit++)
        {
            var on = (tick >> bit & 1) == 1;
            for (var y = Height - 34; y < Height - 14; y++)
                for (var x = 20 + bit * 30; x < 40 + bit * 30; x++)
                {
                    var i = (y * Width + x) * 4;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = on ? (byte)250 : (byte)30;
                }
        }
    }

    /// <summary>Plays the test recording once in 100 ms frames, then keeps sending silence.</summary>
    public static async Task RunMicrophoneAsync(Action<byte[]> frame, CancellationToken cancellationToken)
    {
        var pcm = LoadRecording(Environment.GetEnvironmentVariable("MISSUM_MEETING_VIRTUAL_MIC"));
        var chunk = MeetingAudioService.PcmFormat.AverageBytesPerSecond / 10;
        var silence = new byte[chunk];
        var offset = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (offset < pcm.Length)
            {
                var length = Math.Min(chunk, pcm.Length - offset);
                var data = new byte[chunk];
                Array.Copy(pcm, offset, data, 0, length);
                offset += length;
                frame(data);
            }
            else frame(silence);
        }
    }

    /// <summary>Reads a WAV file as PCM16 16 kHz mono; other formats are converted.</summary>
    internal static byte[] LoadRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return [];
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm && reader.WaveFormat.SampleRate == 16_000
            && reader.WaveFormat.BitsPerSample == 16 && reader.WaveFormat.Channels == 1)
        {
            using var copy = new MemoryStream();
            reader.CopyTo(copy);
            return copy.ToArray();
        }
        using var converted = new MediaFoundationResampler(reader, MeetingAudioService.PcmFormat);
        using var output = new MemoryStream();
        var buffer = new byte[MeetingAudioService.PcmFormat.AverageBytesPerSecond];
        int read;
        while ((read = converted.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        return output.ToArray();
    }
}
