using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.Ai.Server.Tests;

public sealed class CodingPlanValidationTests
{
    [Fact]
    public void MissingInitialCriterionStatusesDefaultToPendingWithoutRejectingTheCombinedPlan()
    {
        // Shape of the actual Science failure: ten complete steps, four criteria
        // with id/title but no status. Neither list needs a separate tool call
        // or a corrective retry: omitted new statuses safely mean pending.
        var original = JsonSerializer.SerializeToNode(new
        {
            steps = Enumerable.Range(1, 10).Select(index => new
            {
                id = $"s{index}", title = $"Forschungsschritt {index}", status = index == 1 ? "in_progress" : "pending",
            }),
            acceptanceCriteria = Enumerable.Range(1, 4).Select(index => new { id = $"a{index}", title = $"Prüfkriterium {index}" }),
            nextStep = "Die vorhandene Publikation lesen.", phase = "exploration",
        })!;
        var state = CodingWorkingState.Create("Bohmsche Mechanik prüfen.");
        var before = JsonSerializer.Serialize(state);
        var repaired = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(original));
        Assert.Equal(before, JsonSerializer.Serialize(state));
        Assert.Equal(10, repaired.Plan.Count);
        Assert.Equal(4, repaired.AcceptanceCriteria.Count);
        Assert.All(repaired.AcceptanceCriteria, item => Assert.Equal("pending", item.Status));
        Assert.Equal("in_progress", repaired.Plan[0].Status);
        Assert.Equal("exploration", repaired.Phase);
        Assert.Equal("Die vorhandene Publikation lesen.", repaired.NextStep);
    }

    [Theory]
    [InlineData("steps")]
    [InlineData("acceptanceCriteria")]
    public void MissingNewFieldsUseIndexedPathsWithoutBorrowingIdsFromTheOtherList(string section)
    {
        var otherSection = section == "steps" ? "acceptanceCriteria" : "steps";
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Task"),
            Args(new Dictionary<string, object> { [otherSection] = new[] { new { id = "a1", title = "Original", status = "pending" } } }));
        var error = Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new Dictionary<string, object> { [section] = new[] { new { id = "a1" } } })));
        Assert.Contains($"{section}[0].title", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"{section}[0].status", error.Message, StringComparison.Ordinal);
        Assert.Contains($"{section}: []", error.Message, StringComparison.Ordinal);
        Assert.Contains("anderen Liste", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidCombinedUpdateKeepsExistingPlanTitlesStatusesAndNavigation()
    {
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Task"), Args(new
        {
            steps = new[] { new { id = "s1", title = "Original step", status = "pending" } },
            acceptanceCriteria = new[] { new { id = "a1", title = "Original criterion", status = "pending" } },
            phase = "planning", nextStep = "Original next step",
        }));
        var before = JsonSerializer.Serialize(state);
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        {
            steps = new[] { new { id = "s1", title = "Original step", status = "in_progress" } },
            acceptanceCriteria = new[] { new { id = "a1", title = "Weaker criterion", status = "pending" } },
            phase = "review", nextStep = "Changed next step",
        })));
        Assert.Equal(before, JsonSerializer.Serialize(state));
    }

    [Fact]
    public void FullAndCompactPoliciesRequestCompleteHeadersAndJointLists()
    {
        Assert.Contains("id und title", CodingAgentPolicy.WorkingStatePrompt, StringComparison.Ordinal);
        Assert.Contains("pending", CodingAgentPolicy.WorkingStatePrompt, StringComparison.Ordinal);
        Assert.Contains("gemeinsam in einem Aufruf", CodingAgentPolicy.WorkingStatePrompt, StringComparison.Ordinal);
        Assert.Contains("vollständigen geordneten Plan", CodingAgentPolicy.WorkingStatePrompt, StringComparison.Ordinal);
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding, [new("user", [new("text", "Research")])]);
        var compact = CompactAgentContextPolicy.Build(request, ["coding.updatePlan"]);
        Assert.Contains("id/title", compact, StringComparison.Ordinal);
        Assert.Contains("pending", compact, StringComparison.Ordinal);
        Assert.Contains("steps und acceptanceCriteria gemeinsam", compact, StringComparison.Ordinal);
        Assert.Contains("vollständigen geordneten Plan", compact, StringComparison.Ordinal);
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Task"), Args(new
        { steps = new[] { new { id = "s1", title = "Original", status = "pending" } } }));
        var updated = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { steps = new[] { new { id = "s1", status = "in_progress" } } }));
        Assert.Equal("Original", Assert.Single(updated.Plan).Title);
        Assert.Equal("in_progress", updated.Plan[0].Status);
    }

    [Fact]
    public void NewRunResetsOnlyPlanFailuresAlongWithThePlanLists()
    {
        var state = CodingWorkingState.Create("Original task");
        foreach (var tool in new[] { "coding.updatePlan", "coding.read" })
            state = CodingWorkingStateReducer.ObserveToolResult(state,
                new LmToolCall(tool, tool, Args(new { })), "{\"success\":false,\"errorCode\":\"test.failure\"}");
        var checkpoint = new AgentRunCheckpoint([new LmChatMessage("user", "Original task")], 1, 1, 0, 0, WorkingState: state);
        var continued = CodingSessionContext.ContinueWorkingState(checkpoint, "Continue")!;
        Assert.Equal("coding.read", Assert.Single(continued.Failures).Tool);
        Assert.Equal(state.Evidence, continued.Evidence);
        var messages = CodingSessionContext.Continue(checkpoint, [new("system", "Policy"), new("user", "Continue")]);
        Assert.Contains(messages, message => message.Content is { } content
            && content.Contains("pro übermitteltem Eintrag id und title", StringComparison.Ordinal)
            && content.Contains("pending", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Content?.Contains("genügen Updates per id", StringComparison.Ordinal) == true);
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}
