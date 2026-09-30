using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class SciencePresentationProgressTests
{
    [Fact]
    public async Task ProgressSurvivesReloadAndCoalescesRepeatedPhaseWithoutLosingParagraphPosition()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Untersuchung", ChatMode.ClaudeScience);
        var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Einleitung", MessageStatus.Streaming);
        var run = Run(session.Id, assistant.Id);
        var start = MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 10, RunEventTypes.ResearchProblemInterpreted), [], 10)!;
        var stored = await chats.SaveToolStepAsync(assistant.Id, start);
        var first = MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 20, RunEventTypes.ResearchEvidenceExtracted, 1), stored, 25)!;
        await chats.SaveToolStepAsync(assistant.Id, first);

        // A new consumer uses the persisted receipt, not a transient deduplication set.
        var reloaded = Assert.Single(await chats.ListMessagesAsync(session.Id)).ToolSteps;
        Assert.Null(MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 20, RunEventTypes.ResearchEvidenceExtracted, 1), reloaded, 100));
        Assert.Null(MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 10, RunEventTypes.ResearchProblemInterpreted), reloaded, 100));
        var second = MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 30, RunEventTypes.ResearchEvidenceExtracted, 2), reloaded, 100)!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(25, second.ContentOffset);
        Assert.Contains("Quellenabruf 2", second.Detail);
        stored = await chats.SaveToolStepAsync(assistant.Id, second);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, step => Assert.Equal("assistant.narration", step.Tool));
    }

    [Fact]
    public void ForeignRunOrProjectAndNonResearchEventsNeverBecomeScienceNarration()
    {
        var run = Run(Guid.NewGuid(), Guid.NewGuid());
        var receipt = Event(run, 10, RunEventTypes.ResearchProblemInterpreted);
        Assert.Null(MissumAiAssistantService.BuildScienceProgressNarration(run, receipt with { RunId = "other-run" }, [], 0));
        Assert.Null(MissumAiAssistantService.BuildScienceProgressNarration(run,
            receipt with { Data = JsonSerializer.SerializeToElement(new { projectId = "research-other" }) }, [], 0));
        Assert.Null(MissumAiAssistantService.BuildScienceProgressNarration(run,
            receipt with { Type = RunEventTypes.ModelGeneration }, [], 0));
    }

    [Fact]
    public void AnotherResearchInvocationPreservesEarlierNarrativeAndDoesNotInventSuccess()
    {
        var run = Run(Guid.NewGuid(), Guid.NewGuid());
        var start = MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 10, RunEventTypes.ResearchProblemInterpreted), [], 0)!;
        var end = MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 20, RunEventTypes.ResearchReportCompleted), [start], 0)!;
        Assert.DoesNotContain("erfolgreich", end.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verifiziert", end.Detail, StringComparison.OrdinalIgnoreCase);
        var newStart = MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 30, RunEventTypes.ResearchProblemInterpreted), [start, end], 120)!;
        Assert.NotEqual(start.Id, newStart.Id);
        Assert.Equal(120, newStart.ContentOffset);
        var newSearch = MissumAiAssistantService.BuildScienceProgressNarration(run,
            Event(run, 40, RunEventTypes.ResearchSearchCompleted), [start, end, newStart], 125)!;
        Assert.Contains(":30:search", newSearch.Id);
    }

    private static MissumAiRunRecord Run(Guid session, Guid message)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), session, message, null, Guid.NewGuid().ToString("N"), "run-fixture", 0,
            "running", "model", null, now, now);
    }

    private static RunEvent Event(MissumAiRunRecord run, long id, string type, int completed = 0) =>
        new(id, run.ServerRunId!, type, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { projectId = "research-" + run.SessionId.ToString("N"), completed, total = 4 }));
}
