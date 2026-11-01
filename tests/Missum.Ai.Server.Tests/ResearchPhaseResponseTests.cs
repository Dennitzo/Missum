using System.Text.Json;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ResearchPhaseResponseTests
{
    private static JsonElement Schema => JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"questions":{"type":"array","maxItems":2,"items":{"type":"object","properties":{"question":{"type":"string","minLength":1},"query":{"type":"string","maxLength":8}},"required":["question","query"],"additionalProperties":false}},"sourceLimit":{"type":"integer","minimum":0,"maximum":12}},"required":["questions"],"additionalProperties":false}
        """);

    [Theory]
    [InlineData("{}", "$.questions")]
    [InlineData("{\"questions\":null}", "$.questions")]
    [InlineData("{\"questions\":[{\"question\":\"Frage\"}]}", "$.questions[0].query")]
    [InlineData("{\"questions\":[],\"sourceLimit\":13}", "$.sourceLimit")]
    [InlineData("{\"questions\":[],\"sourceLimit\":\"2\"}", "$.sourceLimit")]
    [InlineData("{\"questions\":[],\"queries\":[]}", "$.queries")]
    [InlineData("{\"questions\":[],\"questions\":[]}", "$.questions")]
    [InlineData("{\"questions\":[{\"question\":\"Frage\",\"query\":\"zu langer Text\"}]}", "$.questions[0].query")]
    public void InvalidPhaseFieldsAreDiagnosedBeforeThePipelineReadsThem(string content, string field)
    {
        var error = Assert.Throws<InvalidDataException>(() => ResearchPhaseResponse.Parse(content, Schema));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"questions\":[]}")]
    [InlineData("```json\n{\"questions\":[]}\n```")]
    [InlineData("```JSON\r\n{\"questions\":[]}\r\n```")]
    [InlineData("```\n{\"questions\":[]}\n```")]
    public void OnlyACompleteObjectOrSingleCompleteFenceIsAccepted(string content) =>
        Assert.Empty(ResearchPhaseResponse.Parse(content, Schema).GetProperty("questions").EnumerateArray());

    [Theory]
    [InlineData("Hier die Antwort: {\"questions\":[]}")]
    [InlineData("{\"questions\":[]} Noch mehr Text")]
    [InlineData("```json\n{\"questions\":[]}")]
    [InlineData("{\"questions\":[]}{\"questions\":[]}")]
    [InlineData("{\"questions\":[{\"question\":\"\\q\",\"query\":\"Test\"}]}")]
    public void ProseTruncationMultipleObjectsAndInvalidEscapesAreNotSilentlyRepaired(string content) =>
        Assert.Throws<InvalidDataException>(() => ResearchPhaseResponse.Parse(content, Schema));

    [Fact]
    public void ARepairPreservesTheEvaluatedReplyAndDoesNotDuplicateThePhaseEvidenceOrParentPrefix()
    {
        LmChatMessage[] parent = [new("system", "Systemkern"), new("user", "Ursprüngliche Frage")];
        var tool = new LmToolDefinition("research.plan", "Phase", Schema);
        var request = new StagedWebResearchModelRequest("model", "coding",
            [new("user", "Originalbeleg ID e1: unverändertes Zitat")], [tool], null, true, tool.Name, false);
        var conversation = new ResearchConversationContext(parent, []);
        _ = conversation.CreateRequest(request);
        var reply = new LmChatResult("{}", [], 100, 20, ReasoningContent: "Gespeicherter Modellstand");

        var error = Assert.Throws<ResearchPhaseResponseException>(() => conversation.AcceptResponse(request, reply));
        var repair = conversation.CreateRequest(request with { ResponseRepair = error.Message });

        Assert.Same(reply, error.Response);
        Assert.Single(repair, message => message.Content == "Originalbeleg ID e1: unverändertes Zitat");
        Assert.Single(repair, message => message.Role == "assistant" && message.Content == "{}"
            && message.ReasoningContent == reply.ReasoningContent);
        Assert.Contains("$.questions", repair[^1].Content, StringComparison.Ordinal);
        Assert.Contains(Schema.GetRawText(), repair[^1].Content, StringComparison.Ordinal);
        Assert.Same(parent[0], repair[0]);
        Assert.Same(parent[1], repair[1]);
        Assert.Equal(2, parent.Length);
        var repaired = conversation.AcceptResponse(request, new("{\"questions\":[]}", [], 120, 10));
        Assert.Equal(tool.Name, Assert.Single(repaired.ToolCalls).Name);
    }
}
