using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceSkillTests
{
    private static readonly string Hash = new('a', 64);
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
    private static CodingWorkingState Read(CodingWorkingState state, string id, string hash) => Observe(state, id, "coding.read", "{\"path\":\"work/check.py\"}",
        "{\"success\":true,\"path\":\"work/check.py\",\"sha256\":\"" + hash + "\",\"content\":\"assert 1 == 1\"}");
    private static CodingWorkingState Complete(CodingWorkingState state, string id = "calc", string kind = "analytic", bool paths = false) =>
        CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
        {"steps":[{"id":"a","title":"A prüfen","status":"completed","evidenceIds":["ID"]}],"scienceStages":[{"id":"a","outcome":"clarified","checks":[{"id":"check","kind":"KIND","required":true,"evidenceIds":["ID"],"paths":PATHS}]}]}
        """.Replace("ID", id, StringComparison.Ordinal).Replace("KIND", kind, StringComparison.Ordinal).Replace("PATHS", paths ? "[\"work/check.py\"]" : "[]", StringComparison.Ordinal)));

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

    [Fact]
    public void MissingFailedAndWrongKindEvidenceCannotCompleteRequiredCheck()
    {
        var state = Plan(Start(), "numeric");
        Assert.Throws<ArgumentException>(() => Complete(state, kind: "numeric"));
        state = Observe(state, "calc", "coding.command", "{}", "{\"success\":false,\"exitCode\":1}");
        Assert.Throws<ArgumentException>(() => Complete(state, kind: "numeric"));
        state = Read(state, "read", Hash);
        Assert.Throws<ArgumentException>(() => Complete(state, "read", "numeric", paths: true));
        Assert.Equal("in_progress", state.Plan.Single().Status);
    }

    [Fact]
    public void NumericEvidenceDoesNotRequireHashBoundReadsOrAutomaticallyReopenStages()
    {
        var state = Plan(Start(), "numeric");
        state = Read(state, "input", Hash);
        state = Observe(state, "calc", "coding.command", "{}", "{\"success\":true,\"exitCode\":0,\"stdout\":\"independent reference agrees\"}");
        Assert.Equal("completed", Complete(state, kind: "numeric", paths: true).Plan[0].Status);
        state = Read(state, "after", Hash);
        state = Complete(state, kind: "numeric", paths: true);
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"steps":[{"id":"b","title":"Folgerung","status":"completed","evidenceIds":["calc"]},{"id":"c","title":"Unabhängig","status":"completed","evidenceIds":["calc"]}],"scienceStages":[{"id":"b","question":"Folgt B?","strategy":"Folgerung","dependsOn":["a"],"outcome":"clarified"},{"id":"c","question":"Gilt C?","strategy":"Unabhängig","outcome":"clarified"}]}
            """));
        var changed = Read(state, "changed", new('b', 64));
        Assert.Equal("completed", changed.Plan.Single(step => step.Id == "a").Status);
        Assert.Equal("completed", changed.Plan.Single(step => step.Id == "b").Status);
        Assert.Equal("completed", changed.Plan.Single(step => step.Id == "c").Status);
        Assert.NotEmpty(changed.Science!.Stages.Single(stage => stage.Id == "a").Checks.Single().EvidenceIds);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(changed, kind: "numeric", paths: true)));
    }

    [Fact]
    public void LeanInitializationOrCommandThatPrintsAcceptedIsNotAProof()
    {
        var state = Plan(Start(), "lean");
        state = Observe(state, "init", "math.formalProof", "{}", "{\"success\":true,\"formalVerification\":\"NotEstablished\"}");
        Assert.Throws<ArgumentException>(() => Complete(state, "init", "lean"));
        state = Observe(state, "fake", "coding.command", "{}", "{\"success\":true,\"exitCode\":0,\"formalVerification\":\"KernelAccepted\",\"axiomAudit\":\"passed\",\"sourceFileSha256\":\"" + Hash + "\"}");
        Assert.Throws<ArgumentException>(() => Complete(state, "fake", "lean"));
        state = Observe(state, "proof", "math.formalProof", "{}", "{\"status\":\"completed\",\"result\":{\"success\":true,\"formalVerification\":\"KernelAccepted\",\"axiomAudit\":\"passed\",\"source\":\"work/check.py\",\"sourceFileSha256\":\"" + Hash + "\"}}");
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "proof", "lean")));
        state = Read(state, "proof-read", Hash);
        state = Complete(state, "proof", "lean");
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(state));
    }

    [Fact]
    public void WrongPublicationReceiptNamesActualEditCandidateAndTheFieldToCorrect()
    {
        var state = Plan(Start(), "publication");
        state = Observe(state, "actual-edit", "coding.edit", "{\"path\":\"work/check.py\"}", "{\"success\":true,\"path\":\"work/check.py\",\"sha256\":\"" + Hash + "\"}");
        state = Observe(state, "read-only", "web.fetch", "{}", "{\"success\":true}");
        var error = Assert.Throws<ArgumentException>(() => Complete(state, "read-only", "publication", paths: true));
        Assert.Contains("Ersetze evidenceIds", error.Message);
        Assert.Contains("actual-edit", error.Message);
        Assert.Contains("web.fetch", error.Message);
        Assert.Contains("coding.edit", error.Message);
        var pendingError = Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"phase":"review","scienceStages":[{"id":"a","checks":[{"id":"check","kind":"publication","required":true,"evidenceIds":["read-only"],"paths":["work/check.py"]}]}]}
            """)));
        Assert.Contains("actual-edit", pendingError.Message);
        Assert.Empty(state.Science!.Stages[0].Checks[0].EvidenceIds);
    }

    [Fact]
    public void SequentialPublicationEditsAndCurrentReadCanCloseTheSameStage()
    {
        var state = Plan(Start(), "publication");
        state = Observe(state, "old-edit", "coding.edit", "{\"path\":\"work/check.py\"}",
            "{\"success\":true,\"path\":\"work/check.py\",\"sha256\":\"" + new string('b', 64) + "\"}");
        state = Observe(state, "final-edit", "coding.edit", "{\"path\":\"work/check.py\"}",
            "{\"success\":true,\"path\":\"work/check.py\",\"sha256\":\"" + Hash + "\"}");
        state = Read(state, "review", Hash);
        var update = J("""
            {"steps":[{"id":"a","title":"A prüfen","status":"completed","evidenceIds":["old-edit","final-edit"]}],
             "scienceStages":[{"id":"a","outcome":"clarified","checks":[{"id":"check","kind":"publication","required":true,
              "evidenceIds":["old-edit","final-edit","review"],"paths":["work/check.py"]}]}]}
            """);
        var completed = CodingWorkingStateReducer.ApplyPlanUpdate(state, update);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(completed));
        Assert.Equal("completed", Read(completed, "later-change", new('c', 64)).Plan[0].Status);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "review", "publication", paths: true)));
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "old-edit", "publication", paths: true)));
    }

    [Fact]
    public void PublicationReviewNeedsMatchingPathButNoFreshConfirmation()
    {
        var state = Plan(Start(), "publication");
        state = Observe(state, "edit", "coding.edit", "{\"path\":\"work/check.py\"}",
            "{\"success\":true,\"path\":\"work/check.py\",\"sha256\":\"" + Hash + "\"}");
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "edit", "publication", paths: true)));
        state = Read(state, "review", Hash);
        state = Observe(state, "unrelated-read", "coding.read", "{\"path\":\"other.md\"}",
            "{\"success\":true,\"path\":\"other.md\",\"sha256\":\"" + Hash + "\"}");
        Assert.Throws<ArgumentException>(() => Complete(state, "unrelated-read", "publication", paths: true));
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "review", "publication", paths: true)));
    }

    [Fact]
    public void NativeLeanWrapperUsesNestedAuditAndItsExplicitSourcePath()
    {
        var state = Observe(Plan(Start(), "lean"), "source", "coding.read", "{\"path\":\"Original.lean\"}", "{\"success\":true,\"path\":\"Original.lean\",\"sha256\":\"" + Hash + "\"}");
        state = Observe(state, "proof", "math.formalProof", "{\"path\":\"Original.lean\"}", """
            {"success":true,"formalVerification":"KernelAccepted","inputHashes":{"work/lean/Proof.lean":"HASH"},
            "lean":{"formalVerification":"KernelAccepted","axiomAudit":"passed","source":"lean/Proof.lean","sourceFileSha256":"HASH"}}
            """.Replace("HASH", Hash, StringComparison.Ordinal));
        var receipt = Assert.Single(state.Evidence, item => item.Id == "proof");
        Assert.True(receipt.FormalVerified);
        Assert.Equal("lean/Proof.lean", receipt.VerificationPath);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "proof", "lean")));
        state = Observe(state, "confirm", "coding.read", "{\"path\":\"work/lean/Proof.lean\"}",
            "{\"success\":true,\"path\":\"work/lean/Proof.lean\",\"sha256\":\"" + Hash + "\",\"content\":\"theorem checked : True := by trivial\"}");
        state = Complete(state, "proof", "lean");
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(state));
        state = Observe(state, "changed-original", "coding.read", "{\"path\":\"Original.lean\"}", "{\"success\":true,\"path\":\"Original.lean\",\"sha256\":\"" + new string('b', 64) + "\"}");
        Assert.Equal("completed", state.Plan[0].Status);
    }

    [Fact]
    public void OpenEtappeDoesNotBlockIndependentWorkAndCannotWeakenRequiredChecks()
    {
        var state = Plan(Start(), "lean");
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"scienceStages":[{"id":"a","checks":[{"id":"check","kind":"lean","required":false}]}]}
            """)));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"steps":[{"id":"a","title":"A prüfen","status":"pending"},{"id":"b","title":"Unabhängig","status":"in_progress"}],"scienceStages":[{"id":"a","blockers":["Hilfslemma offen"]},{"id":"b","question":"Gilt B?","strategy":"Grenzfall untersuchen"}]}
            """));
        Assert.Equal("b", state.Plan.Single(step => step.Status == "in_progress").Id);
        Assert.Equal("open", ScienceSkill.ResultOutcome(state));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J("""
            {"scienceStages":[{"id":"b","dependsOn":["a"]}]}
            """)));
    }

    [Fact]
    public void ExplicitReopenRequiresExplanationAndInvalidatesPriorReceipts()
    {
        var state = Plan(Start());
        state = Observe(state, "calc", "math.evaluate", "{}", "{\"success\":true}");
        state = Complete(state);
        const string reopen = """{"steps":[{"id":"a","title":"A prüfen","status":"in_progress"}]}""";
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, J(reopen)));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, J(reopen[..^1] + ",\"explanation\":\"Voraussetzung war unvollständig\"}"));
        Assert.Throws<ArgumentException>(() => Complete(state));
        state = Observe(state, "new-calc", "math.evaluate", "{}", "{\"success\":true}");
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "new-calc")));
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
        var state = Complete(Observe(Plan(Start()), "calc", "math.evaluate", "{}", "{\"success\":true}"));
        var checkpoint = new AgentRunCheckpoint([new("system", "policy"), new("user", "old task")], 1, 0, 0, 0, WorkingState: state);
        var continued = CodingSessionContext.ContinueWorkingState(checkpoint, "Nächste unabhängige Aufgabe", science: true)!;
        Assert.Empty(continued.Plan);
        Assert.Empty(continued.Science!.Stages);
        Assert.Equal(state.Evidence, continued.Evidence);
        Assert.NotEmpty(checkpoint.WorkingState!.Plan);
        Assert.NotEmpty(Plan(continued, "numeric").Science!.Stages);
        var messages = CodingSessionContext.Continue(checkpoint, [new("system", "policy"), new("user", "next task")], science: true);
        Assert.Contains(messages, message => message.Content!.Contains("steps und acceptanceCriteria starten leer", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceBasedTaskNeedsNoLeanOrFileHashConfirmation()
    {
        var state = Read(Plan(Start(), "source"), "source", Hash);
        state = Complete(state, "source", "source");
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(state));
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Read(state, "new-source", new('c', 64))));
    }

    [Fact]
    public void AnnotationKeepsJsonAndPreviouslyEvaluatedPrefixUnchanged()
    {
        var state = Observe(Plan(Start()), "calc", "math.evaluate", "{}", "{\"success\":true}");
        state = Observe(state, "later-child", "coding.command", "{}", "{\"success\":true,\"exitCode\":0}");
        var messages = new List<LmChatMessage> { new("system", "fixed"), new("tool", "{\"success\":true}", ToolCallId: "call") };
        ScienceSkill.AttachEvidence(messages, state, "call", "calc");
        Assert.Equal("fixed", messages[0].Content);
        Assert.Equal("calc", J(messages[1].Content!).GetProperty("_missumEvidence").GetProperty("id").GetString());
        Assert.Equal("math.evaluate", J(messages[1].Content!).GetProperty("_missumEvidence").GetProperty("tool").GetString());
    }

    [Fact]
    public void DelegatedProofUsesTrustedKernelReceiptWithoutParentFileConfirmation()
    {
        var state = Plan(Start(), "lean");
        var result = JsonSerializer.SerializeToElement(new
        {
            runId = "child",
            completionEvidence = new LmChatMessage[]
            {
                new("assistant", ToolCalls: [new("child-proof", "math.formalProof", J("{}"))]),
                new("tool", "{\"success\":true,\"formalVerification\":\"KernelAccepted\",\"axiomAudit\":\"passed\",\"source\":\"work/check.py\",\"sourceFileSha256\":\"" + Hash + "\"}", ToolCallId: "child-proof"),
            },
        }, MissumAiProtocol.CreateJsonOptions());
        state = ScienceStagePolicy.ImportDelegatedEvidence(state, result);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "child-proof", "lean")));
        state = Read(state, "parent-read", Hash);
        Assert.Equal("clarified", ScienceSkill.ResultOutcome(Complete(state, "child-proof", "lean")));
        Assert.Equal(state.Evidence.Count, ScienceStagePolicy.ImportDelegatedEvidence(state, result).Evidence.Count);
    }
}
