using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class DeepResearchSharedContextTests
{
    private static JsonElement J(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static LmToolDefinition Tool(string name) => new(name, "Unveränderte Werkzeugbeschreibung: ä, \\alpha.",
        J("""{"type":"object","properties":{"task":{"type":"string"}},"required":["task"]}"""));

    private static StagedWebResearchModelRequest Stage(string name = "research.plan", string instruction = "Plane die Recherche.") =>
        new("coding/shared-context-fixture", "coding",
            [new("system", instruction), new("user", "Die konkrete Recherchefrage und gelesene Belege.")],
            [new(name, "Strukturiertes Phasenergebnis", J("""{"type":"object"}"""))], null, true, name, false);

    [Fact]
    public void ResearchRetainsTheExactParentPrefixAndToolDefinitionsWithoutEditingTheParent()
    {
        LmChatMessage[] parent = [new("system", "Unveränderter Systemkern."), new("user", "Prüfe die Herleitung."),
            new("assistant", "Ich prüfe zunächst die Voraussetzungen.", ReasoningContent: "Gespeicherter Kontext.")];
        LmToolDefinition[] tools = [Tool("coding.read"), Tool("web.deepResearch")];
        var before = JsonSerializer.Serialize(parent);
        var toolBefore = JsonSerializer.Serialize(tools);
        var conversation = new ResearchConversationContext(parent, tools);

        var request = conversation.CreateRequest(Stage());

        Assert.True(request.Count > parent.Length);
        for (var index = 0; index < parent.Length; index++) Assert.Same(parent[index], request[index]);
        Assert.Equal(before, JsonSerializer.Serialize(parent));
        Assert.Equal(toolBefore, JsonSerializer.Serialize(conversation.Tools));
        Assert.Equal(tools.Length, conversation.Tools.Count);
        for (var index = 0; index < tools.Length; index++) Assert.Same(tools[index], conversation.Tools[index]);
        Assert.DoesNotContain(conversation.Tools, tool => tool.Name == "research.plan");
    }

    [Fact]
    public void PendingParentCallsReceiveLocalReceiptsWhileCompletedCallsAreNotDuplicated()
    {
        var completed = new LmToolCall("read-done", "coding.read", J("""{"path":"Publikation.md"}"""));
        var pending = new LmToolCall("research-running", "web.deepResearch", J("""{"task":"Prüfe das Ergebnis."}"""));
        var pendingSibling = new LmToolCall("sibling-running", "coding.read", J("""{"path":"work/proof.py"}"""));
        LmChatMessage[] parent = [new("system", "Systemkern"), new("user", "Prüfen"),
            new("assistant", ToolCalls: [completed]), new("tool", "Originaler gelesener Inhalt", ToolCallId: completed.Id),
            new("assistant", ToolCalls: [pending, pendingSibling])];
        var before = JsonSerializer.Serialize(parent);
        var conversation = new ResearchConversationContext(parent, [Tool("web.deepResearch")]);

        var request = conversation.CreateRequest(Stage());

        Assert.Single(request, item => item.Role == "tool" && item.ToolCallId == completed.Id);
        Assert.Single(request, item => item.Role == "tool" && item.ToolCallId == pending.Id);
        Assert.Single(request, item => item.Role == "tool" && item.ToolCallId == pendingSibling.Id);
        Assert.All(request.Skip(parent.Length).Take(2), item => Assert.Equal("tool", item.Role));
        Assert.Equal(before, JsonSerializer.Serialize(parent));
        Assert.DoesNotContain(parent, item => item.Role == "tool" && item.ToolCallId == pending.Id);

        _ = conversation.AcceptResponse(Stage(), new("{\"questions\":[],\"sourceLimit\":0}", [], 10, 3));
        var nextRequest = conversation.CreateRequest(Stage("research.synthesize"));
        Assert.Single(nextRequest, item => item.Role == "tool" && item.ToolCallId == completed.Id);
        Assert.Single(nextRequest, item => item.Role == "tool" && item.ToolCallId == pending.Id);
        Assert.Single(nextRequest, item => item.Role == "tool" && item.ToolCallId == pendingSibling.Id);
    }

    [Fact]
    public void LaterPhasesAppendToTheResearchConversationIncludingItsEarlierResponse()
    {
        LmChatMessage[] parent = [new("system", "Systemkern"), new("user", "Nutzerauftrag")];
        var conversation = new ResearchConversationContext(parent, [Tool("web.deepResearch")]);
        var firstStage = Stage(instruction: "Erste Phase: Recherche planen.");
        var firstRequest = conversation.CreateRequest(firstStage).ToArray();
        const string firstAnswer = "{\"questions\":[],\"sourceLimit\":0}";
        _ = conversation.AcceptResponse(firstStage, new(firstAnswer, [], 15, 9));

        var secondRequest = conversation.CreateRequest(Stage("research.synthesize", "Zweite Phase: Belege zusammenführen."));

        Assert.True(secondRequest.Count > firstRequest.Length);
        for (var index = 0; index < firstRequest.Length; index++) Assert.Same(firstRequest[index], secondRequest[index]);
        var firstResponseIndex = secondRequest.ToList().FindIndex(item => item.Role == "assistant" && item.Content == firstAnswer);
        var secondInstructionIndex = secondRequest.ToList().FindIndex(item => item.Content?.Contains("Zweite Phase:", StringComparison.Ordinal) == true);
        Assert.True(firstResponseIndex >= firstRequest.Length);
        Assert.True(secondInstructionIndex > firstResponseIndex);
        Assert.Single(secondRequest, item => item.Role == "assistant" && item.Content == firstAnswer);
    }

    [Fact]
    public void JsonPhaseResponseBecomesTheExpectedSyntheticCallAndPreservesNativeMetrics()
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern")], [Tool("web.deepResearch")]);
        var stage = Stage();
        _ = conversation.CreateRequest(stage);
        var metrics = new ModelTurnMetrics(PromptEvaluatedTokens: 37, CachedPromptTokens: 12_345,
            ReasoningTokens: 5, InputTokens: 12_382, OutputTokens: 21);
        var original = new LmChatResult("{\"questions\":[],\"sourceLimit\":0}", [], 12_382, 21,
            HadReasoning: true, ReasoningTokens: 5, Metrics: metrics, ReasoningContent: "Interner Denktext.", FinishReason: "stop");

        var adapted = conversation.AcceptResponse(stage, original);

        var call = Assert.Single(adapted.ToolCalls);
        Assert.Equal("research.plan", call.Name);
        Assert.False(string.IsNullOrWhiteSpace(call.Id));
        Assert.Equal(0, call.Arguments.GetProperty("sourceLimit").GetInt32());
        Assert.Equal(JsonValueKind.Array, call.Arguments.GetProperty("questions").ValueKind);
        Assert.Equal(original.InputTokens, adapted.InputTokens);
        Assert.Equal(original.OutputTokens, adapted.OutputTokens);
        Assert.Same(metrics, adapted.Metrics);
        Assert.Equal(original.HadReasoning, adapted.HadReasoning);
        Assert.Equal(original.ReasoningTokens, adapted.ReasoningTokens);
        Assert.Equal(original.ReasoningContent, adapted.ReasoningContent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Kein strukturiertes Ergebnis.")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void MissingMalformedOrNonObjectResponsesCannotBecomeResearchCalls(string? content)
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern")], [Tool("web.deepResearch")]);
        var stage = Stage();
        _ = conversation.CreateRequest(stage);

        Assert.Throws<ResearchPhaseResponseException>(() => conversation.AcceptResponse(stage, new(content, [], 10, 3)));
    }

    [Fact]
    public void ActualToolCallsInTheToolFreeResearchPhaseAreRejectedRatherThanExecuted()
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern")], [Tool("coding.edit")]);
        var stage = Stage();
        _ = conversation.CreateRequest(stage);
        var unexpected = new LmToolCall("unexpected-edit", "coding.edit", J("""{"path":"Publikation.md","newText":"falsch"}"""));

        var error = Assert.Throws<ResearchPhaseResponseException>(() => conversation.AcceptResponse(stage,
            new("{\"questions\":[],\"sourceLimit\":0}", [unexpected], 10, 3)));
        Assert.Equal(10, error.Response.InputTokens);
        var rejected = Assert.Single(conversation.Messages, item => item.Role == "tool" && item.ToolCallId == unexpected.Id);
        Assert.Contains("research.phase_tools_disabled", rejected.Content, StringComparison.Ordinal);
        Assert.Same(unexpected, Assert.Single(conversation.Messages.Last(item => item.Role == "assistant").ToolCalls!));
    }

    [Fact]
    public void CompactionPreservesTheOwnerTaskPoliciesAndCurrentPhaseEvidenceAndSchemaExactly()
    {
        var system = new LmChatMessage("system", "Unveränderter Systemkern und Werkzeugrechte.");
        var original = new LmChatMessage("user", "Prüfe die kovariante Herleitung und kennzeichne offene Fragen.");
        LmChatMessage[] parent = [system, original, new("assistant", "Ältere abgeschlossene Arbeit: " + new string('a', 18_000))];
        var before = JsonSerializer.Serialize(parent);
        var tools = new[] { Tool("web.deepResearch"), Tool("coding.read") };
        var conversation = new ResearchConversationContext(parent, tools);
        var stage = Stage("research.synthesize") with
        {
            Messages = [new("system", "Nur tatsächlich gelesene Originalstellen bewerten."),
                new("user", "{\"evidenceId\":\"source-1:excerpt-4\",\"url\":\"https://example.org/original\",\"quote\":\"Originalstelle mit Vorzeichen - und Einheiten.\"}")],
        };
        var request = conversation.CreateRequest(stage).ToArray();
        var protectedTail = request.Skip(parent.Length).ToArray();
        var plan = conversation.PlanCompaction(16_384)!;

        Assert.NotNull(plan);
        Assert.Same(original, plan.CurrentRequest);
        Assert.Equal(1, plan.ArchivedMessages);
        Assert.Contains(original.Content!, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
        Assert.Contains("Ältere abgeschlossene Arbeit", plan.SummaryRequest[1].Content, StringComparison.Ordinal);
        Assert.Equal(protectedTail.Length, plan.RecentMessages.Count);
        for (var i = 0; i < protectedTail.Length; i++) Assert.Same(protectedTail[i], plan.RecentMessages[i]);

        var compacted = conversation.CompleteCompaction(plan, "Die ältere Herleitung bleibt eine unbestätigte Annahme; Quelle 1 muss geprüft werden.");

        Assert.Same(system, compacted[0]);
        Assert.Contains(original, compacted);
        Assert.StartsWith(CodingContextCompactor.MemoryMarker, compacted[1].Content);
        var actualTail = compacted.TakeLast(protectedTail.Length).ToArray();
        for (var i = 0; i < protectedTail.Length; i++) Assert.Same(protectedTail[i], actualTail[i]);
        Assert.Equal(before, JsonSerializer.Serialize(parent));
        Assert.Equal(JsonSerializer.Serialize(compacted), JsonSerializer.Serialize(conversation.Messages));
        Assert.Equal(JsonSerializer.Serialize(tools), JsonSerializer.Serialize(conversation.Tools));
    }

    [Fact]
    public void CompactionCarriesTheTypedWorkingPlanOutsideTheHistoricalSummary()
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern"), new("user", "Quellen prüfen"),
            new("assistant", new string('a', 24_000))], [Tool("web.deepResearch")]);
        _ = conversation.CreateRequest(Stage());
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Quellen prüfen"), J("""
            {"steps":[{"id":"source-check","title":"Originalquelle lesen","status":"in_progress"}]}
            """));

        var plan = conversation.PlanCompaction(16_384, state)!;
        var stateMessage = Assert.Single(plan.PreservedContextMessages!);
        Assert.StartsWith(CodingEvidenceContext.Marker, stateMessage.Content);
        Assert.Contains("source-check", stateMessage.Content, StringComparison.Ordinal);

        var compacted = conversation.CompleteCompaction(plan, "Bisher wurde noch keine Quelle fachlich bestätigt.");

        Assert.Contains(stateMessage, compacted);
        Assert.Same(stateMessage, plan.WorkingStateMessage);
        Assert.Same(stateMessage, plan.SummaryRequest[^1]);
    }

    [Fact]
    public void LaterCompactionArchivesCompletedResearchPhasesWithoutRepeatingTheActivePhase()
    {
        var original = new LmChatMessage("user", "Forschung fortsetzen.");
        var conversation = new ResearchConversationContext([new("system", "Systemkern"), original,
            new("assistant", new string('a', 12_000))], [Tool("web.deepResearch")]);
        var first = Stage("research.plan", "Erste Recherchephase.");
        _ = conversation.CreateRequest(first);
        var initialPlan = conversation.PlanCompaction(16_384)!;
        _ = conversation.CompleteCompaction(initialPlan, "Vorarbeit abgeschlossen.");
        const string firstAnswer = "{\"questions\":[],\"sourceLimit\":0,\"notes\":\"";
        _ = conversation.AcceptResponse(first, new(firstAnswer + new string('b', 8_000) + "\"}", [], 100, 50));
        _ = conversation.CreateRequest(Stage("research.synthesize", "Zweite aktuelle Recherchephase."));

        var secondPlan = conversation.PlanCompaction(16_384)!;
        Assert.Contains("Erste Recherchephase.", secondPlan.SummaryRequest[1].Content, StringComparison.Ordinal);
        Assert.Contains("questions", secondPlan.SummaryRequest[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Zweite aktuelle Recherchephase.", secondPlan.SummaryRequest[1].Content, StringComparison.Ordinal);
        var compacted = conversation.CompleteCompaction(secondPlan, "Der Rechercheplan wurde erstellt; die Synthese ist noch offen.");

        Assert.Single(compacted, message => ReferenceEquals(message, original));
        Assert.Single(compacted, message => message.Content?.Contains("Zweite aktuelle Recherchephase.", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(compacted, message => message.Content?.Contains("Erste Recherchephase.", StringComparison.Ordinal) == true);
        Assert.Single(compacted, message => message.Content?.StartsWith(CodingContextCompactor.MemoryMarker, StringComparison.Ordinal) == true);
        _ = conversation.AcceptResponse(Stage("research.synthesize"), new("{\"findings\":[],\"uncertainties\":[]}", [], 20, 10));
        var next = conversation.CreateRequest(Stage("research.verify", "Dritte aktuelle Recherchephase."));
        Assert.Contains(next, message => message.Role == "assistant" && message.Content == "{\"findings\":[],\"uncertainties\":[]}");
        Assert.Single(next, message => message.Content?.Contains("Dritte aktuelle Recherchephase.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ContextWithoutArchivedWorkHasNothingToCompact()
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern"), new("user", "Aktueller Auftrag")], [Tool("web.deepResearch")]);
        Assert.Null(conversation.PlanCompaction(16_384));
        _ = conversation.CreateRequest(Stage());
        Assert.Null(conversation.PlanCompaction(16_384));
        var noTask = new ResearchConversationContext([new("system", "Systemkern"), new("assistant", "Ältere Arbeit")], [Tool("web.deepResearch")]);
        _ = noTask.CreateRequest(Stage());
        Assert.Null(noTask.PlanCompaction(16_384));
    }

    [Fact]
    public void EmptyOrNonShrinkingSummaryCannotDiscardResearchContext()
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern"), new("user", "Auftrag"),
            new("assistant", "Kurze Vorarbeit")], [Tool("web.deepResearch")]);
        _ = conversation.CreateRequest(Stage());
        var plan = conversation.PlanCompaction(16_384)!;
        var before = JsonSerializer.Serialize(conversation.Messages);

        Assert.Throws<InvalidOperationException>(() => conversation.CompleteCompaction(plan, " "));
        Assert.Throws<InvalidOperationException>(() => conversation.CompleteCompaction(plan, new string('x', 1_000)));
        Assert.Equal(before, JsonSerializer.Serialize(conversation.Messages));
    }

    [Fact]
    public void CompletionOfAnOlderCompactionCannotOverwriteANewerResearchPhase()
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern"), new("user", "Auftrag"),
            new("assistant", new string('a', 8_000))], [Tool("web.deepResearch")]);
        _ = conversation.CreateRequest(Stage());
        var plan = conversation.PlanCompaction(16_384)!;
        _ = conversation.AcceptResponse(Stage(), new("{\"questions\":[]}", [], 10, 5));
        _ = conversation.CreateRequest(Stage("research.synthesize"));
        var before = JsonSerializer.Serialize(conversation.Messages);

        Assert.Throws<InvalidOperationException>(() => conversation.CompleteCompaction(plan, "Veralteter Stand"));
        Assert.Equal(before, JsonSerializer.Serialize(conversation.Messages));
    }
}
