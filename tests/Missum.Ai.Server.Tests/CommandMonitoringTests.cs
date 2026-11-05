using System.Net;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Ai.Server.Tests;

public sealed class CommandMonitoringTests
{
    private static JsonElement J(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static readonly LmToolCall Command = new("running-command", ClientToolNames.CodingCommand,
        J("""{"executable":"python","arguments":["calculation.py"]}"""));
    private static readonly LmToolCall Read = new("following-read", ClientToolNames.CodingRead, J("""{"path":"result.json"}"""));

    [Fact]
    public void MonitoringStartsAfterThirtySecondsAndRequiresFreshObservationsAfterEachMinute()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var fresh = new ClientToolProgress("p", 1, 30_000, "", "", false);
        var empty = new CommandMonitoringCheckpoint(null, 0, null, []);
        Assert.False(CommandMonitoringPolicy.IsDue(fresh with { ElapsedMilliseconds = 29_999 }, empty, now));
        Assert.True(CommandMonitoringPolicy.IsDue(fresh, empty, now));
        var last = new CommandMonitoringCheckpoint("p", 1, now, []);
        Assert.False(CommandMonitoringPolicy.IsDue(fresh, last, now.AddSeconds(120)));
        Assert.False(CommandMonitoringPolicy.IsDue(fresh with { Revision = 2 }, last, now.AddSeconds(59)));
        Assert.True(CommandMonitoringPolicy.IsDue(fresh with { Revision = 2 }, last, now.AddSeconds(60)));
        Assert.True(CommandMonitoringPolicy.IsDue(fresh with { ProposalId = "next" }, last, now));
    }

    [Fact]
    public void InterimReceiptsCloseAllPendingCallsTruthfullyWithoutDuplicatingTheirFinalResults()
    {
        var messages = new List<LmChatMessage> { new("user", "Berechnen und prüfen."), new("assistant", ToolCalls: [Command, Read]) };
        var progress = new ClientToolProgress("p", 1, 30_000, "Arbeitet", "", false);
        var state = CommandMonitoringPolicy.ClosePendingCalls(messages, Command, progress, new(null, 0, null, []), DateTimeOffset.UtcNow);
        var receipts = messages.Where(item => item.Role == "tool").ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.Equal("running", J(receipts[0].Content!).GetProperty("status").GetString());
        Assert.Equal("not_started", J(receipts[1].Content!).GetProperty("status").GetString());
        Assert.All(receipts, receipt =>
        {
            Assert.False(J(receipt.Content!).GetProperty("success").GetBoolean());
            Assert.False(J(receipt.Content!).GetProperty("final").GetBoolean());
        });
        state = CommandMonitoringPolicy.ClosePendingCalls(messages, Command, progress with { Revision = 2 }, state, DateTimeOffset.UtcNow);
        Assert.Equal(2, messages.Count(item => item.Role == "tool"));
        foreach (var call in new[] { Command, Read })
        {
            var final = CommandMonitoringPolicy.ToolResult(call, "actual final receipt", state);
            Assert.Equal("user", final.Role);
            Assert.Null(final.ToolCallId);
            Assert.StartsWith(CommandMonitoringPolicy.DeferredResultMarker, final.Content);
            Assert.Contains(call.Id, final.Content);
        }
        Assert.Equal("tool", CommandMonitoringPolicy.ToolResult(new("other", "coding.read", J("{}")), "{}", state).Role);
    }

    [Theory]
    [InlineData("{\"message\":\"Die Berechnung läuft weiter.\",\"action\":\"continue\"}", "continue")]
    [InlineData("```json\n{\"message\":\"Ich stoppe gezielt den fehlerhaften Prozess.\",\"action\":\"stop\",\"reason\":\"Wiederholter konkreter Fehler im letzten Ausgabeblock.\"}\n```", "stop")]
    public void StructuredDecisionsAcceptOnlyUsableStatusMessages(string response, string action) =>
        Assert.Equal(action, CommandMonitoringPolicy.Parse(response).Action);

    [Fact]
    public void MonitoringPromptIsBoundedIndependentlyOfTheMainConversation()
    {
        var hugeTask = new string('x', 250_000);
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create(hugeTask),
            J("""{"steps":[{"id":"phase-1","title":"Symbolische Rechnung profilieren","status":"in_progress"}]}"""));
        var prompt = CommandMonitoringPolicy.CreatePrompt(hugeTask, state, Command,
            new("p", 3, 120_000, new string('o', 16_000), new string('e', 16_000), false,
                ProcessId: 42, CpuMilliseconds: 110_000, WorkingSetBytes: 5_000_000,
                LastOutputElapsedMilliseconds: 100_000, OutputCharacters: 32_000));

