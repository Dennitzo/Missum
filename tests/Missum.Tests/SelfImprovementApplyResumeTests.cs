using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Tests;

/// <summary>
/// The app side of the reattach: after the restart the real assistant service finds the apply proposal that
/// was still 'executing', takes the broker's durable terminal state as its result and submits it to the same
/// gateway run of the same chat exactly once, also when the acknowledgement of the first submission is lost.
/// </summary>
[Collection(SelfImprovementApplyTestGroup.Name)]
public sealed class SelfImprovementApplyResumeTests
{
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    private const string RunId = "run-apply";
    private const string ProposalId = "apply-1";

    [Fact]
    public async Task ResumedChatSubmitsTheBrokerResultToTheSameRunExactlyOnceDespiteALostAck()
    {
        using var scope = SelfImprovementApply.BeginTestScope();
        await using var environment = await TestEnvironment.CreateAsync();
        SelfImprovementApply.StateRootOverride = Path.Combine(environment.Directory, "SelfImprovement");
        var (session, message) = await SeedAsync(environment);
        var handler = new GatewayHandler { LoseFirstAck = true };
        using var host = await Host.CreateAsync(environment, handler);

        // Start after the restart: the gateway stores the result, but its acknowledgement is lost; the
        // journal still holds the receipt, so the reconnect submits it again, byte for byte.
        await host.Service.ResumePendingAsync(_ => Task.CompletedTask);
        Assert.True(handler.Submissions.Count >= 2, "The lost acknowledgement was not retried.");
        var first = handler.Submissions[0];
        Assert.All(handler.Submissions, submission => Assert.Equal((RunId, first.Body), submission));
        using (var body = JsonDocument.Parse(first.Body))
        {
            Assert.Equal(ProposalId, body.RootElement.GetProperty("proposalId").GetString());
            Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
            var output = body.RootElement.GetProperty("result");
            Assert.Equal("applied", output.GetProperty("status").GetString());
            Assert.Equal(SelfImprovementApply.OperationId(session.Id, ProposalId), output.GetProperty("opId").GetString());
        }
        // The gateway deduplicates by proposal id, so the run continues once.
        Assert.Equal(1, handler.AcceptedResults);

        // Further starts find nothing left to deliver.
        var delivered = handler.Submissions.Count;
        await host.Service.ResumePendingAsync(_ => Task.CompletedTask);
        await host.Service.ResumePendingAsync(_ => Task.CompletedTask);
        Assert.Equal(delivered, handler.Submissions.Count);

        var chats = environment.Get<IChatRepository>();
        Assert.Equal(2, (await chats.ListMessagesAsync(session.Id)).Count);
        var resumed = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal(MessageStatus.Completed, resumed.Status);
        var step = Assert.Single(resumed.ToolSteps!, item => item.Id == ProposalId);
        Assert.Equal("completed", step.Status);
        var record = (await environment.Get<IClientToolExecutionRepository>().GetAsync(ProposalId))!;
        Assert.Equal(RunId, record.ServerRunId);
        Assert.NotNull(record.ResultJson);
        Assert.Null(SelfImprovementApply.HoldReason());
    }

