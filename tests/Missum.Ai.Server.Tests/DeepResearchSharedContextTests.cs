using System.Text.Json;
using Missum.Ai.Contracts;
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
            [Tool(name)], null, true, name, false);

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

        Assert.Throws<InvalidDataException>(() => conversation.AcceptResponse(stage, new(content, [], 10, 3)));
    }

    [Fact]
    public void ActualToolCallsInTheToolFreeResearchPhaseAreRejectedRatherThanExecuted()
    {
        var conversation = new ResearchConversationContext([new("system", "Systemkern")], [Tool("coding.edit")]);
        var stage = Stage();
        _ = conversation.CreateRequest(stage);
        var unexpected = new LmToolCall("unexpected-edit", "coding.edit", J("""{"path":"Publikation.md","newText":"falsch"}"""));

        Assert.Throws<InvalidDataException>(() => conversation.AcceptResponse(stage,
            new("{\"questions\":[],\"sourceLimit\":0}", [unexpected], 10, 3)));
    }
}
