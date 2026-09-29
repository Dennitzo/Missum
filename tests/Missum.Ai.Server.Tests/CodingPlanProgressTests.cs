using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CodingPlanProgressTests
{
    private static readonly string[] CommandArguments = ["-m", "unittest", "-v"];

    [Fact]
    public void EquivalentPlanUpdatesDoNotAdvanceWorkingSequenceOrInventEvidence()
    {
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Run tests"), Args(new
        {
            steps = new[] { new { id = "run-tests", title = "Run authorized tests", status = "in_progress" } }, phase = "review",
        }));
        var sequence = state.Sequence;
        for (var count = 1; count <= 3; count++)
        {
            state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
            {
                phase = "review", steps = new[] { new { evidenceIds = Array.Empty<string>(), status = "in_progress", id = "run-tests" } },
                explanation = "This is another announcement, not execution.",
            }));
            Assert.Equal(sequence, state.Sequence);
            Assert.Equal(count, state.ConsecutivePlanNoOps);
            Assert.Empty(state.Tests);
            Assert.Empty(state.Evidence);
        }
        var restored = JsonSerializer.Deserialize<CodingWorkingState>(JsonSerializer.Serialize(state))!;
        Assert.Equal(3, restored.ConsecutivePlanNoOps);
        Assert.Equal(sequence, restored.Sequence);
    }

    [Fact]
    public void NewPlanInformationResetsNoOpRecovery()
    {
        var state = CodingWorkingState.Create("Run tests") with { ConsecutivePlanNoOps = 3 };
        var updated = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new { nextStep = "Run the authorized unit tests", phase = "review" }));
        Assert.Equal(0, updated.ConsecutivePlanNoOps);
        Assert.Equal(state.Sequence + 1, updated.Sequence);
        CodingPlanProgressGuard.ThrowIfStalled(updated);
    }

    [Fact]
    public void RepeatedRealCommandsRemainExecutableAndResetAdministrativeRecovery()
    {
        var state = CodingWorkingState.Create("Repeat the verification when required") with { ConsecutivePlanNoOps = 2 };
        var arguments = Args(new { executable = "python", arguments = CommandArguments });
        for (var index = 1; index <= 2; index++)
        {
            var call = new LmToolCall("test-" + index, ClientToolNames.CodingCommand, arguments);
            CodingLoopGuard.ThrowIfRepeatedFailure([], call, state);
            state = CodingWorkingStateReducer.ObserveToolResult(state, call,
                "{\"success\":true,\"exitCode\":0,\"stdout\":\"3 tests passed\"}");
            Assert.Equal(0, state.ConsecutivePlanNoOps);
        }
        Assert.Equal(2, state.Tests.Count);
        Assert.NotEqual(state.Tests[0].Id, state.Tests[1].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewRecoveryPreservesExplicitProcessCapabilities(bool processAllowed)
    {
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding, [new("user", [new("text", "Run tests")])],
            ClientCapabilities: processAllowed ? ["coding", "coding.process"] : ["coding"]);
        var available = new AgentToolCatalog().GetAvailableTools(request);
        var state = CodingWorkingState.Create("Run tests") with { Phase = "review", ConsecutivePlanNoOps = 2 };
        var offered = CodingPlanProgressGuard.OfferedTools(available, state);
        Assert.Equal(processAllowed, offered.Any(tool => tool.Name == ClientToolNames.CodingCommand));
        Assert.DoesNotContain(offered, tool => tool.Name == CodingWorkingStateTools.PlanTool);
        Assert.Equal(available.Count - 1, offered.Length);
        var receipt = CodingPlanProgressGuard.NoOpReceipt(state, available);
        Assert.False(receipt.Succeeded);
        Assert.False(receipt.Result.GetProperty("changed").GetBoolean());
        Assert.Contains(processAllowed ? "tatsächlichen Exitcode" : "nicht freigegeben", receipt.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenPlanTransportNameIsRecoveredOnlyWhileNoOpRecoveryIsActive()
    {
        var arguments = Args(new { phase = "review" });
        var planTransport = ModelRuntimeClient.ToTransportToolName(CodingWorkingStateTools.PlanTool);
        var repeatedPlan = new LmToolCall("repeat-plan", planTransport, arguments);
        var activeRecovery = CodingWorkingState.Create("Run tests") with { ConsecutivePlanNoOps = 2 };

        Assert.Equal(CodingWorkingStateTools.PlanTool,
            CodingPlanProgressGuard.RestoreHiddenPlanCall(repeatedPlan, activeRecovery).Name);
        Assert.Equal(planTransport,
            CodingPlanProgressGuard.RestoreHiddenPlanCall(repeatedPlan,
                activeRecovery with { ConsecutivePlanNoOps = 1 }).Name);

        var unrelatedTransport = ModelRuntimeClient.ToTransportToolName(ClientToolNames.CodingCommand);
        Assert.Equal(unrelatedTransport,
            CodingPlanProgressGuard.RestoreHiddenPlanCall(
                new LmToolCall("unoffered-command", unrelatedTransport, arguments), activeRecovery).Name);
    }

    [Fact]
    public void FollowUpStartsFreshRecoveryWhilePreservingPriorEvidence()
    {
        var previous = CodingWorkingState.Create("Old task") with { ConsecutivePlanNoOps = 4 };
        var checkpoint = new AgentRunCheckpoint([], 0, 0, 0, 0, WorkingState: previous);
        var next = CodingSessionContext.ContinueWorkingState(checkpoint, "New user request");
        Assert.NotNull(next);
        Assert.Equal(0, next.ConsecutivePlanNoOps);
        Assert.Equal("New user request", next.OriginalTask);
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}
