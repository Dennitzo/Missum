using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class ScienceProgressStreamTests
{
    private const string RunId = "run-science-progress-stream";
    private const string Report = "## Ergebnis\n\nDie Größen sind dimensionsgleich: $E=mc^2$.";

    [Theory]
    [InlineData(ChatMode.ClaudeScience, 6)]
    [InlineData(ChatMode.General, 0)]
    public async Task ReattachedResearchEventsBecomeDurableNarrativeOnlyInScience(ChatMode mode, int expectedParagraphs)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var session = await chats.CreateSessionAsync("Forschungsfortschritt", mode);
        var turn = await chats.AddTurnAsync(session.Id, "Untersuche den Zusammenhang wissenschaftlich.");
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, turn.AssistantMessage.Id, null,
            Guid.NewGuid().ToString("N"), RunId, 5, "running", "fixture-model", null, now, now));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
            { MissumAiServerUrl = "http://127.0.0.1:65000", ActiveSessionId = session.Id });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new Handler(session.Id));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runs, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<MissumAiAssistantService>.Instance);
        var updates = new ConcurrentQueue<MissumAiAssistantUpdate>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.ResumePendingAsync(update => { updates.Enqueue(update); return Task.CompletedTask; }, timeout.Token);

        Assert.DoesNotContain(updates, update => update.Kind == MissumAiAssistantUpdateKind.Failed);
        Assert.Equal("completed", (await runs.GetAsync(run.Id))!.State);
        var completed = Assert.Single(updates, update => update.Kind == MissumAiAssistantUpdateKind.Completed);
        Assert.Equal(Report, completed.Message.Content);
        var restored = Assert.Single(await chats.ListMessagesAsync(session.Id), message => message.Id == turn.AssistantMessage.Id);
        var narration = (restored.ToolSteps ?? []).Where(step => step.Tool == "assistant.narration").ToArray();
        Assert.Equal(expectedParagraphs, narration.Length);
        Assert.Contains(restored.ToolSteps!, step => step.Tool == "web.fetch" && step.Status == "completed");
        if (mode == ChatMode.ClaudeScience)
        {
            Assert.Contains(narration, step => step.Detail!.Contains("Quellenabruf 2", StringComparison.Ordinal));
            Assert.DoesNotContain(narration, step => step.Detail!.Contains("Quellenabruf 1", StringComparison.Ordinal));
            Assert.All(narration, step => Assert.Equal(0, step.ContentOffset));
            Assert.Contains(updates, update => update.Kind == MissumAiAssistantUpdateKind.Delta
                && update.Message.Content.Length == 0 && update.Message.ToolSteps?.Any(step => step.Tool == "assistant.narration") == true);
        }
    }

    private sealed class Handler(Guid sessionId) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                Assert.Equal("5", Assert.Single(request.Headers.GetValues(MissumAiHeaders.LastEventId)));
                var projectId = "research-" + sessionId.ToString("N");
                var events = new (string Type, object Data)[]
                {
                    (RunEventTypes.ResearchProblemInterpreted, new { projectId, completed = 0 }),
                    (RunEventTypes.ResearchPlanUpdated, new { projectId, completed = 0 }),
                    (RunEventTypes.ResearchSearchCompleted, new { projectId, completed = 0 }),
                    (RunEventTypes.ResearchEvidenceExtracted, new { projectId, completed = 1 }),
                    (RunEventTypes.ServerToolStarted, new { callId = "source-1", tool = "web.fetch", target = "Originalquelle" }),
                    (RunEventTypes.ServerToolCompleted, new { callId = "source-1", tool = "web.fetch", success = true, result = new { found = true } }),
                    (RunEventTypes.ResearchEvidenceExtracted, new { projectId, completed = 2 }),
                    (RunEventTypes.ResearchVerificationUpdated, new { projectId, completed = 2 }),
                    (RunEventTypes.ResearchReportCompleted, new { projectId, completed = 2 }),
                    (RunEventTypes.TextDelta, new TextDeltaEvent(Report)),
                    (RunEventTypes.RunCompleted, new RunCompletedEvent(null, "fixture-model", 20, 5)),
                };
                var stream = string.Concat(events.Select((item, index) => "data: " + JsonSerializer.Serialize(
                    new RunEvent(index + 6, RunId, item.Type, DateTimeOffset.UtcNow,
                        JsonSerializer.SerializeToElement(item.Data, Json)), Json) + "\n\n"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") });
            }
            if (path == "/v1/runs/" + RunId)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new RunSnapshot(RunId, RunState.Completed,
                        RunMode.General, "fixture-model", null, 30, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Json),
                        Encoding.UTF8, "application/json"),
                });
            throw new InvalidOperationException("Unexpected fixture request: " + request.Method + " " + path);
        }
    }
}