    [Fact]
    public async Task ApplyWithoutBrokerOperationIsReportedAsUnknownAndNotRepeated()
    {
        using var scope = SelfImprovementApply.BeginTestScope();
        await using var environment = await TestEnvironment.CreateAsync();
        SelfImprovementApply.StateRootOverride = Path.Combine(environment.Directory, "SelfImprovement");
        await SeedAsync(environment, writeOperation: false);
        var handler = new GatewayHandler();
        using var host = await Host.CreateAsync(environment, handler);

        await host.Service.ResumePendingAsync(_ => Task.CompletedTask);
        var submission = Assert.Single(handler.Submissions);
        using var body = JsonDocument.Parse(submission.Body);
        Assert.Equal("failed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("client.tool_outcome_unknown", body.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public void OperationIdIsStablePerSessionAndProposal()
    {
        var session = Guid.NewGuid();
        var id = SelfImprovementApply.OperationId(session, ProposalId);
        Assert.Equal(32, id.Length);
        Assert.Equal(id, SelfImprovementApply.OperationId(session, ProposalId));
        Assert.NotEqual(id, SelfImprovementApply.OperationId(session, "apply-2"));
        Assert.NotEqual(id, SelfImprovementApply.OperationId(Guid.NewGuid(), ProposalId));
    }

    [Fact]
    public async Task GatewayOutsideThisMachineIsNotAcceptedAsLocallyManaged()
    {
        var (installation, problem) = await SelfImprovementApply.ResolveInstallationAsync(
            new(RunId, null, new Uri("http://192.168.0.67:8080/")), CancellationToken.None);
        Assert.Null(installation);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    private static async Task<(ChatSession Session, ChatMessage Message)> SeedAsync(TestEnvironment environment, bool writeOperation = true)
    {
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Selbstverbesserung", ChatMode.General);
        var turn = await chats.AddTurnAsync(session.Id, "Übernimm den geprüften Kandidaten.");
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, "Ich übernehme den Kandidaten.", MessageStatus.Interrupted);
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, new(ProposalId, SelfImprovementApply.ToolName, "running", "Übernahme läuft"));
        var now = DateTimeOffset.UtcNow;
        var run = await environment.Get<IMissumAiRunRepository>().CreateAsync(new(Guid.NewGuid(), session.Id, turn.AssistantMessage.Id,
            null, "apply-key", RunId, 8, "waitingForClient", "fixture/model", null, now, now));
        await environment.Get<IClientToolExecutionRepository>().BeginAsync(new(ProposalId, run.Id, RunId, 7,
            SelfImprovementApply.ToolName, "executing", null, now, now));
        if (writeOperation)
        {
            var directory = Directory.CreateDirectory(SelfImprovementApply.OperationDirectory(
                SelfImprovementApply.OperationId(session.Id, ProposalId))).FullName;
            await File.WriteAllTextAsync(Path.Combine(directory, "operation.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(new
            {
                status = "applied", phase = "done", candidateId = "cand-1", changeApp = true, changeStack = false,
                detail = "Kandidat übernommen.", completedAt = now.ToString("O"),
            }));
        }
        return ((await chats.GetSessionAsync(session.Id))!, (await chats.GetMessageAsync(turn.AssistantMessage.Id))!);
    }

    private sealed class Host(SettingsCoordinator settings, MissumAiConnectionService connection, MicrophoneTranscriptionService microphone,
        MissumAiAssistantService service) : IDisposable
    {
        internal MissumAiAssistantService Service => service;
        internal static async Task<Host> CreateAsync(TestEnvironment environment, GatewayHandler handler)
        {
            await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { MissumAiServerUrl = "http://127.0.0.1:65000", SelectedModel = "fixture/model", IsAutomaticSpeechEnabled = false });
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance, () => handler);
            var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
            var chats = environment.Get<IChatRepository>();
            var documents = environment.Get<IDocumentIngestor>();
            var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
                environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(), environment.Get<IBinaryObjectStore>(), documents,
                new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats), new LocalToolBroker(connection, documents, null!, chats),
                null!, microphone, settings, new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance),
                NullLogger<MissumAiAssistantService>.Instance);
            return new(settings, connection, microphone, service);
        }
        public void Dispose() { service.Dispose(); microphone.Dispose(); connection.Dispose(); settings.Dispose(); }
    }

    /// <summary>A gateway that keeps the run waiting for the apply result and continues it once that result arrives.</summary>
    private sealed class GatewayHandler : HttpMessageHandler
    {
        internal bool LoseFirstAck { get; init; }
        internal List<(string RunId, string Body)> Submissions { get; } = [];
        internal int AcceptedResults { get; private set; }
        private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            if (path == "/v1/health/live") return Response(new HealthSnapshot("live", MissumAiProtocol.Version, now));
            if (path == "/v1/models/status") return Response(new ModelStatusSnapshot(true, "fixture",
                [new("fixture/model", "general", true, true, "loaded", 131_072)], now));
            if (path == "/v1/capabilities") return Response(new CapabilitySnapshot(MissumAiProtocol.Version, "fixture", [],
                ["web.search", "web.fetch"], [], new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize));
            if (path == $"/v1/runs/{RunId}")
                return Response(new RunSnapshot(RunId, _accepted.Count > 0 ? RunState.Running : RunState.WaitingForClient,
                    RunMode.General, "fixture/model", null, 8, now, now));
            if (path == $"/v1/runs/{RunId}/client-tool-results" && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                Submissions.Add((RunId, body));
                using (var parsed = JsonDocument.Parse(body))
                    if (_accepted.Add(parsed.RootElement.GetProperty("proposalId").GetString()!)) AcceptedResults++;
                if (LoseFirstAck && Submissions.Count == 1) throw new HttpRequestException("Acknowledgement lost.");
                return new(HttpStatusCode.Accepted) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            if (path == $"/v1/runs/{RunId}/events")
            {
                var items = new[]
                {
                    new RunEvent(9, RunId, RunEventTypes.TextDelta, now, JsonSerializer.SerializeToElement(new TextDeltaEvent(" Übernahme abgeschlossen."), Json)),
                    new RunEvent(10, RunId, RunEventTypes.RunCompleted, now, JsonSerializer.SerializeToElement(new RunCompletedEvent(null, "fixture/model", 10, 10, []), Json)),
                };
                return new(HttpStatusCode.OK) { Content = new StringContent(string.Join("", items.Select(item => "data: " + JsonSerializer.Serialize(item, Json) + "\n\n")), Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException("Unexpected apply resume request: " + path);
        }

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    }
}
