using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Audio;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runtime;
using Missum.Ai.Server.Core.Storage;
using Missum.Ai.Server.Core.Workers;

namespace Missum.Ai.Server.Tests;

public sealed class RecognitionLifecycleTests
{
    [Fact]
    public async Task GatewayStartupWarmsOnlyTtsAndClearsOrphanedRecognition()
    {
        using var fixture = new Fixture();
        await fixture.Workers.WarmAllStartupResourcesAsync();
        Assert.Equal(["/recognition/release", "/load:tts"], fixture.Handler.Requests.ToArray());
    }

    [Fact]
    public async Task LastExplicitRecognitionOwnerReleasesModelsWithoutReleasingTts()
    {
        using var fixture = new Fixture();
        await fixture.Workers.PrepareLiveCaptionResourcesAsync("microphone", LiveCaptionProfile.Dictation);
        await fixture.Workers.PrepareLiveCaptionResourcesAsync("captions", LiveCaptionProfile.Captions);
        Assert.Equal(["/load:stt", "/load:speaker"], fixture.Handler.Requests.ToArray());
        await fixture.Workers.ReleaseLiveCaptionResourcesAsync("microphone");
        Assert.DoesNotContain("/recognition/release", fixture.Handler.Requests);
        await fixture.Workers.WarmSpeechResourcesAsync();
        Assert.DoesNotContain("/recognition/release", fixture.Handler.Requests);
        await fixture.Workers.ReleaseLiveCaptionResourcesAsync("captions");
        await fixture.Workers.ReleaseLiveCaptionResourcesAsync("captions");
        Assert.Single(fixture.Handler.Requests, request => request == "/recognition/release");
        Assert.DoesNotContain("/release", fixture.Handler.Requests);
        await fixture.Workers.PrepareLiveCaptionResourcesAsync("again", LiveCaptionProfile.Dictation);
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request == "/load:stt"));
        await fixture.Workers.ReleaseLiveCaptionResourcesAsync("again");
    }

    [Fact]
    public async Task DisablingMicrophoneDoesNotUnloadAnotherInflightTranscription()
    {
        using var fixture = new Fixture();
        await fixture.Workers.PrepareLiveCaptionResourcesAsync("microphone", LiveCaptionProfile.Dictation);
        fixture.Handler.HoldTranscription = true;
        var transcription = fixture.Workers.TranscribeAsync(new("upload-" + new string('0', 32), "de"));
        await fixture.Handler.TranscriptionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Workers.ReleaseLiveCaptionResourcesAsync("microphone");
        Assert.DoesNotContain("/recognition/release", fixture.Handler.Requests);
        fixture.Handler.FinishTranscription.TrySetResult();
        Assert.Equal("Guten Tag.", (await transcription).Text);
        Assert.Single(fixture.Handler.Requests, request => request == "/recognition/release");
    }

    [Fact]
    public async Task ExplicitSessionStopAndGatewayShutdownEjectRecognition()
    {
        using var fixture = new Fixture();
        using var captions = new LiveCaptionService(fixture.Workers, fixture.Runtime);
        var first = await captions.CreateAsync(new(WindowMilliseconds: 6000, OverlapMilliseconds: 0, Profile: LiveCaptionProfile.Dictation));
        Assert.Equal(["/load:stt"], fixture.Handler.Requests.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => captions.CreateAsync(new()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => captions.StopSessionAsync("foreign-session"));
        Assert.DoesNotContain("/recognition/release", fixture.Handler.Requests);
        await captions.StopSessionAsync(first.SessionId);
        Assert.Single(fixture.Handler.Requests, request => request == "/recognition/release");
        _ = await captions.CreateAsync(new(Profile: LiveCaptionProfile.Captions));
        await captions.StopAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request == "/recognition/release"));
    }

    [Fact]
    public async Task FailedRecognitionWarmupReleasesPartialAllocationAndCanRetry()
    {
        using var fixture = new Fixture();
        fixture.Handler.FailSpeakerLoad = true;
        await Assert.ThrowsAsync<WorkerRequestException>(() => fixture.Workers.PrepareLiveCaptionResourcesAsync("failed", LiveCaptionProfile.Captions));
        Assert.Single(fixture.Handler.Requests, request => request == "/recognition/release");
        fixture.Handler.FailSpeakerLoad = false;
        await fixture.Workers.PrepareLiveCaptionResourcesAsync("recovered", LiveCaptionProfile.Dictation);
        await fixture.Workers.ReleaseLiveCaptionResourcesAsync("recovered");
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request == "/load:stt"));
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request == "/recognition/release"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly HttpClient _http;
        private readonly ModelRuntimeClient _model;
        private readonly GpuLeaseScheduler _scheduler;
        internal Transport Handler { get; } = new();
        internal ServerRuntimeState Runtime { get; } = new();
        internal WorkerOrchestrator Workers { get; }

        internal Fixture()
        {
            _http = new(Handler);
            _model = new(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            _scheduler = new(_context.Database, Runtime);
            Workers = new(new WorkerApiClient(_http, _context.WrappedOptions), _model, _scheduler,
                new ArtifactService(_context.Database, _context.WrappedOptions), _context.WrappedOptions, Runtime);
        }

        public void Dispose()
        {
            Workers.Dispose();
            _scheduler.Dispose();
            _model.Dispose();
            _http.Dispose();
            _context.Dispose();
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal bool HoldTranscription { get; set; }
        internal bool FailSpeakerLoad { get; set; }
        internal TaskCompletionSource TranscriptionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FinishTranscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/load")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var component = body.RootElement.GetProperty("component").GetString();
                Requests.Enqueue(path + ":" + component);
                if (component == "speaker" && FailSpeakerLoad) return new(HttpStatusCode.ServiceUnavailable);
            }
            else Requests.Enqueue(path);
            if (path == "/transcriptions")
            {
                TranscriptionEntered.TrySetResult();
                if (HoldTranscription) await FinishTranscription.Task.WaitAsync(cancellationToken);
                return Json(new { text = "Guten Tag.", language = "de", languageProbability = .99, segments = Array.Empty<object>(), provider = "isolated-whisper" });
            }
            if (path is "/load" or "/recognition/release") return Json(new { success = true });
            throw new InvalidOperationException("Unerwarteter Modell-/Workerzugriff: " + path);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
