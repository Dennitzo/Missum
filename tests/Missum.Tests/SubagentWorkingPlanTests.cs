using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class SubagentWorkingPlanTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletePlanSurvivesTheDisplayWindowAndMetadataOnlyProgress()
    {
        var child = Child();
        var plan = Plan("plan", "Herleitung prüfen", At);
        child = child with { AssistantMessage = child.AssistantMessage with
        {
            ToolSteps = new[] { plan }.Concat(Enumerable.Range(0, 90)
                .Select(index => new AssistantToolStep("later-" + index, "coding.read", "completed"))).ToArray(),
        } };
        var displayed = child.ForDisplay();
        Assert.Equal(64, displayed.AssistantMessage.ToolSteps!.Count);
        Assert.DoesNotContain(displayed.AssistantMessage.ToolSteps, step => step.Tool == "coding.updatePlan");
        AssertPlan(displayed.WorkingPlan, "plan", "Herleitung prüfen");
        Assert.Equal(displayed.WorkingPlan, displayed.ForDisplay().WorkingPlan);
        var progressed = child.Apply(new RunEvent(1, child.RunId, RunEventTypes.ModelGeneration, At.AddSeconds(1),
            JsonSerializer.SerializeToElement(new ModelGenerationEvent("decoding", GeneratedTokens: 30), SubagentChatState.JsonOptions)));
        AssertPlan(progressed.ForDisplay().WorkingPlan, "plan", "Herleitung prüfen");
        // The unchanged list reuses the same compact JSON instead of parsing historical tools again.
        Assert.Equal(displayed.WorkingPlan, progressed.ForDisplay().WorkingPlan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryRetainsThePlanWithoutOpeningTheStoredTranscript(bool external)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var child = Child();
        child = child with { AssistantMessage = child.AssistantMessage with
        {
            Content = external ? new string('x', 40_000) : "Ergebnis",
            ToolSteps = [Plan("saved-plan", "Numerik vergleichen", At)],
        } };
        var receipt = await child.PersistAsync(environment.Directory);
        var parent = child.AssistantMessage with { Id = Guid.NewGuid(), ToolSteps =
            [new(child.ReceiptId, "subagent", "running", OutputJson: receipt)] };
        FileStream? locked = null;
        try
        {
            if (external)
            {
                using var parsed = JsonDocument.Parse(receipt);
                var path = Path.Combine(environment.Directory, "Subagents", parsed.RootElement.GetProperty("subagentStorageKey").GetString() + ".json");
                locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            var summary = Assert.Single(SubagentChatState.ReadSummaries([parent]));
            Assert.True(summary.TranscriptDeferred);
            Assert.Empty(summary.AssistantMessage.ToolSteps ?? []);
            AssertPlan(summary.ForDisplay().WorkingPlan, "saved-plan", "Numerik vergleichen");
        }
        finally { locked?.Dispose(); }
        var restored = await SubagentChatState.ReadAsync(parent, child.AgentId, environment.Directory);
        AssertPlan(restored!.ForDisplay().WorkingPlan, "saved-plan", "Numerik vergleichen");
    }

    [Fact]
    public void PreviousAttemptPlanIsAvailableUntilANewSuccessfulPlanReplacesIt()
    {
        var child = Child();
        var old = child.AssistantMessage with { ToolSteps = [Plan("old-plan", "Alte Etappe", At)] };
        child = child with { PreviousMessages = [child.UserMessage, old],
            AssistantMessage = child.AssistantMessage with { Id = Guid.NewGuid(), ToolSteps = [] } };
        AssertPlan(child.ForDisplay().WorkingPlan, "old-plan", "Alte Etappe");
        child = child with { AssistantMessage = child.AssistantMessage with { ToolSteps =
            [Plan("failed-plan", "Nicht gespeichert", At.AddSeconds(1)) with { OutputJson = "{\"success\":false}" }] } };
        AssertPlan(child.ForDisplay().WorkingPlan, "old-plan", "Alte Etappe");
        child = child with { AssistantMessage = child.AssistantMessage with { ToolSteps =
            [new("unfinished-plan", "coding.updatePlan", "completed", InputJson: "{\"steps\":[]}")] } };
        AssertPlan(child.ForDisplay().WorkingPlan, "old-plan", "Alte Etappe");
        child = child with { AssistantMessage = child.AssistantMessage with { ToolSteps =
            [Plan("new-plan", "Neue Etappe", At.AddSeconds(2))] } };
        AssertPlan(child.ForDisplay().WorkingPlan, "new-plan", "Neue Etappe");
    }

    [Fact]
    public async Task CoordinatorEmitsPlanMetadataEvenWhenThePlanIsOutsideTheToolWindow()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var activity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), null, settings, activity);
        var child = Child();
        child = child with { AssistantMessage = child.AssistantMessage with { ToolSteps =
            new[] { Plan("dto-plan", "Quellen prüfen", At) }.Concat(Enumerable.Range(0, 70)
                .Select(index => new AssistantToolStep("read-" + index, "coding.read", "completed"))).ToArray() } };
        JsonElement snapshot = default;
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.SubagentChanged,
            child.AssistantMessage, Subagent: child), (type, payload, _) =>
        {
            if (type == "subagent.snapshot") snapshot = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
            return Task.CompletedTask;
        }, "subagent-plan");
        AssertPlan(snapshot.GetProperty("workingPlan"), "dto-plan", "Quellen prüfen");
        Assert.DoesNotContain(snapshot.GetProperty("messages").EnumerateArray().SelectMany(message => message.GetProperty("toolSteps").EnumerateArray()),
            step => step.GetProperty("tool").GetString() == "coding.updatePlan");
    }

    private static SubagentChatState Child() => SubagentChatState.Create(
        new SubagentRunEvent("parent", "child", "agent", "Forschung prüfen", "model", RunState.Running), Guid.NewGuid(), At);

    private static AssistantToolStep Plan(string id, string title, DateTimeOffset at) => new(id, "coding.updatePlan", "completed",
        OutputJson: JsonSerializer.Serialize(new { success = true,
            storedSteps = new[] { new { id = "stage", title, status = "in_progress" } },
            storedAcceptanceCriteria = new[] { new { id = "check", title = "Vergleich belegt", status = "pending" } } }), UpdatedAt: at);

    private static void AssertPlan(JsonElement? metadata, string id, string title)
    {
        Assert.True(metadata.HasValue);
        var plan = metadata.Value;
        Assert.Equal(id, plan.GetProperty("stepId").GetString());
        Assert.Equal(title, Assert.Single(plan.GetProperty("steps").EnumerateArray()).GetProperty("title").GetString());
        Assert.Single(plan.GetProperty("acceptanceCriteria").EnumerateArray());
        Assert.True(plan.TryGetProperty("updatedAt", out _));
    }
}
