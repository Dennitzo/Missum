using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificQuantitativeContractTests
{
    private readonly AgentToolCatalog _catalog = new();
    private static readonly string[] DefinitionFields = ["observable", "reference", "artifactPath", "sampling", "checks"];

    [Fact]
    public void CreationAndTargetedPatchAcceptTheSameCompleteContract()
    {
        var tool = Tool();
        _catalog.Validate(tool, Arguments(Definition()));
        _catalog.Validate(tool, JsonSerializer.SerializeToElement(new
        {
            projectId = "research-test", changes = new[] { new { id = "double-slit", kind = "requirement", expectedRevision = 2,
                patch = new { quantitativeValidation = Definition(), status = "unresolved" } } },
        }));
    }

    [Theory]
    [InlineData("coding/Qwen3.8")]
    [InlineData("coding/DeepSeek")]
    public void ActualTransportRetainsTheAcceptanceContractInEveryDataAndPatchAlternative(string model)
    {
        var function = ModelRuntimeClient.PrepareTransportTools(model,
            [Tool().ToLmDefinition() with { ContextProfileVersion = ToolContextProfiles.Current }])[0].GetProperty("function");
        var alternatives = function.GetProperty("parameters").GetProperty("properties").GetProperty("changes").GetProperty("items").GetProperty("anyOf");
        foreach (var change in alternatives.EnumerateArray())
        {
            var fields = change.GetProperty("properties");
            var isPatch = fields.TryGetProperty("patch", out var payload);
            if (!isPatch) payload = fields.GetProperty("data");
            foreach (var branch in payload.GetProperty("anyOf").EnumerateArray())
            {
                var quantitative = branch.GetProperty("properties").GetProperty("quantitativeValidation");
                if (isPatch) quantitative = quantitative.GetProperty("anyOf")[0];
                Assert.Equal("object", quantitative.GetProperty("type").GetString());
                Assert.False(quantitative.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(DefinitionFields, quantitative.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
                var check = quantitative.GetProperty("properties").GetProperty("checks");
                Assert.Equal(2, check.GetProperty("minItems").GetInt32());
                Assert.False(check.GetProperty("items").GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(0, check.GetProperty("items").GetProperty("properties").GetProperty("maximum").GetProperty("minimum").GetInt32());
                // Compact transport deliberately strips prose descriptions. The
                // allowed semantic categories must remain in the grammar itself.
                var kinds = check.GetProperty("items").GetProperty("properties").GetProperty("kind").GetProperty("enum")
                    .EnumerateArray().Select(value => value.GetString()).ToArray();
                Assert.Contains("accuracy", kinds);
                Assert.Contains("convergence", kinds);
                Assert.Contains("uncertainty", kinds);
            }
        }
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("pearson")]
    [InlineData("uncertainty")]
    [InlineData("convergence")]
    [InlineData("duplicate_metric")]
    [InlineData("negative")]
    [InlineData("infinite")]
    [InlineData("path")]
    [InlineData("trailing_newline")]
    [InlineData("empty_reference")]
    [InlineData("extra_field")]
    public void InvalidContractsProduceActionableErrorsBeforeDispatch(string error)
    {
        var definition = JsonNode.Parse(Definition().GetRawText())!;
        switch (error)
        {
            case "correlation": definition["checks"]![0]!["metric"] = "one_minus_correlation"; break;
            case "pearson": definition["checks"]![0]!["metric"] = "Pearson"; break;
            case "uncertainty": definition["checks"]!.AsArray().RemoveAt(2); break;
            case "convergence": definition["checks"]![1]!["kind"] = "accuracy"; break;
            case "duplicate_metric": definition["checks"]![1]!["metric"] = "distribution_error"; break;
            case "negative": definition["checks"]![0]!["maximum"] = -1; break;
            case "infinite": definition["checks"]![0]!["maximum"] = JsonNode.Parse("1e999"); break;
            case "path": definition["artifactPath"] = "work/../outside.json"; break;
            case "trailing_newline": definition["artifactPath"] = "work/validation.json\n"; break;
            case "empty_reference": definition["reference"] = "   "; break;
            case "extra_field": definition["passed"] = true; break;
        }
        var exception = Assert.Throws<ArgumentException>(() => _catalog.Validate(Tool(), Arguments(JsonSerializer.SerializeToElement(definition))));
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    private AgentToolSpec Tool() => _catalog.Resolve(ClientToolNames.ResearchUpdate,
        _catalog.GetAvailableTools(new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new RunMessage("user", [new ContentPart("text", Text: "Doppelspalt quantitativ prüfen")])],
            ClientCapabilities: ["research.deliverables"], AllowedServerTools: [], ResearchOptions: new(ProtocolVersion: 2))));
    private static JsonElement Arguments(JsonElement quantitativeValidation) => JsonSerializer.SerializeToElement(new
    {
        projectId = "research-test", changes = new[] { new { id = "double-slit", kind = "requirement", expectedRevision = 0,
            data = new { statement = "Die Auftreffverteilung quantitativ prüfen.", required = true, quantitativeValidation } } },
    });
    private static JsonElement Definition() => JsonSerializer.Deserialize<JsonElement>("""
        {"observable":"Normierte Auftreffverteilung am Schirm","reference":"Zeitintegrierter positiver Strom desselben Schirmmodells","artifactPath":"work/validation.json","sampling":true,"checks":[{"metric":"distribution_error","kind":"accuracy","maximum":0.05},{"metric":"refinement_error","kind":"convergence","maximum":0.01},{"metric":"sampling_error","kind":"uncertainty","maximum":0.1}]}
        """);
}
