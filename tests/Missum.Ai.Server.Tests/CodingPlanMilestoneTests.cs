using System.Text.Json;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;

namespace Missum.Ai.Server.Tests;

public sealed class CodingPlanMilestoneTests
{
    private static readonly string[] VerificationArguments = ["verify.py"];
    [Fact]
    public void SeveralStagesCanBeCompletedInOneAtomicUpdate()
    {
        var before = InitialState();
        var saved = JsonSerializer.Serialize(before);
        var candidate = Apply(before, """
            {"steps":[
              {"id":"inspect","status":"completed"},
              {"id":"change","status":"completed"},
              {"id":"review","status":"in_progress"}],"phase":"review"}
            """);

        Assert.Equal(2, candidate.Plan.Count(step => step.Status == "completed"));
        Assert.Equal("in_progress", candidate.Plan[2].Status);
        Assert.Equal(saved, JsonSerializer.Serialize(before));
        Assert.Equal("in_progress", before.Plan[0].Status);
        Assert.Equal("pending", before.Plan[1].Status);
    }

    [Fact]
    public void OneCompletedStageAndItsNextActiveStageCanBePersistedTogether()
    {
        var before = InitialState();
        var after = Apply(before, """
            {"steps":[
              {"id":"inspect","status":"completed"},
              {"id":"change","status":"in_progress"}],"phase":"editing"}
            """);

        Assert.Equal("completed", after.Plan[0].Status);
        Assert.Equal("in_progress", after.Plan[1].Status);
        Assert.Equal("pending", after.Plan[2].Status);
    }

    [Fact]
    public void FullListUpdatesRetainEarlierCompletionsWhileCompletingOneFurtherStage()
    {
        var before = CompleteFirstStage();
        var after = Apply(before, """
            {"steps":[
              {"id":"inspect","title":"Ursache prüfen","status":"completed"},
              {"id":"change","title":"Korrektur umsetzen","status":"completed"},
              {"id":"review","title":"Ergebnis prüfen","status":"in_progress"}]}
            """);

        Assert.Equal(2, after.Plan.Count(step => step.Status == "completed"));
        Assert.Equal(before.Plan[0].Title, after.Plan[0].Title);
        Assert.Equal("in_progress", after.Plan[2].Status);
    }

    [Fact]
    public void UnchangedFullPlanConfirmationDoesNotCountEarlierCompletionsAgain()
    {
        var before = CompleteFirstStage();
        var after = Apply(before, JsonSerializer.Serialize(new
        {
            steps = before.Plan.Select(step => new { id = step.Id, title = step.Title, status = step.Status }),
        }));

        Assert.Equal(before.Plan, after.Plan);
        Assert.Equal(1, after.ConsecutivePlanNoOps);
    }

    [Fact]
    public void OneStageMaySatisfySeveralAcceptanceCriteriaAtTheSameMilestone()
    {
        var before = InitialState();
        var after = Apply(before, """
            {"steps":[
              {"id":"inspect","status":"completed"},
              {"id":"change","status":"in_progress"}],
             "acceptanceCriteria":[
              {"id":"correct","status":"completed"},
              {"id":"checked","status":"completed"}]}
            """);

        Assert.All(after.AcceptanceCriteria, criterion => Assert.Equal("completed", criterion.Status));
        Assert.Single(after.Plan, step => step.Status == "completed");
    }

    [Fact]
    public void AReopenedStageIsCompletedAgainAfterTheCheckRunsOnceMore()
    {
        var completed = CompleteFirstStage();
        var reopened = Apply(completed, """
            {"steps":[{"id":"inspect","status":"in_progress"},{"id":"change","status":"pending"}],
             "explanation":"Eine neue Beobachtung erfordert eine erneute Ursachenprüfung."}
            """);
        reopened = ObserveSuccessfulCommand(reopened, "rechecked-result");
        var checkedAgain = Apply(reopened, """
            {"steps":[
              {"id":"inspect","status":"completed"},
              {"id":"change","status":"in_progress"}]}
            """);

        Assert.Equal("completed", checkedAgain.Plan[0].Status);
        Assert.Equal("in_progress", checkedAgain.Plan[1].Status);
        Assert.Contains(checkedAgain.Evidence, item => item.Id == "rechecked-result");
    }

    [Fact]
    public void NewRunGuidanceAppendsWithoutRewritingTheEvaluatedPromptPrefix()
    {
        var prefix = new LmChatMessage("system", "Bereits ausgewertete native Modellanweisungen.");
        var task = new LmChatMessage("user", "Setze die Reparatur fort.");
        var history = new LmChatMessage("assistant", "Die Ursache ist eingegrenzt.");
        var messages = new List<LmChatMessage> { prefix, task, history };

        CodingPlanMilestoneGuard.EnsureInstructions(messages);
        CodingPlanMilestoneGuard.EnsureInstructions(messages);

        Assert.Equal(4, messages.Count);
        Assert.Same(prefix, messages[0]);
        Assert.Same(task, messages[1]);
        Assert.Same(history, messages[2]);
        Assert.Equal("system", messages[3].Role);
        Assert.Equal(Instruction, messages[3].Content);
    }

