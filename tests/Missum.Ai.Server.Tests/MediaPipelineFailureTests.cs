using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Runtime;
using Missum.Ai.Server.Core.Storage;
using Missum.Ai.Server.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

/// <summary>Failure receipts from the real executor, worker and model paths, without a live model or GPU.</summary>
public sealed class MediaPipelineFailureTests
{
    private const string ModelId = "coding/DeepSeek-Vision-test~media";
    private static readonly byte[] ImageBytes = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46];
    private static readonly string[] VisionModelTags = ["missum-vision:projector", "missum-reasoning-mode:none", "missum-reasoning-levels:none", "missum-reasoning-default:none"];

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "media.worker_busy", true)]
    [InlineData(HttpStatusCode.BadRequest, "media.invalid_image", false)]
    public async Task WorkerFailureRetainsItsCodeAndProcessingStage(HttpStatusCode status, string code, bool retryable)
    {
        using var fixture = new Fixture(status, code);
        var uploadId = await fixture.UploadAsync();

        var result = await fixture.Executor.ExecuteAsync("media.analyze",
            JsonSerializer.SerializeToElement(new { uploadId, prompt = "Prüfe das aktuelle Bild." }),
            "media-worker-failure", ModelId, "none", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal("media_processing", result.Result.GetProperty("stage").GetString());
        Assert.Equal(ModelId, result.Result.GetProperty("selectedModelId").GetString());
        Assert.Equal(uploadId, result.Result.GetProperty("uploadId").GetString());
        Assert.Equal((int)status, result.Result.GetProperty("httpStatus").GetInt32());
        Assert.Equal(retryable, result.Result.GetProperty("retryable").GetBoolean());
        Assert.Equal(code, result.Result.GetProperty("workerErrorCode").GetString());
        Assert.Contains("Keine erfolgreiche Sichtprüfung", result.Result.GetProperty("cause").GetString());
        Assert.Equal(0, fixture.Handler.VisionRequests);
        var log = Assert.Single(fixture.Runtime.GetLogs(), item => item.EventId == "media.pipeline.failed");
        Assert.Contains("WorkerRequestException", log.Message);
        Assert.Contains("media_processing", log.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Context.Options.LogDirectory, "server-events.jsonl")));
    }

    [Theory]
    [InlineData("generation", "vision_generation")]
    [InlineData("token_counting", "vision_generation")]
    public async Task VisionFailureExposesProviderPhaseAndActualModelInsteadOfGenericUnavailable(string phase, string stage)
    {
        using var fixture = new Fixture(providerFailurePhase: phase);
        var uploadId = await fixture.UploadAsync();

        var result = await fixture.Executor.ExecuteAsync("media.analyze",
            JsonSerializer.SerializeToElement(new { uploadId, prompt = "Prüfe das aktuelle Bild." }),
            "media-provider-failure", ModelId, "none", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("media.provider_unavailable", result.ErrorCode);
        Assert.Equal(stage, result.Result.GetProperty("stage").GetString());
        Assert.Equal(phase, result.Result.GetProperty("providerPhase").GetString());
        Assert.Equal(ModelId, result.Result.GetProperty("modelId").GetString());
        Assert.Equal(500, result.Result.GetProperty("httpStatus").GetInt32());
        Assert.Contains("native engine channel closed", result.Result.GetProperty("cause").GetString());
        Assert.Contains("höchstens einmal", result.Result.GetProperty("recovery").GetString());
        Assert.DoesNotContain("data:image", result.Result.GetRawText());
        var log = Assert.Single(fixture.Runtime.GetLogs(), item => item.EventId == "media.pipeline.failed");
        Assert.Contains("ModelProviderRequestException", log.Message);
        Assert.Contains("HttpRequestException", log.Message);
        Assert.Contains("native engine channel closed", log.Message);
    }

    [Theory]
    [InlineData("text/plain", "not JSON")]
    [InlineData("application/json", "{\"detail\":{\"errorCode\":\"media.bad_code with spaces\"}}")]
    public async Task MalformedWorkerBodyStillRetainsHttpStatus(string mediaType, string body)
    {
        using var fixture = new Fixture(HttpStatusCode.ServiceUnavailable, workerBody: body, workerContentType: mediaType);
        var exception = await Assert.ThrowsAsync<WorkerRequestException>(() =>
            fixture.WorkerClient.InspectMediaAsync(new("upload-" + new string('a', 32), "image/png")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
        Assert.Null(exception.Detail);
    }

    [Fact]
    public async Task OversizedWorkerBodyIsNotRetained()
    {
        using var fixture = new Fixture(HttpStatusCode.BadRequest,
            workerBody: JsonSerializer.Serialize(new { detail = new { errorCode = "media.invalid_image", message = new string('x', 5000) } }));
        var exception = await Assert.ThrowsAsync<WorkerRequestException>(() =>
            fixture.WorkerClient.InspectMediaAsync(new("upload-" + new string('a', 32), "image/png")));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
        Assert.Null(exception.Detail);
    }

    [Fact]
    public void DiagnosticTextRedactsImagePayloadAndBoundsProviderText()
    {
        var text = AgentToolExecutor.SanitizeMediaFailureDetail("native error: data:image/jpeg;base64,"
            + new string('A', 2000) + "\nplain " + new string('B', 2000) + " " + new string('z', 10000));

        Assert.Contains("native error:", text);
        Assert.Contains("[Medieninhalt]", text);
        Assert.DoesNotContain("data:image", text);
        Assert.DoesNotContain(new string('A', 256), text);
        Assert.DoesNotContain(new string('B', 256), text);
        Assert.DoesNotContain('\n', text);
        Assert.True(text.Length <= 1200);
    }

    private sealed class Fixture : IDisposable
    {
        public TestServerContext Context { get; } = new();
        public Transport Handler { get; }
        public UploadService Uploads { get; }
        public AgentToolExecutor Executor { get; }
        public ServerRuntimeState Runtime { get; }
        public WorkerApiClient WorkerClient { get; }
        private readonly HttpClient _http;
        private readonly ModelRuntimeClient _model;
        private readonly GpuLeaseScheduler _scheduler;
        private readonly WorkerOrchestrator _workers;

        public Fixture(HttpStatusCode? workerStatus = null, string? workerCode = null, string? providerFailurePhase = null,
            string? workerBody = null, string workerContentType = "application/json")
        {
            Context.Options.GeneralContextLength = 8192;
            Context.Options.VisionContextLength = 8192;
            var relative = "artifacts/worker/media-test/vision-input.jpg";
            var artifactPath = Path.Combine(Context.Options.ResolvedWorkerDataDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllBytes(artifactPath, ImageBytes);
            Handler = new Transport(relative, workerStatus, workerCode, providerFailurePhase, workerBody, workerContentType);
            _http = new HttpClient(Handler);
            _model = new ModelRuntimeClient(_http, Context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            Runtime = new ServerRuntimeState(Context.WrappedOptions);
            _scheduler = new GpuLeaseScheduler(Context.Database, Runtime);
            var artifacts = new ArtifactService(Context.Database, Context.WrappedOptions);
            WorkerClient = new WorkerApiClient(_http, Context.WrappedOptions);
            _workers = new WorkerOrchestrator(WorkerClient, _model, _scheduler, artifacts, Context.WrappedOptions, Runtime);
            Uploads = new UploadService(Context.Database, Context.WrappedOptions);
            Executor = new AgentToolExecutor(null!, _workers, Uploads, artifacts, _model,
                _scheduler, new ServiceActivityTracker(), Context.WrappedOptions, Runtime);
        }

        public async Task<string> UploadAsync()
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(ImageBytes));
            var upload = await Uploads.CreateAsync(new("capture.jpg", "image/jpeg", ImageBytes.Length, hash));
            using var stream = new MemoryStream(ImageBytes, writable: false);
            await Uploads.PutChunkAsync(upload.UploadId, 0, stream, hash);
            await Uploads.CompleteAsync(upload.UploadId);
            return upload.UploadId;
        }

        public void Dispose()
        {
            _workers.Dispose();
            _model.Dispose();
            _scheduler.Dispose();
            _http.Dispose();
            Context.Dispose();
        }
    }

    private sealed class Transport(string relativeArtifact, HttpStatusCode? workerStatus, string? workerCode,
        string? providerFailurePhase, string? workerBody, string workerContentType) : HttpMessageHandler
    {
        public int VisionRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/inspect")
            {
                if (workerStatus is { } failure)
                    return Task.FromResult(new HttpResponseMessage(failure)
                    {
                        Content = new StringContent(workerBody ?? JsonSerializer.Serialize(new
                        { detail = new { errorCode = workerCode, message = "Keine erfolgreiche Sichtprüfung in diesem Schritt." } }),
                            Encoding.UTF8, workerContentType),
                    });
                return Json(new { kind = "image", metadata = new { width = 16, height = 16 },
                    artifacts = new[] { new { relativePath = relativeArtifact, fileName = "vision-input.jpg", mediaType = "image/jpeg", role = "vision_input" } },
                    frames = Array.Empty<object>() });
            }
            if (path == "/release") return Json(new { released = true });
            if (path == "/v1/models") return Json(new { data = new[] { new
            {
                id = ModelId, status = new { value = "loaded" },
                tags = VisionModelTags,
            } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 8192 } });
            if (path is "/sessions/prepare" or "/sessions/save") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path == "/v1/chat/completions/input_tokens" && providerFailurePhase != "token_counting") return Json(new { input_tokens = 100 });
            if (path is "/v1/chat/completions" or "/v1/chat/completions/input_tokens")
            {
                VisionRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("native engine channel closed", Encoding.UTF8, "text/plain") });
            }
            throw new InvalidOperationException("Unexpected isolated media request: " + path);
        }

        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }
}
