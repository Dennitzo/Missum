using Missum.Ai.Contracts;
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

/// <summary>Actual executor/orchestrator/runtime paths with an isolated HTTP transport and SQLite store.</summary>
public sealed class AgentToolModelIdentityTests
{
    private const string EmbeddingId = "embedding/bge-m3-f16~identity";
    private const string DefaultGeneralId = "coding/gpt-oss-120b-MXFP4~identity";
    private const string SelectedGeneralId = "coding/Qwen3.8-test~identity";
    private const string SelectedVisionId = "vision/Qwen3-VL-test~identity";
    private static readonly string[] InstalledIds = [EmbeddingId, DefaultGeneralId, SelectedGeneralId, SelectedVisionId];
    private static readonly string[] ReasoningTags = ["missum-reasoning-mode:none", "missum-reasoning-levels:none", "missum-reasoning-default:none"];
    private static readonly string[] EmbeddingInputs = ["target", "other"];
    private static readonly double[] RelevantVector = [1d, 0d];
    private static readonly double[] OtherVector = [0d, 1d];

    [Theory]
    [InlineData("batch")]
    [InlineData("context.embed")]
    [InlineData("context.retrieve")]
    public async Task EmbeddingResponsesAndInferenceUseThePreparedCanonicalModel(string operation)
    {
        using var fixture = new ExecutorFixture();
        Assert.Equal("text-embedding-bge-m3", fixture.Context.Options.EmbeddingModelId);
        string? responseModel;
        if (operation == "batch")
        {
            var result = await fixture.Executor.CreateEmbeddingBatchAsync(new(
                [new("first", "target"), new("second", "other")]), "identity-test");
            responseModel = result.ModelId;
            Assert.Equal(2, result.Dimensions);
            Assert.Equal(2, result.Vectors.Count);
            Assert.Equal(RelevantVector, result.Vectors[0].Values);
            Assert.Equal(OtherVector, result.Vectors[1].Values);
        }
        else
        {
            var arguments = operation == "context.embed"
                ? JsonSerializer.SerializeToElement(new { inputs = EmbeddingInputs })
                : JsonSerializer.SerializeToElement(new { query = "target", documents = EmbeddingInputs, topK = 2 });
            var result = await fixture.Executor.ExecuteAsync(operation, arguments, "identity-test");
            Assert.True(result.Succeeded, result.ErrorMessage);
            responseModel = result.Result.GetProperty("model").GetString();
            if (operation == "context.embed")
            {
                Assert.Equal(2, result.Result.GetProperty("dimensions").GetInt32());
                Assert.Equal(RelevantVector, result.Result.GetProperty("vectors")[0].EnumerateArray().Select(item => item.GetDouble()));
            }
            else
            {
                var matches = result.Result.GetProperty("matches");
                Assert.Equal(2, matches.GetArrayLength());
                Assert.Equal("target", matches[0].GetProperty("document").GetString());
                Assert.Equal(1d, matches[0].GetProperty("score").GetDouble());
                Assert.Equal(0d, matches[1].GetProperty("score").GetDouble());
            }
        }
        Assert.Equal(EmbeddingId, responseModel);
        Assert.Equal(EmbeddingId, Assert.Single(fixture.Handler.LoadedModels));
        Assert.Equal(EmbeddingId, Assert.Single(fixture.Handler.EmbeddingModels));
        Assert.Empty(fixture.Handler.ChatModels);
    }