        Assert.Equal(3, prompt.Count);
        Assert.True(prompt.Sum(message => message.Content?.Length ?? 0) < 55_000);
        Assert.Contains("Symbolische Rechnung profilieren", prompt[1].Content);
        Assert.Contains("outputCharacters", prompt[2].Content);
        Assert.DoesNotContain(new string('x', 20_000), string.Concat(prompt.Select(message => message.Content)));
    }

    [Fact]
    public void MonitoringPromptAlsoBoundsLargeCommandArguments()
    {
        var command = Command with
        {
            Arguments = J(JsonSerializer.Serialize(new
            {
                executable = "python",
                arguments = Enumerable.Range(0, 64).Select(index => index + "-" + new string('a', 4_000)).ToArray(),
            })),
        };
        var prompt = CommandMonitoringPolicy.CreatePrompt("Auftrag", null, command,
            new("p", 1, 30_000, "", "", false));

        Assert.True(prompt.Sum(message => message.Content?.Length ?? 0) < 30_000);
        var instruction = prompt[2].Content!;
        using var data = JsonDocument.Parse(instruction[(instruction.LastIndexOf('\n') + 1)..]);
        Assert.EndsWith("[für die Prozessprüfung gekürzt]", data.RootElement.GetProperty("argumentsJson").GetString());
    }

    [Theory]
    [InlineData("{\"message\":\"\",\"action\":\"continue\"}")]
    [InlineData("{\"message\":\"Stopp\",\"action\":\"stop\"}")]
    [InlineData("{\"message\":\"Stopp\",\"action\":\"kill\",\"reason\":\"x\"}")]
    public void InvalidDecisionsCannotRequestAProcessStop(string response) =>
        Assert.Throws<JsonException>(() => CommandMonitoringPolicy.Parse(response));

    [Fact]
    public void LoopGuardIgnoresRunningAndNotStartedReceiptsAndRecognizesTheirLaterAuthoritativeResults()
    {
        var messages = new List<LmChatMessage>();
        var failed = new List<LmChatMessage>();
        for (var index = 0; index < 2; index++)
        {
            var call = Command with { Id = "command-" + index };
            var state = new CommandMonitoringCheckpoint("p-" + index, 1, DateTimeOffset.UtcNow, [call.Id]);
            var invocation = new LmChatMessage("assistant", ToolCalls: [call]);
            var interim = new LmChatMessage("tool", JsonSerializer.Serialize(new
                { status = index == 0 ? "running" : "not_started", final = false, success = false }), ToolCallId: call.Id);
            messages.Add(invocation);
            messages.Add(interim);
            failed.Add(invocation);
            failed.Add(interim);
            messages.Add(CommandMonitoringPolicy.ToolResult(call, """{"status":"completed","result":{"success":true,"exitCode":0}}""", state));
            failed.Add(CommandMonitoringPolicy.ToolResult(call, """{"status":"failed","result":{"success":false,"exitCode":1}}""", state));
        }
        CodingLoopGuard.ThrowIfRepeatedFailure(messages, Command with { Id = "retry" });
        Assert.Throws<AgentRunLimitException>(() => CodingLoopGuard.ThrowIfRepeatedFailure(failed, Command with { Id = "retry" }));
        // Redelivery of one failed final receipt is still one actual failed call.
        CodingLoopGuard.ThrowIfRepeatedFailure([.. failed.Take(3), failed[2]], Command with { Id = "retry" });
    }

    [Fact]
    public void RenderGuardAllowsAnUnstartedMonitoredSiblingButRecognizesItsActualDeferredCompletion()
    {
        var render = new LmToolCall("render", ClientToolNames.CodingRenderHtml, J("""{"code":"<p>Plot</p>","title":"Plot"}"""));
        var messages = new List<LmChatMessage> { new("assistant", ToolCalls: [Command, render]) };
        var progress = new ClientToolProgress("p", 1, 30_000, "", "", false);
        var state = CommandMonitoringPolicy.ClosePendingCalls(messages, Command, progress, new(null, 0, null, []), DateTimeOffset.UtcNow);
        CodingLoopGuard.ThrowIfRenderAlreadyUsed(messages, render);
        messages.Add(CommandMonitoringPolicy.ToolResult(render, """{"success":true,"previewDisplayed":true}""", state));
        Assert.Throws<ArgumentException>(() => CodingLoopGuard.ThrowIfRenderAlreadyUsed(messages, render with { Id = "second-render" }));
    }

    [Fact]
    public void CompactionKeepsTheUserTaskAndLatestMonitorRatherThanMistakingFinalReceiptsForInstructions()
    {
        var messages = new List<LmChatMessage> { new("system", "System"), new("user", "Ursprüngliche Forschungsfrage") };
        var monitoring = new CommandMonitoringCheckpoint("p", 1, DateTimeOffset.UtcNow, [Command.Id]);
        messages.Add(new("assistant", ToolCalls: [Command]));
        messages.Add(new("tool", "running", ToolCallId: Command.Id));
        for (var index = 0; index < 30; index++)
        {
            messages.Add(new("user", CommandMonitoringPolicy.Instruction(Command, new("p", index + 1, 30_000 + index * 60_000, new string('x', 1000), "", false))));
            messages.Add(new("assistant", "{\"message\":\"Weiter\",\"action\":\"continue\"}"));
        }
        var latest = messages[^2];
        messages.Add(CommandMonitoringPolicy.ToolResult(Command, "Tatsächlicher späterer Abschluss", monitoring));
        var plan = Assert.IsType<CodingCompactionPlan>(CodingContextCompactor.Plan(messages, 8192, force: true));
        Assert.Equal("Ursprüngliche Forschungsfrage", plan.CurrentRequest.Content);
        var compacted = CodingContextCompactor.Complete(plan, "Prozess war aktiv und ist nun abgeschlossen.");
        Assert.Contains(latest, compacted);
        Assert.Single(compacted, item => item.Content?.Contains(CommandMonitoringPolicy.InstructionMarker, StringComparison.Ordinal) == true);
        Assert.True(compacted.Length < messages.Count);
    }

    [Fact]
    public async Task PendingCommandProducesNormalTextBeforeItsResultWhilePreservingDispatchAndRoundBudget()
    {
        using var harness = new Harness();
        var run = await harness.SeedPendingAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, timeout.Token));
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Contains(events, item => item.Type == RunEventTypes.TextDelta
            && item.Data.GetProperty("delta").GetString()!.Contains("Die Berechnung läuft", StringComparison.Ordinal)
            && item.Data.GetProperty("round").GetInt32() == 7);
        Assert.DoesNotContain(events, item => item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed);
        Assert.Null(await harness.Repository.GetClientToolResultAsync(Harness.ProposalId));
        var saved = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(7, saved.RoundCount);
        Assert.Equal(128, saved.InputTokens);
        Assert.Equal(8, saved.OutputTokens);
        Assert.Equal(Harness.ProposalId, saved.PendingProposalId);
        Assert.Equal(Command.Id, saved.PendingToolCallId);
        Assert.Equal(0, saved.NextToolIndex);
        Assert.Equal(2, saved.ActiveToolCalls!.Count);
        Assert.Equal(2, saved.CommandMonitoring!.ClosedToolCallIds.Count);
        var body = Assert.Single(harness.Handler.Bodies);
        Assert.Equal("none", body.GetProperty("tool_choice").GetString());
        Assert.Equal("none", body.GetProperty("reasoning_effort").GetString());
        Assert.Equal(512, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(Handler.Model, body.GetProperty("model").GetString());
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schemaContract = format.GetProperty("json_schema");
        Assert.True(schemaContract.GetProperty("strict").GetBoolean());
        var schema = schemaContract.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["message", "action", "reason"], schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["continue", "stop"], schema.GetProperty("properties").GetProperty("action").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.All(schema.GetProperty("properties").EnumerateObject(), property =>
            Assert.Equal("string", property.Value.GetProperty("type").GetString()));
        var monitorMessages = body.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["system", "user", "user"], monitorMessages.Select(item => item.GetProperty("role").GetString()));
        var boundedContext = monitorMessages[1].GetProperty("content").GetString()!;
        using (var contextDocument = JsonDocument.Parse(boundedContext[(boundedContext.IndexOf('\n') + 1)..]))
            Assert.Equal("Berechne und prüfe das Ergebnis.", contextDocument.RootElement.GetProperty("originalTask").GetString());
        Assert.Contains("Berechnung gestartet", monitorMessages[2].GetProperty("content").GetString());
        Assert.Equal(0, harness.Handler.Loads);
        Assert.DoesNotContain(harness.Handler.Paths, path => path.StartsWith("/sessions/", StringComparison.Ordinal));
        // Replayed heartbeat cannot produce another status or spend a round.
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, timeout.Token));
        Assert.Single(harness.Handler.Bodies);
    }

    [Fact]
    public async Task MonitoringStopRequestsOnlyItsCommandAndWaitsForTheRealClientReceipt()
    {
        using var harness = new Harness();
        harness.Handler.Decision = """{"message":"Ich stoppe den Prozess wegen des wiederholten konkreten Rechenfehlers.","action":"stop","reason":"Die Ausgabe meldet wiederholt Division durch null."}""";
        var run = await harness.SeedPendingAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, timeout.Token));
        var response = await harness.Repository.SaveClientToolProgressAsync(run,
            new(Harness.ProposalId, 2, 35_000, "ZeroDivisionError", "", false));
        Assert.True(response.StopRequested);
        Assert.Equal("Die Ausgabe meldet wiederholt Division durch null.", response.Reason);
        Assert.Equal(RunState.WaitingForClient, (await harness.Repository.GetAsync(run))!.State);
        Assert.Null(await harness.Repository.GetClientToolResultAsync(Harness.ProposalId));
    }

    [Fact]
    public async Task RejectedMonitorToolTextCannotPolluteSharedHistoryOrStopTheCommand()
    {
        using var harness = new Harness();
        const string rejected = "<tool_call>coding.updatePlan</tool_call>";
        harness.Handler.Decision = rejected;
        var run = await harness.SeedPendingAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, timeout.Token));
        var saved = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.DoesNotContain(saved.Messages, message => message.Content == rejected);
        Assert.Equal(128, saved.InputTokens);
        Assert.Equal(8, saved.OutputTokens);
        Assert.Equal(7, saved.RoundCount);
        Assert.False((await harness.Repository.SaveClientToolProgressAsync(run,
            new(Harness.ProposalId, 2, 35_000, "", "", false))).StopRequested);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Contains(events, item => item.Type == RunEventTypes.TextDelta
            && item.Data.GetProperty("delta").GetString()!.Contains("inhaltliche AI-Prüfung", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunFailed);
    }

    [Fact]
    public async Task MonitoringProviderFailureLeavesTheCommandPendingAndCanBeRetriedLater()
    {
        using var harness = new Harness();
        harness.Handler.FailGeneration = true;
        var run = await harness.SeedPendingAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, timeout.Token));
        Assert.Equal(RunState.WaitingForClient, (await harness.Repository.GetAsync(run))!.State);
        var saved = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(Harness.ProposalId, saved.PendingProposalId);
        Assert.NotNull(saved.CommandMonitoring!.LastAttemptAt);
        Assert.Equal(7, saved.RoundCount);
        Assert.False((await harness.Repository.SaveClientToolProgressAsync(run, new(Harness.ProposalId, 2, 95_000, "", "", false))).StopRequested);
        var fallbackEvents = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Contains(fallbackEvents, item => item.Type == RunEventTypes.TextDelta
            && item.Data.GetProperty("delta").GetString()!.Contains("inhaltliche AI-Prüfung", StringComparison.Ordinal));
        Assert.DoesNotContain(fallbackEvents, item => item.Type == RunEventTypes.RunFailed);
        await harness.Repository.SaveCheckpointAsync(run, saved with
        { CommandMonitoring = saved.CommandMonitoring with { LastAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-2) } });
        harness.Handler.FailGeneration = false;
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, timeout.Token));
        Assert.Contains(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.TextDelta);
    }

    [Fact]
    public async Task ActualCompletionDuringTheModelCallWinsOverALateStopAndKeepsTheBatchHistoryValid()
    {
        using var harness = new Harness();
        harness.Handler.Decision = """{"message":"Ich stoppe gezielt.","action":"stop","reason":"Konkreter Ausgabefehler."}""";
        var run = await harness.SeedPendingAsync();
        harness.Handler.DuringGeneration = () => harness.Repository.SaveClientToolResultAsync(run,
            new(Harness.ProposalId, "completed", J("""{"success":true,"stdout":"Fertig","exitCode":0}""")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, timeout.Token));
        var saved = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(1, saved.NextToolIndex);
        Assert.Equal(Read.Id, saved.PendingToolCallId);
        Assert.NotEqual(Harness.ProposalId, saved.PendingProposalId);
        Assert.Single(saved.Messages, item => item.Role == "tool" && item.ToolCallId == Command.Id);
        Assert.Single(saved.Messages, item => item.Role == "tool" && item.ToolCallId == Read.Id);
        Assert.Contains(saved.Messages, item => item.Role == "user"
            && item.Content?.StartsWith(CommandMonitoringPolicy.DeferredResultMarker, StringComparison.Ordinal) == true
            && item.Content.Contains("Fertig", StringComparison.Ordinal));
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.TextDelta);
        Assert.False(await harness.Repository.RequestClientToolStopAsync(run, Harness.ProposalId, "Abschluss ist bereits vorhanden."));
    }

    private sealed class Harness : IDisposable
    {
        internal const string ProposalId = "pending-command";
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        internal Handler Handler { get; } = new();
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }
        private RunRequest Request { get; }
        internal Harness()
        {
            _context.Options.ModelRuntimeUri = new("http://native.test:8081");
            _http = new(Handler);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(new ModelRuntimeClient(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
            Request = new(MissumAiProtocol.Version, RunMode.Coding, [new("user", [new("text", Text: "Berechne und prüfe das Ergebnis.")])],
                ClientCapabilities: ["coding", "coding.process"], AllowedServerTools: [], SessionId: "command-monitoring",
                PreferredGeneralModelId: Handler.Model, PreferredCodingModelId: Handler.Model,
                CodingOptions: new(WorkspacePath: _context.Root, ContinueSessionContext: true),
                Limits: new(TimeoutSeconds: 0), ContextProfileVersion: "compact-v1", ReasoningEffort: "high");
        }
        internal async Task<string> SeedPendingAsync()
        {
            var run = (await Repository.CreateAsync(Request, null)).Snapshot.RunId;
            var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Berechne und prüfe das Ergebnis."),
                J("""{"steps":[{"id":"s1","title":"Rechnung überprüfen","status":"in_progress"}]}"""));
            var messages = RunProcessor.CreateInitialMessages(Request, "coding", [], "compact-v1").ToList();
            messages.Add(new("assistant", ToolCalls: [Command, Read]));
            await Repository.SaveCheckpointAsync(run, new(messages, 7, 2, 0, 0, ActiveToolCalls: [Command, Read],
                PendingProposalId: ProposalId, PendingToolCallId: Command.Id, WorkingState: state, ActiveCallRound: 7,
                SelectedModelId: Handler.Model, SelectedReasoningEffort: "high", ContextProfileVersion: "compact-v1", SciencePlanInitialized: true));
            await Repository.SaveToolProposalAsync(new(ProposalId, run, ClientToolNames.CodingCommand, Command.Arguments,
                ToolRiskClass.Process, "Berechnung", DateTimeOffset.MaxValue));
            await Repository.EnsureClientToolProposedEventAsync(run, ProposalId);
            await Repository.SaveClientToolProgressAsync(run, new(ProposalId, 1, 30_000, "Berechnung gestartet", "", false,
                ProcessId: 123, CpuMilliseconds: 29_000, WorkingSetBytes: 1_000_000, LastOutputElapsedMilliseconds: 1_000, OutputCharacters: 21));
            return run;
        }
        public void Dispose() { _services.Dispose(); _http.Dispose(); _context.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal const string Model = "coding/qwen3.8-27b~command-monitoring";
        private static readonly string[] Tags = ["missum-context-train:32768", "missum-reasoning-mode:llama-native",
            "missum-reasoning-levels:none|low|high", "missum-reasoning-default:high"];
        internal List<JsonElement> Bodies { get; } = [];
        internal List<string> Paths { get; } = [];
        internal string Decision { get; set; } = """{"message":"Die Berechnung läuft mit aktiver CPU weiter; fehlende Ausgabe allein belegt keine Endlosschleife.","action":"continue"}""";
        internal bool FailGeneration { get; set; }
        internal Func<Task>? DuringGeneration { get; set; }
        internal int Loads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/v1/models") return Response(new { data = new[]
                { new { id = Model, tags = Tags, status = new { value = "loaded" } } } });
            if (path == "/props") return Response(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/models/load") { Loads++; return Response(new { success = true }); }
            if (path.StartsWith("/sessions/", StringComparison.Ordinal)) return Response(new { status = "resident" });
            if (path == "/v1/chat/completions/input_tokens") return Response(new { input_tokens = 128 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected native endpoint: " + path);
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token)));
            if (DuringGeneration is { } during) await during();
            if (FailGeneration) return new(HttpStatusCode.InternalServerError)
                { Content = new StringContent("{\"error\":\"model temporarily unavailable\"}", Encoding.UTF8, "application/json") };
            return Response(new { choices = new[] { new { message = new { role = "assistant", content = Decision }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 128, completion_tokens = 8 } });
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
