using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class SciencePlanInitializationTests
{
    private static readonly string[] IncompleteStageIds = ["q1", "q2", "q3", "q6"];
    private static readonly string[] ActualResultEvidence = ["actual-result"];
    private static CodingWorkingState Start() => CodingWorkingState.Create("Prüfe den vollständigen Forschungsauftrag.")
        with { Science = new(ScienceSkill.Profile, []) };
    private static JsonElement J(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static CodingWorkingState Apply(CodingWorkingState state, string value) =>
        CodingWorkingStateReducer.ApplyPlanUpdate(state, J(value));
    private static CodingWorkingState Observe(CodingWorkingState state, string tool, bool success = true) =>
        CodingWorkingStateReducer.ObserveToolResult(state, new("actual-result", tool, J("{}")),
            JsonSerializer.Serialize(new { status = "completed", result = new { success, verificationStatus = success ? "ProcessSucceeded" : "ProcessFailed",
                runs = new[] { new { success, exitCode = success ? 0 : 2, stdout = "Tatsächlich berechnetes Ergebnis", stderr = "" } } } }));

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
        state = Observe(state, "math.numeric");
        var error = Assert.Throws<ArgumentException>(() => Apply(state, """
            {"steps":[{"id":"s","status":"completed","evidenceIds":["actual-result"]}],
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
        Assert.Throws<ArgumentException>(() => Apply(state, """{"scienceStages":[{"id":"s","checks":[{"id":"n","kind":"numeric","required":false}]}]}"""));
        state = Observe(state, "math.numeric");
        state = Apply(state, """
            {"steps":[{"id":"s","status":"completed","evidenceIds":["actual-result"]}],
             "scienceStages":[{"id":"s","outcome":"clarified","checks":[{"id":"n","kind":"numeric","required":true,"evidenceIds":["actual-result"]}]}]}
            """);
        Assert.Equal("completed", state.Plan[0].Status);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(state));
    }

    [Fact]
    public void ExplicitInvalidDetailsAreStillRejectedAtomicallyAndThePendingMarkerCannotBeForged()
    {
        var state = Apply(Start(), """{"steps":[{"id":"s","title":"Untersuchen"}]}""");
        Assert.Throws<ArgumentException>(() => Apply(state, """{"scienceStages":[{"id":"s","question":null}]}"""));
        Assert.Throws<ArgumentException>(() => Apply(state, """{"scienceStages":[{"id":"s","detailsPending":false}]}"""));
        Assert.Empty(state.Science!.Stages[0].Question);
        Assert.True(state.Science.Stages[0].DetailsPending);
    }

    [Theory]
    [InlineData("math.numeric", "numeric")]
    [InlineData("math.evaluate", "numeric")]
    [InlineData("math.symbolic", "analytic")]
    [InlineData("math.smt", "analytic")]
    [InlineData("math.evaluate", "analytic")]
    public void SuccessfulMathToolContractsCanBeBoundToTheirCorrespondingCheckKinds(string tool, string kind)
    {
        var state = Configure(kind);
        state = Observe(state, tool);
        var completed = Apply(state, Completion(kind));
        Assert.Equal("completed", completed.Plan[0].Status);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(completed));
        var failed = Observe(Configure(kind), tool, success: false);
        Assert.Throws<ArgumentException>(() => Apply(failed, Completion(kind)));
    }

    [Theory]
    [InlineData("math.numeric", "analytic")]
    [InlineData("math.symbolic", "numeric")]
    [InlineData("coding.edit", "analytic")]
    [InlineData("coding.write", "numeric")]
    [InlineData("math.numeric", "lean")]
    [InlineData("math.symbolic", "lean")]
    public void MatchingARealMathToolDoesNotWeakenOtherEvidenceKinds(string tool, string kind)
    {
        var state = Observe(Configure(kind), tool);
        Assert.Throws<ArgumentException>(() => Apply(state, Completion(kind)));
    }

    [Theory]
    [InlineData("{\"success\":true,\"verificationStatus\":\"ProcessFailed\"}")]
    [InlineData("{\"success\":true,\"verificationStatus\":\"ProcessSucceeded\",\"runs\":[{\"success\":true,\"exitCode\":2}]}")]
    [InlineData("{\"success\":true,\"verificationStatus\":\"ProcessSucceeded\",\"runs\":[{\"success\":false,\"exitCode\":0}]}")]
    public void ContradictoryMathManifestCannotOverrideAFailedProcess(string manifest)
    {
        var state = CodingWorkingStateReducer.ObserveToolResult(Configure("numeric"), new("actual-result", "math.numeric", J("{}")), manifest);
        Assert.False(Assert.Single(state.Evidence).Success);
        Assert.Equal("error_recovery", state.Phase);
        Assert.Equal("math.numeric", Assert.Single(state.Failures).Tool);
        Assert.Throws<ArgumentException>(() => Apply(state, Completion("numeric")));
    }

    [Fact]
    public void ASuccessfulPdfProcessIsNotSilentlyPromotedToAContentReview()
    {
        var state = Configure("publication");
        state = CodingWorkingStateReducer.ObserveToolResult(state, new("actual-result", "coding.command", J("{}")), "{\"success\":true,\"exitCode\":0,\"stdout\":\"PDF created\"}");
        var error = Assert.Throws<ArgumentException>(() => Apply(state, Completion("publication")));
        Assert.Contains("PDF-Befehl", error.Message);
        Assert.Contains("Publikationsinhalt", error.Message);
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
    private static string Completion(string kind) => JsonSerializer.Serialize(new
    {
        steps = new[] { new { id = "s", status = "completed", evidenceIds = ActualResultEvidence } },
        scienceStages = new[] { new { id = "s", outcome = "clarified", checks = new[] { new { id = "check", kind, required = true, evidenceIds = ActualResultEvidence } } } },
    });
}
