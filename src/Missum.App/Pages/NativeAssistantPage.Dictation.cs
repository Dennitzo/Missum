using System.Threading.Channels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.Wave;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private WaveInEvent? _dictationCapture;
    private Channel<byte[]>? _dictationFrames;
    private Task? _dictationPump;
    private readonly double[] _voiceBands = new double[5];
    private bool _dictationTransition;
    private string? _dictationFinalText;
    private Task? _dictationStopTask;

    private async Task StartNativeDictationAsync()
    {
        if (_dictationTransition || _dictationStopTask is not null || _dictationCapture is not null) return;
        _dictationTransition = true;
        try
        {
            await _microphone.StartAsync(cancellationToken: _lifetime.Token);
            var session = _session.ToString();
            _dictationFinalText = null;
            var turn = Guid.NewGuid().ToString("N");
            var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100) { SingleReader = true, SingleWriter = true });
            _dictationFrames = frames;
            _dictationPump = Task.Run(async () =>
            {
                var index = 0;
                await foreach (var frame in frames.Reader.ReadAllAsync())
                    await _microphone.SubmitChunkAsync(turn, session, index++, Convert.ToBase64String(frame), false);
                if (index == 0) return;
                var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Finished(object? sender, Missum.App.Services.MicrophoneTurnSnapshot result)
                { if (result.TurnId == turn && result.IsFinal) completed.TrySetResult(result.Text); }
                _microphone.TurnChanged += Finished;
                try
                {
                    await _microphone.SubmitChunkAsync(turn, session, index, Convert.ToBase64String(new byte[3200]), true);
                    _dictationFinalText = await completed.Task.WaitAsync(TimeSpan.FromSeconds(45));
                }
                finally { _microphone.TurnChanged -= Finished; }
            });
            var capture = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
            _dictationCapture = capture;
            capture.DataAvailable += OnDictationAudio;
            capture.RecordingStopped += OnDictationStopped;
            capture.StartRecording();
        }
        catch
        {
            await StopNativeDictationAsync();
            throw;
        }
        finally { _dictationTransition = false; }
    }

    private void OnDictationAudio(object? sender, WaveInEventArgs e)
    {
        var frame = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        if (_dictationFrames?.Writer.TryWrite(frame) != true)
        {
            DispatcherQueue.TryEnqueue(async () => { await StopNativeDictationAsync(); ShowError("Die Mikrofonverarbeitung konnte nicht Schritt halten. Bitte erneut starten."); });
            return;
        }
        var bands = Missum.App.Services.MicrophoneSpectrum.Analyze(frame);
        for (var band = 0; band < bands.Length; band++) Volatile.Write(ref _voiceBands[band], bands[band]);
    }

    private void OnDictationStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        DispatcherQueue.TryEnqueue(async () => { await StopNativeDictationAsync(); ShowError(e.Exception.Message); });
    }

    private async Task StopNativeDictationAsync()
    {
        if (_dictationStopTask is { } stopping) { await stopping; return; }
        var task = StopNativeDictationCoreAsync();
        _dictationStopTask = task;
        try { await task; }
        finally { _dictationStopTask = null; }
    }

    private async Task StopNativeDictationCoreAsync()
    {
        var capture = _dictationCapture; _dictationCapture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDictationAudio;
            capture.RecordingStopped -= OnDictationStopped;
            capture.StopRecording(); capture.Dispose();
        }
        var frames = _dictationFrames; _dictationFrames = null;
        var pump = _dictationPump; _dictationPump = null;
        frames?.Writer.TryComplete();
        try
        {
            if (pump is not null) await pump;
            if (!_disposed && _dictationSession == _session && _dictationFinalText is { } text)
                Composer.Text = (_dictationPrefix + " " + text).Trim();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
        finally { await _microphone.StopAsync(); }
    }

    private void UpdateVoiceVisual()
    {
        var active = _dictationCapture is not null;
        if (DictationBars.Children.Count == 0)
            for (var i = 0; i < 5; i++) DictationBars.Children.Add(new Border { Width = 3, Height = 3, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center });
        DictationIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        DictationBars.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        DictationButton.Background = active ? ThemeBrush("MissumAccentSubtleBrush", 50) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ToolTipService.SetToolTip(DictationButton, active ? "Diktieren beenden" : "Diktieren");
        for (var i = 0; i < 5; i++)
        {
            var bar = (Border)DictationBars.Children[i];
            var target = 3 + 19 * Volatile.Read(ref _voiceBands[i]);
            bar.Height += (target - bar.Height) * .45;
            bar.Background = ThemeBrush("MissumAccentBrush", 180);
        }
    }
}
