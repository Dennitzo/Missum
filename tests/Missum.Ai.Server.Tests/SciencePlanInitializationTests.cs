using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class SciencePlanInitializationTests
{
    private static readonly string[] IncompleteStageIds = ["q1", "q2", "q3", "q6"];
    private static CodingWorkingState Start() => CodingWorkingState.Create("Prüfe den vollständigen Forschungsauftrag.")
        with { Science = new(ScienceSkill.Profile, []) };
    private static JsonElement J(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static CodingWorkingState Apply(CodingWorkingState state, string value) =>
        CodingWorkingStateReducer.ApplyPlanUpdate(state, J(value));
    [Fact]
    public void PartialInitialScienceMetadataNeverDiscardsTheRestOfThePlan()
    {
        // Shape observed in De-Broglie-Bohm: six steps, a missing question and
        // omitted scienceStages for q2/q3/q6. Preserve the plan without inventing
        // any omitted question, strategy, requirement or verification.
        const string input = """
            {"steps":[
              {"id":"q1","title":"Publikation prüfen","status":"in_progress"},
              {"id":"q2","title":"Annahmen abgrenzen"},{"id":"q3","title":"Herleitung überprüfen"},
              {"id":"q4","title":"Numerisch gegenprüfen"},{"id":"q5","title":"Ergebnisse einarbeiten"},
              {"id":"q6","title":"Offene Grenzen dokumentieren"}],
             "scienceStages":[{"id":"q1","strategy":"Gezielte Lektüre"},
              {"id":"q4","question":"Stimmen Grenzfälle überein?","strategy":"Unabhängige Rechnung"},
              {"id":"q5","question":"Ist der Text nachvollziehbar?","strategy":"Publikation lesen"}]}
            """;
        var state = Apply(Start(), input);
        Assert.Equal(6, state.Plan.Count);
        Assert.Equal(6, state.Science!.Stages.Count);
        var incomplete = state.Science.Stages.Where(stage => stage.DetailsPending).OrderBy(stage => stage.Id).ToArray();
        Assert.Equal(IncompleteStageIds, incomplete.Select(stage => stage.Id));
        Assert.Empty(incomplete[0].Question);
        Assert.Equal("Gezielte Lektüre", incomplete[0].Strategy);
        Assert.All(incomplete.Skip(1), stage => { Assert.Empty(stage.Question); Assert.Empty(stage.Strategy); Assert.Empty(stage.Checks); });
        Assert.All(state.Plan.Skip(1), step => Assert.Equal("pending", step.Status));
        var receipt = CodingWorkingStateTools.CreatePlanReceipt(state, J(input));
        Assert.True(receipt.GetProperty("success").GetBoolean());
        Assert.Equal(6, receipt.GetProperty("storedSteps").GetArrayLength());
        Assert.Equal(4, receipt.GetProperty("pendingScienceDetails").GetArrayLength());
        Assert.Contains("noch offen", receipt.GetProperty("message").GetString());
    }

    [Fact]
    public void APlainPlanCanBeStoredButCannotClaimScientificCompletionWithoutExplicitDetails()
    {
        var state = Apply(Start(), """{"steps":[{"id":"s","title":"Aussage prüfen"}],"acceptanceCriteria":[{"id":"a","title":"Gültigkeitsbereich dokumentiert"}]}""");
        Assert.Equal("pending", Assert.Single(state.Plan).Status);
        Assert.Equal("pending", Assert.Single(state.AcceptanceCriteria).Status);
        var stage = Assert.Single(state.Science!.Stages);
        Assert.True(stage.DetailsPending);
        Assert.Empty(stage.Question);
        Assert.Empty(stage.Strategy);
        var error = Assert.ThrowsAny<ArgumentException>(() => Apply(state, """
            {"steps":[{"id":"s","status":"completed"}],
             "scienceStages":[{"id":"s","outcome":"clarified"}]}
            """));
        Assert.Contains("question oder strategy", error.Message);
        Assert.True(state.Science!.Stages[0].DetailsPending);
        Assert.Equal("pending", state.Plan[0].Status);
    }

    [Fact]
    public void ExplicitDetailsMayBeCompletedIncrementallyWithoutLosingDeclaredRequiredChecks()
    {
        var state = Apply(Start(), """
            {"steps":[{"id":"s","title":"Grenzfall prüfen","status":"in_progress"}],
             "scienceStages":[{"id":"s","strategy":"Numerische Gegenrechnung", "checks":[{"id":"n","kind":"numeric","required":true}]}]}
            """);
        Assert.True(state.Science!.Stages[0].DetailsPending);
        state = Apply(state, """{"scienceStages":[{"id":"s","question":"Gilt das Ergebnis im Grenzfall?"}]}""");
        Assert.False(state.Science!.Stages[0].DetailsPending);
        Assert.True(Assert.Single(state.Science.Stages[0].Checks).Required);
        Assert.ThrowsAny<ArgumentException>(() => Apply(state, """{"scienceStages":[{"id":"s","checks":[{"id":"n","kind":"numeric","required":false}]}]}"""));
        state = Apply(state, """
            {"steps":[{"id":"s","status":"completed"}],
             "scienceStages":[{"id":"s","outcome":"clarified","checks":[{"id":"n","kind":"numeric","required":true}]}]}
            """);
        Assert.Equal("completed", state.Plan[0].Status);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(state));
    }

    [Fact]
    public void ExplicitInvalidDetailsAreStillRejectedAtomicallyAndThePendingMarkerCannotBeForged()
    {
        var state = Apply(Start(), """{"steps":[{"id":"s","title":"Untersuchen"}]}""");
        Assert.ThrowsAny<ArgumentException>(() => Apply(state, """{"scienceStages":[{"id":"s","question":null}]}"""));
        Assert.ThrowsAny<ArgumentException>(() => Apply(state, """{"scienceStages":[{"id":"s","detailsPending":false}]}"""));
        Assert.Empty(state.Science!.Stages[0].Question);
        Assert.True(state.Science.Stages[0].DetailsPending);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("analytic")]
    [InlineData("lean")]
    [InlineData("numeric")]
    [InlineData("plot")]
    [InlineData("publication")]
    public void DeclaredChecksDoNotRequireProofIdentifiersForPlanUpdates(string kind)
    {
        var state = Configure(kind);
        var completed = Apply(state, """
            {"steps":[{"id":"s","status":"completed"}],"scienceStages":[{"id":"s","outcome":"clarified"}]}
            """);
        Assert.Equal("completed", completed.Plan[0].Status);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(completed));
        Assert.Empty(completed.Evidence);
    }

    [Fact]
    public void RemovedCheckEvidenceParameterIsNotAcceptedAsFallback()
    {
        var state = Configure("publication");
        Assert.ThrowsAny<ArgumentException>(() => Apply(state, """
            {"scienceStages":[{"id":"s","checks":[{"id":"check","kind":"publication","required":true,"evidenceIds":[]}]}]}
            """));
        Assert.Equal("in_progress", state.Plan[0].Status);
    }

    [Theory]
    [InlineData("{\"success\":true,\"verificationStatus\":\"ProcessFailed\"}")]
    [InlineData("{\"success\":true,\"verificationStatus\":\"ProcessSucceeded\",\"runs\":[{\"success\":true,\"exitCode\":2}]}")]
    [InlineData("{\"success\":true,\"verificationStatus\":\"ProcessSucceeded\",\"runs\":[{\"success\":false,\"exitCode\":0}]}")]
    public void ContradictoryMathManifestStillRecordsTheActualProcessFailure(string manifest)
    {
        var state = CodingWorkingStateReducer.ObserveToolResult(Configure("numeric"), new("actual-result", "math.numeric", J("{}")), manifest);
        Assert.False(Assert.Single(state.Evidence).Success);
        Assert.Equal("error_recovery", state.Phase);
        Assert.Equal("math.numeric", Assert.Single(state.Failures).Tool);
        Assert.Equal("in_progress", state.Plan[0].Status);
    }

    [Fact]
    public void CompletionReportsAllMissingScientificFieldsTogetherWithoutSavingTheRejectedDraft()
    {
        var state = Apply(Start(), """
            {"steps":[{"id":"s","title":"Phase 1: Prüfen","status":"in_progress"}]}
            """);
        state = CodingWorkingStateReducer.ObserveToolResult(state, new("read", "coding.read", J("""{"path":"Publikation.md"}""")),
            """{"success":true,"path":"Publikation.md","content":"Originaltext"}""");
        var error = Assert.Throws<PlanValidationException>(() => Apply(state, """
            {"steps":[{"id":"s","status":"completed"}]}
            """));
        Assert.Equal(3, error.Issues.Count);
        Assert.Contains(error.Issues, issue => issue.Field == "scienceStages.question");
        Assert.Contains(error.Issues, issue => issue.Field == "scienceStages.strategy");
        Assert.Contains(error.Issues, issue => issue.Field == "scienceStages.outcome");
        Assert.Equal("in_progress", state.Plan[0].Status);
        Assert.Empty(state.Science!.Stages[0].Question);
    }
    [Fact]
    public void PendingDetailsSurviveCheckpointAndContinuationWhileLegacyConfiguredStagesRemainCompatible()
    {
        var state = Apply(Start(), """{"steps":[{"id":"s","title":"Prüfen"}]}""");
        var checkpoint = new AgentRunCheckpoint([], 0, 0, 0, 0, WorkingState: state);
        var restored = JsonSerializer.Deserialize<AgentRunCheckpoint>(JsonSerializer.Serialize(checkpoint, MissumAiProtocol.CreateJsonOptions()), MissumAiProtocol.CreateJsonOptions())!;
        Assert.True(CodingSessionContext.ContinueWorkingState(restored, "Fortsetzen", science: true)!.Science!.Stages[0].DetailsPending);
        const string legacy = """{"id":"old","question":"Frage","strategy":"Strategie","dependsOn":[],"checks":[],"openQuestions":[],"blockers":[],"outcome":"open"}""";
        Assert.False(JsonSerializer.Deserialize<ScienceStage>(legacy, MissumAiProtocol.CreateJsonOptions())!.DetailsPending);
    }

    private static CodingWorkingState Configure(string kind) => Apply(Start(), JsonSerializer.Serialize(new
    {
        steps = new[] { new { id = "s", title = "Prüfen", status = "in_progress" } },
        scienceStages = new[] { new { id = "s", question = "Gilt die Aussage?", strategy = "Unabhängig prüfen", checks = new[] { new { id = "check", kind, required = true } } } },
    }));
}