    [Theory]
    [InlineData(null, DefaultGeneralId)]
    [InlineData(SelectedGeneralId, SelectedGeneralId)]
    [InlineData("qwen3.8-test", SelectedGeneralId)]
    [InlineData(SelectedVisionId, SelectedVisionId)]
    public async Task AudioTranscriptAnalysisHonorsTheSelectedModelAndReportsItsCanonicalIdentity(string? requestedModel, string expectedModel)
    {
        using var fixture = new ExecutorFixture();
        var bytes = Encoding.UTF8.GetBytes("isolated worker transport audio fixture");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var upload = await fixture.Uploads.CreateAsync(new("audio.wav", "audio/wav", bytes.Length, hash));
        using (var stream = new MemoryStream(bytes, writable: false))
            await fixture.Uploads.PutChunkAsync(upload.UploadId, 0, stream, hash);
        await fixture.Uploads.CompleteAsync(upload.UploadId);

        var result = await fixture.Executor.ExecuteAsync("media.analyze",
            JsonSerializer.SerializeToElement(new { uploadId = upload.UploadId, prompt = "Beschreibe den gesprochenen Inhalt." }),
            "audio-model-identity", requestedModel, "none", CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("Der rote Apfel liegt auf dem Tisch.", result.Result.GetProperty("analysis").GetString());
        Assert.Equal(expectedModel, result.ModelId);
        Assert.Equal(expectedModel, result.Result.GetProperty("modelId").GetString());
        Assert.Equal(expectedModel, Assert.Single(fixture.Handler.ChatModels));
        Assert.Equal(expectedModel, Assert.Single(fixture.Handler.LoadedModels));
        Assert.Equal(1, fixture.Handler.TranscriptionRequests);
        Assert.Empty(fixture.Handler.EmbeddingModels);
        if (requestedModel is not null) Assert.DoesNotContain(DefaultGeneralId, fixture.Handler.LoadedModels);
    }

    private sealed class ExecutorFixture : IDisposable
    {
        public TestServerContext Context { get; } = new();
        public RuntimeHandler Handler { get; } = new();
        public UploadService Uploads { get; }
        public AgentToolExecutor Executor { get; }
        private readonly HttpClient _http;
        private readonly ModelRuntimeClient _model;
        private readonly GpuLeaseScheduler _scheduler;
        private readonly WorkerOrchestrator _workers;

        public ExecutorFixture()
        {
            Context.Options.GeneralContextLength = 32_768;
            _http = new HttpClient(Handler);
            _model = new ModelRuntimeClient(_http, Context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var runtime = new ServerRuntimeState();
            _scheduler = new GpuLeaseScheduler(Context.Database, runtime);
            var artifacts = new ArtifactService(Context.Database, Context.WrappedOptions);
            _workers = new WorkerOrchestrator(new WorkerApiClient(_http, Context.WrappedOptions), _model,
                _scheduler, artifacts, Context.WrappedOptions, runtime);
            Uploads = new UploadService(Context.Database, Context.WrappedOptions);
            Executor = new AgentToolExecutor(null!, _workers, Uploads, artifacts, _model,
                _scheduler, new ServiceActivityTracker(), Context.WrappedOptions);
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

    private sealed class RuntimeHandler : HttpMessageHandler
    {
        private string? _loadedModel;
        public List<string> LoadedModels { get; } = [];
        public List<string> EmbeddingModels { get; } = [];
        public List<string> ChatModels { get; } = [];
        public int TranscriptionRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/release") return Json(new { released = true });
            if (path == "/v1/models") return Json(new { data = InstalledIds.Select(id => new
            {
                id, status = new { value = id == _loadedModel ? "loaded" : "unloaded" }, tags = ReasoningTags,
            }) });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = _loadedModel == EmbeddingId ? 8192 : 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            if (path == "/transcriptions")
            {
                TranscriptionRequests++;
                return Json(new { text = "Der rote Apfel liegt auf dem Tisch.", language = "de", languageProbability = 0.99,
                    segments = Array.Empty<TranscriptionSegment>(), provider = "isolated-whisper-transport" });
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var model = body.RootElement.GetProperty("model").GetString()!;
            if (path == "/models/load")
            {
                _loadedModel = model;
                LoadedModels.Add(model);
                return Json(new { success = true });
            }
            if (path == "/models/unload")
            {
                Assert.Equal(_loadedModel, model);
                _loadedModel = null;
                return Json(new { success = true });
            }
            Assert.Equal(_loadedModel, model);
            if (path == "/v1/embeddings")
            {
                EmbeddingModels.Add(model);
                return Json(new { data = body.RootElement.GetProperty("input").EnumerateArray().Select((input, index) => new
                {
                    index, embedding = input.GetString() == "target" ? RelevantVector : OtherVector,
                }).ToArray() });
            }
            Assert.Equal("/v1/chat/completions", path);
            ChatModels.Add(model);
            return Json(new { choices = new[] { new { message = new { role = "assistant", content = "Der rote Apfel liegt auf dem Tisch." }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 100, completion_tokens = 10 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}
