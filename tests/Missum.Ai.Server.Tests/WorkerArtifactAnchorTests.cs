using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Storage;
using Missum.Ai.Server.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Ai.Server.Tests;

public sealed class WorkerArtifactAnchorTests
{
    private const string ModelId = "coding/Media-WorkerFixture~anchor";
    private static readonly byte[] OriginalBytes = "The untouched full-resolution original PNG"u8.ToArray();
    private static readonly byte[] VisionBytes = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46];
    private static readonly byte[] ThumbnailBytes = "Small thumbnail bytes"u8.ToArray();
    private static readonly string[] ModelTags = ["missum-vision:projector", "missum-reasoning-mode:none",
        "missum-reasoning-levels:none", "missum-reasoning-default:none"];

    [Theory]
    [InlineData(RunWorkloadKind.ImageGeneration, "image.generate")]
    [InlineData(RunWorkloadKind.MediaAnalysis, "media.analyze")]
    public async Task WorkerImageEventsRetainTheirToolAnchorWhenReplayedAfterTheStart(RunWorkloadKind kind, string tool)
    {
        using var fixture = new Fixture();
        var uploadId = await fixture.UploadAsync();
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Erstelle oder prüfe das Originalbild.")])],
            PreferredGeneralModelId: ModelId, ReasoningEffort: "none",
            Workload: new(kind, UploadId: kind == RunWorkloadKind.MediaAnalysis ? uploadId : null,
                Prompt: "Prüfe oder erzeuge das Bild.",
                Options: new Dictionary<string, string> { ["mediaType"] = "image/png" }));
        var created = await fixture.Repository.CreateAsync(request, null);

        await fixture.Processor.ProcessAsync(created.Snapshot.RunId, CancellationToken.None);

        Assert.Equal(RunState.Completed, (await fixture.Repository.GetAsync(created.Snapshot.RunId))!.State);
        var events = await fixture.Repository.GetEventsAfterAsync(created.Snapshot.RunId, 0);
        var start = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolStarted);
        var stepId = start.Data.GetProperty("callId").GetString();
        Assert.Equal(RunProcessor.WorkerToolStepId(created.Snapshot.RunId, tool), stepId);
        Assert.Equal(stepId, start.Data.GetProperty("toolCallId").GetString());
        Assert.Equal(tool, start.Data.GetProperty("tool").GetString());
        Assert.Equal("Prüfe oder erzeuge das Bild.", start.Data.GetProperty("arguments").GetProperty("prompt").GetString());
        if (kind == RunWorkloadKind.MediaAnalysis)
            Assert.Equal(uploadId, start.Data.GetProperty("arguments").GetProperty("uploadId").GetString());
        Assert.All(events.Where(item => item.Type == RunEventTypes.ServerToolCompleted), completed =>
        {
            Assert.Equal(stepId, completed.Data.GetProperty("callId").GetString());
            Assert.Equal(stepId, completed.Data.GetProperty("toolCallId").GetString());
            Assert.Equal(tool, completed.Data.GetProperty("tool").GetString());
        });
        var descriptor = Assert.Single(events, item => item.Type == RunEventTypes.ArtifactCreated)
            .Data.Deserialize<ArtifactDescriptor>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal(stepId, descriptor.StepId);
        Assert.Equal("image/png", descriptor.MediaType);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(OriginalBytes)), descriptor.Sha256);
        if (kind == RunWorkloadKind.MediaAnalysis)
        {
            Assert.Equal("original", descriptor.Metadata!["role"]);
            Assert.Equal("internal", descriptor.Metadata["visibility"]);
            Assert.Equal(uploadId, descriptor.Metadata["sourceUploadId"]);
            Assert.Equal(1, fixture.Handler.VisionRequests);
        }
        else Assert.Equal(0, fixture.Handler.VisionRequests);
        var replayed = await new RunRepository(fixture.Context.Database, new RunEventNotifier())
            .GetEventsAfterAsync(created.Snapshot.RunId, start.Id);
        Assert.DoesNotContain(replayed, item => item.Type == RunEventTypes.ServerToolStarted);
        Assert.Equal(stepId, Assert.Single(replayed, item => item.Type == RunEventTypes.ArtifactCreated)
            .Data.Deserialize<ArtifactDescriptor>(MissumAiProtocol.CreateJsonOptions())!.StepId);
    }

    private sealed class Fixture : IDisposable
    {
        public TestServerContext Context { get; } = new();
        public WorkerTransport Handler { get; } = new();
        public RunRepository Repository { get; }
        public RunProcessor Processor { get; }
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;

        public Fixture()
        {
            Context.Options.ModelRuntimeUri = new("http://native.test");
            Context.Options.GeneralContextLength = 8192;
            Context.Options.VisionContextLength = 8192;
            var directory = Path.Combine(Context.Options.WorkerArtifactDirectory, "image-anchor-test");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "original.png"), OriginalBytes);
            File.WriteAllBytes(Path.Combine(directory, "vision-input.jpg"), VisionBytes);
            File.WriteAllBytes(Path.Combine(directory, "thumbnail.jpg"), ThumbnailBytes);
            _http = new HttpClient(Handler);
            var services = new ServiceCollection().AddLogging();
            services.AddMissumAiServerServices(Context.Options, includeHostedServices: false);
            services.AddSingleton(Context.Database);
            services.AddSingleton(new ModelRuntimeClient(_http, Context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
            services.AddSingleton(new WorkerApiClient(_http, Context.WrappedOptions));
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
        }

        public async Task<string> UploadAsync()
        {
            var uploads = _services.GetRequiredService<UploadService>();
            var sha = Convert.ToHexStringLower(SHA256.HashData(OriginalBytes));
            var upload = await uploads.CreateAsync(new("original.png", "image/png", OriginalBytes.Length, sha));
            using var content = new MemoryStream(OriginalBytes, writable: false);
            await uploads.PutChunkAsync(upload.UploadId, 0, content, sha);
            await uploads.CompleteAsync(upload.UploadId);
            return upload.UploadId;
        }

        public void Dispose()
        {
            _services.Dispose();
            _http.Dispose();
            Context.Dispose();
        }
    }

    private sealed class WorkerTransport : HttpMessageHandler
    {
        public int VisionRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/release") return Json(new { released = true });
            if (path == "/generate") return Json(new { provider = "isolated-worker", model = "fixture", durationMilliseconds = 1,
                artifacts = new[] { new { relativePath = "artifacts/worker/image-anchor-test/original.png", fileName = "original.png", mediaType = "image/png" } } });
            if (path == "/inspect") return Json(new { kind = "image", metadata = new { width = 4800, height = 2400 }, artifacts = new[]
            {
                new { relativePath = "artifacts/worker/image-anchor-test/original.png", fileName = "original.png", mediaType = "image/png", role = "original" },
                new { relativePath = "artifacts/worker/image-anchor-test/vision-input.jpg", fileName = "vision-input.jpg", mediaType = "image/jpeg", role = "vision_input" },
                new { relativePath = "artifacts/worker/image-anchor-test/thumbnail.jpg", fileName = "thumbnail.jpg", mediaType = "image/jpeg", role = "thumbnail" },
            }, frames = Array.Empty<object>() });
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, status = new { value = "loaded" }, tags = ModelTags } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 8192 } });
            if (path is "/sessions/prepare" or "/sessions/save") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            if (path == "/v1/chat/completions")
            {
                VisionRequests++;
                return Json(new { choices = new[] { new { message = new { role = "assistant", content = "Das Originalbild ist geprüft." }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 100, completion_tokens = 10 } });
            }
            throw new InvalidOperationException("Unexpected isolated worker request: " + path);
        }

        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }
}
