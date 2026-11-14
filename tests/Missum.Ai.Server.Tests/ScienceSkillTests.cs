using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceSkillTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static CodingWorkingState Start() => CodingWorkingState.Create("Prüfe die Aussage") with { Science = new(ScienceSkill.Profile, []) };
    private static RunRequest Request(string? workflow = ScienceSkill.Profile) => new(MissumAiProtocol.Version, RunMode.Coding,
        [new("user", [new("text", "Untersuche zwei Fragen.")])], ClientCapabilities: ["coding", "coding.process", "math.lean"],
        CodingOptions: new(WorkspacePath: "C:/science"), WorkflowProfile: workflow);
    private static CodingWorkingState Plan(CodingWorkingState state, string kind = "analytic") => CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
        {"steps":[{"id":"a","title":"A prüfen","status":"in_progress"}],"scienceStages":[{"id":"a","question":"Gilt A?","strategy":"Unabhängig gegenprüfen","checks":[{"id":"check","kind":"KIND","required":true}]}]}
        """.Replace("KIND", kind, StringComparison.Ordinal)));
    private static CodingWorkingState Observe(CodingWorkingState state, string id, string tool, string arguments, string result) =>
        CodingWorkingStateReducer.ObserveToolResult(state, new(id, tool, J(arguments)), result);
    private static CodingWorkingState Complete(CodingWorkingState state) =>
        CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"steps":[{"id":"a","title":"A prüfen","status":"completed"}],"scienceStages":[{"id":"a","outcome":"clarified"}]}
            """));

    [Theory]
    [InlineData(null)]
    [InlineData("compact-v1")]
    public void SkillResourcesAndScienceSchemaOnlyAppearInExplicitScienceProfile(string? contextProfile)
    {
        var request = Request() with { ContextProfileVersion = contextProfile };
        RunRequestValidator.Validate(request);
        var messages = RunProcessor.CreateInitialMessages(request, "coding", [], contextProfile);
        Assert.Contains(messages, message => message.Role == "system" && message.Content!.Contains(ScienceSkill.Marker, StringComparison.Ordinal));
        Assert.Contains("Hoher Aufwand", ScienceSkill.Instructions);
        var catalog = new AgentToolCatalog();
        Assert.True(catalog.GetAvailableTools(request).Single(tool => tool.Name == "coding.updatePlan").Schema.GetProperty("properties").TryGetProperty("scienceStages", out _));
        Assert.False(catalog.GetAvailableTools(Request(null)).Single(tool => tool.Name == "coding.updatePlan").Schema.GetProperty("properties").TryGetProperty("scienceStages", out _));
        Assert.DoesNotContain(RunProcessor.CreateInitialMessages(Request(null), "coding", [], contextProfile), message => message.Content!.Contains(ScienceSkill.Marker, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request with { Mode = RunMode.General }));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request with { WorkflowProfile = "unknown" }));
        var ordinaryState = CodingWorkingState.Create("Normaler Auftrag");
        Assert.False(JsonSerializer.SerializeToElement(ordinaryState).TryGetProperty("Science", out _));
        var ordinaryReceipt = new CodingToolEvidence("read", "coding.read", true, "a.txt", null, "Text", 1);
        var ordinaryJson = JsonSerializer.SerializeToElement(ordinaryReceipt);
        Assert.False(ordinaryJson.TryGetProperty("FileHashes", out _));
        Assert.False(ordinaryJson.TryGetProperty("FormalVerified", out _));
        Assert.False(ordinaryJson.TryGetProperty("VerificationPath", out _));
    }

    [Fact]
    public void PublicationBuildPolicyIsPresentInitiallyAndUpgradesStoredScienceInstructions()
    {
        var initial = RunProcessor.CreateInitialMessages(Request(), "coding", []);
        var skill = Assert.Single(initial, message => message.Role == "system"
            && message.Content?.StartsWith(ScienceSkill.Marker, StringComparison.Ordinal) == true);
        Assert.Contains("Nach jeder Phase", skill.Content);
        Assert.Contains("Publikation-erzeugen.ps1 ohne", skill.Content);
        Assert.Contains("wiederhole den Bau", skill.Content);
        Assert.Contains("halte die Phase offen", skill.Content);

        // A continued run can start from a checkpoint created before this rule.
        var restored = CodingPlanFormat.UpgradeMessages(
            [new LmChatMessage("system", ScienceSkill.Marker + "Alter Science-Skill")], [], out var changed);
        Assert.True(changed);
        Assert.Equal(ScienceSkill.Instructions, Assert.Single(restored).Content);
        Assert.Contains("Publikation-erzeugen.ps1 ohne", restored[0].Content);
    }

    [Fact]
    public void ExistingVersionTwoScienceCheckpointGetsPolicyWithoutChangingUserOrToolContent()
    {
        var user = new LmChatMessage("user", "Bitte das laufende Projekt fortsetzen.");
        var tool = new LmChatMessage("tool", "Originaler Fehlerbeleg", ToolCallId: "tool-1");
        var oldSkill = new LmChatMessage("system", ScienceSkill.Marker + "Bisherige Science-Anweisungen");
        var checkpoint = new AgentRunCheckpoint([oldSkill, user, tool], 3, 2, 100, 10,
            WorkingState: Start(), PreserveSessionPromptPrefix: true, WorkingStatePromptIncluded: true,
            ToolCatalogSignature: "old-cache", PlanFormatVersion: CodingPlanFormat.CurrentVersion);

        var upgraded = CodingPlanFormat.UpgradeCheckpoint(checkpoint);

        Assert.Equal(CodingPlanFormat.CurrentVersion, upgraded.PlanFormatVersion);
        Assert.Same(oldSkill, upgraded.Messages[0]);
        Assert.Same(user, upgraded.Messages[1]);
        Assert.Same(tool, upgraded.Messages[2]);
        Assert.StartsWith(ScienceSkill.PublicationPhaseMarker, upgraded.Messages[3].Content);
        Assert.Contains("Publikation-erzeugen.ps1 ohne", upgraded.Messages[3].Content);
        Assert.False(upgraded.PreserveSessionPromptPrefix);
        Assert.False(upgraded.WorkingStatePromptIncluded);
        Assert.Null(upgraded.ToolCatalogSignature);
        Assert.Same(upgraded, CodingPlanFormat.UpgradeCheckpoint(upgraded));

        var longContext = upgraded.Messages.Append(new LmChatMessage("assistant", new string('x', 100_000)))
            .Append(new LmChatMessage("user", "Setze die laufende Phase fort.")).ToArray();
        var plan = CodingContextCompactor.Plan(longContext, 32_768, upgraded.WorkingState);
        Assert.NotNull(plan);
        var compacted = CodingContextCompactor.Complete(plan, "Zusammengefasster Arbeitsstand.");
        Assert.Contains(compacted, message => message.Content?.StartsWith(ScienceSkill.PublicationPhaseMarker, StringComparison.Ordinal) == true);

        var normal = checkpoint with { Messages = [user, tool], WorkingState = CodingWorkingState.Create("Normaler Auftrag") };
        Assert.Same(normal, CodingPlanFormat.UpgradeCheckpoint(normal));
    }

    [Fact]
    public void LegacyHashBindingsAreDiscardedWithoutLosingPlanOrEvidence()
    {
        const string legacy = """
            {"originalTask":"Continue","plan":[],"acceptanceCriteria":[],"facts":[],"rejectedHypotheses":[],
             "evidence":[{"id":"read","tool":"coding.read","success":true,"path":"Publikation.md","sha256":"old",
                          "summary":"Read","sequence":1,"fileHashes":{"Publikation.md":"old"}}],
             "tests":[],"activeFiles":[],"failures":[],"nextStep":"Finish","phase":"review","environmentRevision":0,"sequence":1,
             "science":{"profile":"claude-science-v1","stages":[],"files":{"Publikation.md":{"sha256":"old","needsRead":true}}}}
            """;
        var state = JsonSerializer.Deserialize<CodingWorkingState>(legacy, MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal("read", Assert.Single(state.Evidence).Id);
        Assert.DoesNotContain("sha256", CodingEvidenceContext.Build(state).Content!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileHashes", JsonSerializer.Serialize(state), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("coding.write", "{\"path\":\"a.txt\",\"content\":\"new\"}")]
    [InlineData("coding.edit", "{\"path\":\"a.txt\",\"oldText\":\"old\",\"newText\":\"new\"}")]
    public void MutationSchemasExposeNoHashAndAcceptDirectEdits(string toolName, string arguments)
    {
        var tool = CodingToolCatalog.CreateTools().Single(item => item.Name == toolName);
        Assert.False(tool.Schema.GetProperty("properties").TryGetProperty("expectedSha256", out _));
        new AgentToolCatalog().Validate(tool, J(arguments));
    }

    [Theory]
    [InlineData("coding.write", "{\"path\":\"a.txt\",\"content\":\"new\",\"expectedSha256\":\"old\"}")]
    [InlineData("coding.edit", "{\"path\":\"a.txt\",\"oldText\":\"old\",\"newText\":\"new\",\"expectedSha256\":\"old\"}")]
    public void RemovedHashArgumentsAreNotAcceptedAsFallback(string name, string arguments)
    {
        var tool = CodingToolCatalog.CreateTools().Single(item => item.Name == name);
        Assert.Throws<ArgumentException>(() => new AgentToolCatalog().Validate(tool, J(arguments)));
    }

    [Fact]
    public void ModulesAndFocusAppendOnlyOnMeaningfulChangesAndAfterCompaction()
    {
        var state = Plan(Start());
        var messages = RunProcessor.CreateInitialMessages(Request(), "coding", []);
        var prefix = messages.Select(message => message.Content).ToArray();
        ScienceSkill.AppendModules(messages, state);
        var count = messages.Count;
        ScienceSkill.AppendModules(messages, state with { Sequence = 999 });
        Assert.Equal(count, messages.Count);
        Assert.Equal(prefix, messages.Take(prefix.Length).Select(message => message.Content));
        Assert.Single(messages, message => message.Content!.StartsWith("MISSUM_SCIENCE_SKILL_MODULE_V1:verification", StringComparison.Ordinal));
        messages.RemoveAll(message => message.Content!.StartsWith("MISSUM_SCIENCE_FOCUS_V1", StringComparison.Ordinal));
        ScienceSkill.AppendModules(messages, state);
        Assert.Contains(messages, message => message.Content!.StartsWith("MISSUM_SCIENCE_FOCUS_V1", StringComparison.Ordinal));
    }

    [Fact]
    public void RealCompactionPreservesSkillModulesLatestFocusAndStructuredStageMemory()
    {
        var state = Plan(Start());
        var messages = RunProcessor.CreateInitialMessages(Request(), "coding", []);
        ScienceSkill.AppendModules(messages, state);
        var oldFocus = messages.Single(message => message.Content!.StartsWith("MISSUM_SCIENCE_FOCUS_V1", StringComparison.Ordinal));
        messages.Add(new("assistant", new string('x', 100000)));
        state = state with { Science = state.Science! with { Stages = state.Science.Stages.Select(stage => stage with { Question = "Aktuelle Leitfrage nach Korrektur" }).ToArray() } };
        ScienceSkill.AppendModules(messages, state);
        messages.Add(new("user", "Setze die korrigierte Etappe fort."));
        var plan = CodingContextCompactor.Plan(messages, 32768, state);
        Assert.NotNull(plan);
        var compacted = CodingContextCompactor.Complete(plan, "Frühere Herleitung bleibt prüfbedürftig.");
        Assert.Contains(compacted, message => message.Content!.StartsWith(ScienceSkill.Marker, StringComparison.Ordinal));
        Assert.Contains(compacted, message => message.Content!.StartsWith("MISSUM_SCIENCE_SKILL_MODULE_V1:verification", StringComparison.Ordinal));
        Assert.DoesNotContain(compacted, message => ReferenceEquals(message, oldFocus));
        Assert.Contains(compacted, message => message.Content!.Contains("Aktuelle Leitfrage nach Korrektur", StringComparison.Ordinal));
        Assert.Contains("check", plan.WorkingStateMessage!.Content);
    }

    [Theory]
    [InlineData("analytic")]
    [InlineData("numeric")]
    [InlineData("lean")]
    [InlineData("source")]
    [InlineData("publication")]
    public void StageCompletionUsesStoredScientificStateWithoutReceiptBinding(string kind)
    {
        var state = Complete(Plan(Start(), kind));
        Assert.Equal("completed", Assert.Single(state.Plan).Status);
        Assert.Empty(state.Evidence);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(state));
        var json = JsonSerializer.SerializeToElement(state, MissumAiProtocol.CreateJsonOptions());
        Assert.False(json.GetProperty("plan")[0].TryGetProperty("evidenceIds", out _));
        Assert.False(json.GetProperty("science").GetProperty("stages")[0].GetProperty("checks")[0].TryGetProperty("evidenceIds", out _));
    }

    [Fact]
    public void ToolFailuresAndLaterFileChangesDoNotReopenAnExplicitlyCompletedPlan()
    {
        var state = Complete(Plan(Start(), "numeric"));
        state = Observe(state, "failed-command", "coding.command", "{}", "{\"success\":false,\"exitCode\":1}");
        Assert.False(Assert.Single(state.Evidence).Success);
        Assert.Equal("completed", state.Plan[0].Status);
        state = Observe(state, "later-edit", "coding.edit", "{\"path\":\"Publikation.md\"}", "{\"success\":true,\"path\":\"Publikation.md\"}");
        Assert.Equal("completed", state.Plan[0].Status);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(state));
    }

    [Theory]
    [InlineData("KernelAccepted", "passed", true)]
    [InlineData("KernelAccepted", "failed", false)]
    [InlineData("Rejected", "passed", false)]
    public void ActualLeanResultStillReportsKernelAndAxiomStatus(string verification, string audit, bool verified)
    {
        var state = Observe(Start(), "proof", "math.formalProof", "{}", JsonSerializer.Serialize(new
        {
            success = true, formalVerification = verification, axiomAudit = audit, source = "work/Proof.lean",
        }));
        var receipt = Assert.Single(state.Evidence);
        Assert.Equal(verified, receipt.FormalVerified);
        Assert.Equal(verified ? "work/Proof.lean" : null, receipt.VerificationPath);
    }

    [Fact]
    public void OpenStagesAllowIndependentWorkAndRequiredChecksKeepTheirDeclaredMeaning()
    {
        var state = Plan(Start(), "lean");
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"scienceStages":[{"id":"a","checks":[{"id":"check","kind":"lean","required":false}]}]}
            """)));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"steps":[{"id":"a","title":"A prüfen","status":"pending"},{"id":"b","title":"Unabhängig","status":"in_progress"}],
             "scienceStages":[{"id":"a","blockers":["Hilfslemma offen"]},{"id":"b","question":"Gilt B?","strategy":"Grenzfall untersuchen"}]}
            """));
        Assert.Equal("b", Assert.Single(state.Plan, step => step.Status == "in_progress").Id);
        Assert.Equal("open", ScienceSkill.ResultOutcome(state));
        Assert.Throws<PlanValidationException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"scienceStages":[{"id":"b","dependsOn":["a"]}]}
            """)));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData(".git/config")]
    [InlineData("C:/absolute.md")]
    public void CheckPathsRemainRelativeProjectReferences(string path)
    {
        var state = Plan(Start());
        var update = JsonSerializer.SerializeToElement(new
        {
            scienceStages = new[]
            {
                new { id = "a", checks = new[] { new { id = "check", kind = "analytic", required = true, paths = new[] { path } } } },
            },
        });
        Assert.Throws<PlanValidationException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, update));
        Assert.Empty(state.Science!.Stages[0].Checks[0].Paths);
    }

    [Fact]
    public void CyclicDependenciesAndDocumentedBlockersStillPreventCompletion()
    {
        var state = Plan(Start());
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"steps":[{"id":"b","title":"Folgerung"}],
             "scienceStages":[{"id":"a","dependsOn":["b"]},{"id":"b","dependsOn":["a"]}]}
            """)));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"scienceStages":[{"id":"a","blockers":["Ungeklärter Grenzfall"]}]}
            """));
        var error = Assert.Throws<PlanValidationException>(() => Complete(state));
        Assert.Contains(error.Issues, issue => issue.Field == "scienceStages.blockers");
        Assert.Equal("in_progress", state.Plan[0].Status);
    }

    [Fact]
    public void ExplicitReopenRequiresExplanationAndResetsOnlyDependentWork()
    {
        var state = Complete(Plan(Start()));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"steps":[{"id":"b","title":"Folgerung","status":"completed"},{"id":"c","title":"Unabhängig","status":"completed"}],
             "scienceStages":[{"id":"b","question":"Folgt B?","strategy":"Folgerung","dependsOn":["a"],"outcome":"clarified"},
                              {"id":"c","question":"Gilt C?","strategy":"Unabhängig","outcome":"clarified"}]}
            """));
        const string reopen = """{"steps":[{"id":"a","title":"A prüfen","status":"in_progress"}]}""";
        Assert.Throws<PlanValidationException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J(reopen)));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, J(reopen[..^1] + ",\"explanation\":\"Voraussetzung war unvollständig\"}"));
        Assert.Equal("in_progress", state.Plan[0].Status);
        Assert.Equal("pending", state.Plan[1].Status);
        Assert.Equal("completed", state.Plan[2].Status);
        Assert.Equal("open", state.Science!.Stages.Single(stage => stage.Id == "a").Outcome);
        Assert.Equal("open", state.Science.Stages.Single(stage => stage.Id == "b").Outcome);
        Assert.Equal("completed", Complete(state).Plan[0].Status);
    }

    [Fact]
    public void ScienceStateSurvivesCheckpointAndContinuationButNotOtherModesOrSessions()
    {
        var state = Plan(Start());
        var checkpoint = new AgentRunCheckpoint([new("system", "policy"), new("user", "task")], 1, 0, 0, 0, WorkingState: state);
        var json = JsonSerializer.Serialize(checkpoint, MissumAiProtocol.CreateJsonOptions());
        var restored = JsonSerializer.Deserialize<AgentRunCheckpoint>(json, MissumAiProtocol.CreateJsonOptions())!;
        Assert.Single(CodingSessionContext.ContinueWorkingState(restored, "Fortsetzen", science: true)!.Science!.Stages);
        Assert.Single(CodingSessionContext.ContinueWorkingState(restored, "Fortsetzen", science: true)!.Plan);
        Assert.Empty(CodingSessionContext.ContinueWorkingState(restored, "Codex")!.Plan);
        Assert.Null(CodingSessionContext.ContinueWorkingState(restored, "Codex")!.Science);
        Assert.Empty(Start().Science!.Stages);
    }

    [Fact]
    public void SettledPlanRemainsHistoricalAndNewTaskCanReuseStageIdsWithoutOldRequirements()
    {
        var state = Complete(Plan(Start()));
        var checkpoint = new AgentRunCheckpoint([new("system", "policy"), new("user", "old task")], 1, 0, 0, 0, WorkingState: state);
        var continued = CodingSessionContext.ContinueWorkingState(checkpoint, "Nächste unabhängige Aufgabe", science: true)!;
        Assert.Empty(continued.Plan);
        Assert.Empty(continued.Science!.Stages);
        Assert.Equal(state.Evidence, continued.Evidence);
        Assert.NotEmpty(checkpoint.WorkingState!.Plan);
        Assert.NotEmpty(Plan(continued, "numeric").Science!.Stages);
    }

    [Fact]
    public void DelegatedProofHistoryKeepsTrustedKernelResultIndependentlyOfThePlan()
    {
        var state = Plan(Start(), "lean");
        var result = JsonSerializer.SerializeToElement(new
        {
            runId = "child",
            completionEvidence = new LmChatMessage[]
            {
                new("assistant", ToolCalls: [new("child-proof", "math.formalProof", J("{}"))]),
                new("tool", "{\"success\":true,\"formalVerification\":\"KernelAccepted\",\"axiomAudit\":\"passed\",\"source\":\"work/Proof.lean\"}", ToolCallId: "child-proof"),
            },
        }, MissumAiProtocol.CreateJsonOptions());
        state = ScienceStagePolicy.ImportDelegatedEvidence(state, result);
        var receipt = Assert.Single(state.Evidence);
        Assert.True(receipt.FormalVerified);
        Assert.True(receipt.Historical);
        Assert.Equal("child", receipt.SourceRunId);
        Assert.Equal("in_progress", state.Plan[0].Status);
        Assert.Equal(state.Evidence.Count, ScienceStagePolicy.ImportDelegatedEvidence(state, result).Evidence.Count);
    }
}