    [Fact]
    public void APreviouslyFoldedNativeInstructionDoesNotAppendDuplicateGuidance()
    {
        var prefix = new LmChatMessage("system", "Stabiles natives Systempräfix.");
        var folded = new LmChatMessage("user", "Missum-Laufanweisung:\n" + Instruction);
        var messages = new List<LmChatMessage> { prefix, new("user", "Bearbeite die nächste Etappe."), folded };

        CodingPlanMilestoneGuard.EnsureInstructions(messages);

        Assert.Equal(3, messages.Count);
        Assert.Same(prefix, messages[0]);
        Assert.Same(folded, messages[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactionPreservesTheLatestMilestoneGuidanceOutsideTheInitialPrefix(bool folded)
    {
        var prefix = new LmChatMessage("system", CompactAgentContextPolicy.Marker + "\nUnveränderte native Systemanweisung.");
        var task = new LmChatMessage("user", "Korrigiere den offenen Fehler und prüfe das Ergebnis.");
        var oldInstruction = new LmChatMessage("system", Instruction);
        var latestInstruction = new LmChatMessage(folded ? "user" : "system",
            (folded ? "Missum-Laufanweisung:\n" : "") + Instruction);
        LmChatMessage[] messages = [prefix, new("user", "Vorheriger Auftrag"), oldInstruction,
            new("assistant", new string('x', 12_000)), latestInstruction, task,
            new("assistant", "Die erste Etappe ist bereits geprüft.")];

        var plan = CodingContextCompactor.Plan(messages, 32_768, force: true);

        Assert.NotNull(plan);
        Assert.Same(prefix, Assert.Single(plan.SystemMessages));
        Assert.Same(task, plan.CurrentRequest);
        Assert.Contains(latestInstruction, plan.PreservedContextMessages!);
        Assert.DoesNotContain(plan.PreservedContextMessages!, message => ReferenceEquals(message, oldInstruction));
        Assert.DoesNotContain(CodingPlanMilestoneGuard.Marker, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
        var compacted = CodingContextCompactor.Complete(plan, "Die Ursachenprüfung ist abgeschlossen; die Korrektur steht aus.");
        Assert.Same(prefix, compacted[0]);
        Assert.Single(compacted, message => message.Content?.Contains(CodingPlanMilestoneGuard.Marker, StringComparison.Ordinal) == true);
        Assert.Contains(task, compacted);
        var continued = compacted.ToList();
        CodingPlanMilestoneGuard.EnsureInstructions(continued);
        Assert.Equal(compacted.Length, continued.Count);
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(compacted);
        Assert.Equal(prefix.Content, Assert.Single(native, message => message.Role == "system").Content);
        Assert.Contains(native, message => message.Role == "user" && message.Content == "Missum-Laufanweisung:\n" + Instruction);
    }

    private static string Instruction => CodingPlanMilestoneGuard.Marker + CodingPlanMilestoneGuard.Instructions;

    private static CodingWorkingState InitialState()
    {
        var state = Apply(CodingWorkingState.Create("Prüfe die Ursache, korrigiere sie und verifiziere das Verhalten."), """
            {"steps":[
              {"id":"inspect","title":"Ursache prüfen","status":"in_progress"},
              {"id":"change","title":"Korrektur umsetzen"},
              {"id":"review","title":"Ergebnis prüfen"}],
             "acceptanceCriteria":[{"id":"correct","title":"Ursache belegt"},{"id":"checked","title":"Ergebnis nachgerechnet"}]}
            """);
        return ObserveSuccessfulCommand(state, "verified-result");
    }

    private static CodingWorkingState CompleteFirstStage() => Apply(InitialState(), """
        {"steps":[
          {"id":"inspect","status":"completed"},
          {"id":"change","status":"in_progress"}]}
        """);

    private static CodingWorkingState ObserveSuccessfulCommand(CodingWorkingState state, string id) =>
        CodingWorkingStateReducer.ObserveToolResult(state,
            new(id, "coding.command", JsonSerializer.SerializeToElement(new { executable = "python", arguments = VerificationArguments })),
            """{"status":"completed","result":{"success":true,"exitCode":0,"stdout":"Independent comparison passed.","stderr":""}}""");

    private static CodingWorkingState Apply(CodingWorkingState state, string arguments) =>
        CodingWorkingStateReducer.ApplyPlanUpdate(state, JsonSerializer.Deserialize<JsonElement>(arguments));
}
