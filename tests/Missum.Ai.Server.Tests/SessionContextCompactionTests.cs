using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class SessionContextCompactionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitSummaryBelowThresholdStoresOnlyInferenceContextAndDoesNotContinueTheTask(bool coding, bool science)
    {
        using var fixture = new Fixture();
        var request = Request(coding, science);
        var runId = await fixture.SeedAsync(request);
        var source = (await fixture.Repository.GetIdleSessionCompactionSourceAsync(request.SessionId!))!;
        var originalRequest = JsonSerializer.Serialize(await fixture.Repository.GetRequestAsync(runId));
        var originalEvents = JsonSerializer.Serialize(await fixture.Repository.GetEventsAfterAsync(runId, 0));
        Assert.Null(CodingContextCompactor.Plan(source.Checkpoint.Messages, 32768));

        var result = await fixture.Processor.CompactSessionContextAsync(new(request.SessionId!));

        Assert.True(result.Changed);
        Assert.Equal(runId, result.SourceRunId);
        Assert.True(result.AfterTokenEstimate < result.BeforeTokenEstimate);
        Assert.Equal(1, result.CompactionCount);
        Assert.Equal(1, fixture.Native.InferenceCount);
        Assert.Equal(RunState.Completed, (await fixture.Repository.GetAsync(runId))!.State);
        Assert.Equal(originalRequest, JsonSerializer.Serialize(await fixture.Repository.GetRequestAsync(runId)));
        Assert.Equal(originalEvents, JsonSerializer.Serialize(await fixture.Repository.GetEventsAfterAsync(runId, 0)));
        var compacted = (await fixture.Repository.GetIdleSessionCompactionSourceAsync(request.SessionId!))!.Checkpoint;
        Assert.Equal(source.Checkpoint.Messages[0], compacted.Messages[0]);
        Assert.Equal(source.Checkpoint.Messages[^2], compacted.Messages[^2]);
        Assert.Equal(source.Checkpoint.Messages[^1], compacted.Messages[^1]);
        Assert.Equal(source.Checkpoint.WorkingState, compacted.WorkingState);
        Assert.Equal(source.Checkpoint.ContextTools, compacted.ContextTools);
        Assert.Contains(compacted.Messages, message => message.Content?.StartsWith(CodingContextCompactor.MemoryMarker, StringComparison.Ordinal) == true);
        Assert.False(compacted.WorkingStatePromptIncluded);
        if (!coding)
        {
            var followUp = request with { Messages = [.. request.Messages, Message("assistant", "Aktuelle Antwort."), Message("user", "Nächste Frage.")] };
            var initial = RunProcessor.CreateInitialMessages(followUp, "general", []);
            Assert.True(GeneralSessionContext.TryContinue(new(compacted, request, "Aktuelle Antwort."), followUp, initial, out var continued));
            Assert.Contains(continued, message => message.Content?.StartsWith(CodingContextCompactor.MemoryMarker, StringComparison.Ordinal) == true);
            Assert.Equal("Nächste Frage.", continued[^1].Content);
        }
    }

    [Fact]
    public async Task ActiveSessionIsRejectedBeforeModelInferenceAndOtherSessionsAreIsolated()
    {
        using var fixture = new Fixture();
        var request = Request(false);
        await fixture.SeedAsync(request);
        var active = (await fixture.Repository.CreateAsync(request with { SessionId = "other-session" }, null)).Snapshot.RunId;
        Assert.True((await fixture.Processor.CompactSessionContextAsync(new(request.SessionId!))).Changed);
        Assert.Equal(RunState.Queued, (await fixture.Repository.GetAsync(active))!.State);
        _ = await fixture.Repository.CreateAsync(request, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Processor.CompactSessionContextAsync(new(request.SessionId!)));
        Assert.Equal(1, fixture.Native.InferenceCount);
    }

    [Fact]
    public async Task NewPromptDuringSummaryCannotBeOverwrittenAndTheSessionLockIsReleased()
    {
        using var fixture = new Fixture();
        var request = Request(true);
        await fixture.SeedAsync(request);
        var before = (await fixture.Repository.GetIdleSessionCompactionSourceAsync(request.SessionId!))!.StoredCheckpoint;
        string? newRunId = null;
        fixture.Native.OnInference = async token =>
        {
            newRunId = (await fixture.Repository.CreateAsync(request, null, token)).Snapshot.RunId;
            return "Ältere Ergebnisse zusammengefasst.";
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Processor.CompactSessionContextAsync(new(request.SessionId!)));

        Assert.NotNull(newRunId);
        await fixture.Repository.UpdateStateAsync(newRunId, RunState.Cancelled);
        Assert.Equal(before, (await fixture.Repository.GetIdleSessionCompactionSourceAsync(request.SessionId!))!.StoredCheckpoint);
        fixture.Native.OnInference = null;
        Assert.True((await fixture.Processor.CompactSessionContextAsync(new(request.SessionId!))).Changed);
    }

    [Fact]
    public async Task StopDuringSummaryPreservesCheckpointAndAllowsAnotherExplicitSummary()
    {
        using var fixture = new Fixture();
        var request = Request(true);
        await fixture.SeedAsync(request);
        var before = (await fixture.Repository.GetIdleSessionCompactionSourceAsync(request.SessionId!))!.StoredCheckpoint;
        using var stop = new CancellationTokenSource();
        fixture.Native.OnInference = token =>
        {
            stop.Cancel();
            return Task.FromCanceled<string>(token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.CompactSessionContextAsync(new(request.SessionId!), stop.Token));
        Assert.Equal(before, (await fixture.Repository.GetIdleSessionCompactionSourceAsync(request.SessionId!))!.StoredCheckpoint);
        fixture.Native.OnInference = null;
        Assert.True((await fixture.Processor.CompactSessionContextAsync(new(request.SessionId!))).Changed);
    }

    [Fact]
    public async Task EmptySessionDoesNotLoadOrRunAModel()
    {
        using var fixture = new Fixture();
        var result = await fixture.Processor.CompactSessionContextAsync(new("empty-session"));
        Assert.False(result.Changed);
        Assert.Null(result.SourceRunId);
        Assert.Equal(0, fixture.Native.InferenceCount);
        Assert.Contains("kein gespeicherter", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualSummaryUsesActualFittedWindowAndRecoversFromDenseNativeTokenization()
    {
        using var fixture = new Fixture();
        fixture.Native.FittedContext = 8192;
        fixture.Native.CountActualCharacters = true;
        var request = Request(true, science: true) with
        {
            Messages = [Message("user", "Earlier scientific task"), Message("assistant", new string('x', 200_000)),
                Message("user", "Verify the current goal without losing its assumptions.")],
        };
        var run = await fixture.SeedAsync(request);

        var result = await fixture.Processor.CompactSessionContextAsync(new(request.SessionId!, MaximumContextTokens: 32768));

        Assert.True(result.Changed);
        Assert.Equal(1, fixture.Native.InferenceCount);
        Assert.True(fixture.Native.Counts.Count >= 2);
        Assert.True(fixture.Native.Counts[^1] + fixture.Native.LastOutputLimit + 1 <= fixture.Native.FittedContext);
        var saved = (await fixture.Repository.GetIdleSessionCompactionSourceAsync(request.SessionId!))!;
        Assert.Equal(run, saved.RunId);
        Assert.Contains(saved.Checkpoint.Messages, message => message.Content == "Verify the current goal without losing its assumptions.");
        Assert.Equal(RunState.Completed, (await fixture.Repository.GetAsync(run))!.State);
    }

    [Fact]
    public void ExplicitCompactionPreservesOpenToolExchangesAndWorkingState()
    {
        var pending = new LmToolCall("pending", "coding.command", JsonSerializer.SerializeToElement(new { command = "python verify.py" }));
        LmChatMessage[] source = [new("system", "Policy"), new("user", "Historischer Auftrag"),
            new("assistant", new string('x', 5000)), new("user", "Aktueller Auftrag"),
            new("assistant", "Prozess läuft", ToolCalls: [pending])];
        var plan = CodingContextCompactor.Plan(source, 32768, force: true)!;
        var result = CodingContextCompactor.Complete(plan, "Bisherige Ergebnisse.");
        Assert.Contains(source[^1], result);
        Assert.Contains(source[^2], result);
        Assert.DoesNotContain(result, message => message.Role == "tool");
        Assert.Equal(source[^1], result[^1]);
    }

    private static RunMessage Message(string role, string text) => new(role, [new("text", Text: text)]);

    private static RunRequest Request(bool coding, bool science = false) => new(MissumAiProtocol.Version,
        coding ? RunMode.Coding : RunMode.General,
        [Message("user", "Früherer Auftrag"), Message("assistant", new string('x', 8000)), Message("user", "Aktueller Auftrag")],
        SessionId: "compact-session", PreferredCodingModelId: NativeHandler.ModelId, PreferredGeneralModelId: NativeHandler.ModelId,
        CodingOptions: coding ? new(WorkspacePath: "C:/fixture/project", ContinueSessionContext: true) : null,
        WorkflowProfile: science ? ScienceSkill.Profile : null);

    private sealed class Fixture : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly ModelRuntimeClient _runtime;
        internal NativeHandler Native { get; } = new();
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }

        internal Fixture()
        {
            _context.Options.ModelRuntimeUri = new("http://native.test:19090");
            _context.Options.GeneralModelId = NativeHandler.ModelId;
            _context.Options.CodingModelId = NativeHandler.ModelId;
            _http = new(Native, disposeHandler: false);
            _runtime = new ModelRuntimeClient(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var services = new ServiceCollection().AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database).AddSingleton(_runtime);
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
        }

        internal async Task<string> SeedAsync(RunRequest request)
        {
            var id = (await Repository.CreateAsync(request, null)).Snapshot.RunId;
            var messages = RunProcessor.CreateInitialMessages(request, request.Mode == RunMode.Coding ? "coding" : "general", []);
            await Repository.SaveCheckpointAsync(id, new([.. messages, new("assistant", "Aktuelle Antwort.")], 4, 1, 1000, 200,
                PreserveSessionPromptPrefix: true, WorkingStatePromptIncluded: true, ContextTools: [], SelectedModelId: NativeHandler.ModelId));
            await Repository.AppendEventAsync(id, RunEventTypes.TextDelta, new { delta = "Aktuelle Antwort." });
            Assert.True(await Repository.FinalizeConversationAsync(id, new(null, NativeHandler.ModelId, 1000, 200)));
            return id;
        }

        public void Dispose()
        {
            _services.Dispose();
            _runtime.Dispose();
            _http.Dispose();
            Native.Dispose();
            _context.Dispose();
        }
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        internal const string ModelId = "coding/Qwen3.8-27B~compaction";
        private static readonly string[] ModelTags = ["missum-context-train:32768"];
        internal int InferenceCount { get; private set; }
        internal int FittedContext { get; set; } = 32768;
        internal bool CountActualCharacters { get; set; }
        internal List<int> Counts { get; } = [];
        internal int LastOutputLimit { get; private set; }
        internal Func<CancellationToken, Task<string>>? OnInference { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, tags = ModelTags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = FittedContext } });
            if (path == "/v1/chat/completions/input_tokens")
            {
                using var countBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var characters = countBody.RootElement.GetProperty("messages").EnumerateArray()
                    .Sum(message => message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString()!.Length : 0);
                var count = CountActualCharacters ? characters * 2 / 3 + 96 : 3200;
                Counts.Add(count);
                return Json(new { input_tokens = count });
            }
            if (path is "/sessions/prepare" or "/sessions/save") return Json(new { status = "saved" });
            if (path == "/v1/chat/completions")
            {
                InferenceCount++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                LastOutputLimit = body.RootElement.GetProperty("max_tokens").GetInt32();
                Assert.False(body.RootElement.TryGetProperty("tools", out var tools)
                    && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0);
                var answer = OnInference is null ? "Vorherige Arbeit: geprüfte Ergebnisse und offene Fragen sind zusammengefasst."
                    : await OnInference(cancellationToken);
                return Json(new { choices = new[] { new { message = new { role = "assistant", content = answer }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 3200, completion_tokens = 40 } });
            }
            throw new InvalidOperationException("Unexpected endpoint: " + path);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
