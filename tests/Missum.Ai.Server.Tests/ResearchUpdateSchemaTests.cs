using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ResearchUpdateSchemaTests
{
    private static readonly string[] RequiredChangeFields = ["id", "kind", "expectedRevision", "data"];
    private static readonly string[] RequiredPatchChangeFields = ["id", "kind", "expectedRevision", "patch"];
    private static readonly string[] InvalidSourceIds = [""];
    private static readonly string[] ManuscriptFields = ["title", "contentMarkdown"];
    private static readonly string[] AssertionFields = ["statement"];
    private static readonly string[] ScientificKinds = ["claim", "contribution", "hypothesis", "requirement", "section"];
    private readonly AgentToolCatalog _catalog = new();

    [Theory]
    [InlineData("section")]
    [InlineData("contribution")]
    public void ManuscriptObjectsRequireTheirActualTextAndRejectMetadataOnlySubmissions(string kind)
    {
        var tool = ResearchUpdateTool();
        _catalog.Validate(tool, Arguments(kind, new { title = "Grenzfall", contentMarkdown = "Der Grenzfall folgt aus $v/c \\to 0$.", description = "Hintergrund" }));
        var error = Assert.Throws<ArgumentException>(() => _catalog.Validate(tool,
            Arguments(kind, new { title = "Grenzfall", description = "Ein Entwurf", method = "Herleitung", limit = "Keine neue Theorie" })));
        Assert.Contains("kind=" + kind, error.Message, StringComparison.Ordinal);
        Assert.Contains("child-draft", error.Message, StringComparison.Ordinal);
        Assert.Contains("data.title und data.contentMarkdown", error.Message, StringComparison.Ordinal);
        Assert.Contains("description, method und limit ersetzen ihn nicht", error.Message, StringComparison.Ordinal);
        Assert.Contains("expectedRevision", error.Message, StringComparison.Ordinal);
        Assert.Contains("keine Änderung wurde übernommen", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hypothesis")]
    [InlineData("claim")]
    [InlineData("requirement")]
    public void ScientificAssertionsRequireStatementWithoutArtificialManuscriptFields(string kind)
    {
        var tool = ResearchUpdateTool();
        _catalog.Validate(tool, Arguments(kind, new { statement = "Den klassischen Grenzfall prüfen.", method = "Dimensionsanalyse" }));
        var error = Assert.Throws<ArgumentException>(() => _catalog.Validate(tool,
            Arguments(kind, new { description = "Nur Metadaten", contentMarkdown = "Keine Aussage" })));
        Assert.Contains("kind=" + kind, error.Message, StringComparison.Ordinal);
        Assert.Contains("data.statement", error.Message, StringComparison.Ordinal);
        Assert.Contains("description ersetzt sie nicht", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyManuscriptTextProducesTheSameActionableCorrection(string? text)
    {
        var error = Assert.Throws<ArgumentException>(() => _catalog.Validate(ResearchUpdateTool(),
            Arguments("contribution", new { title = "Entwurf", contentMarkdown = text })));
        Assert.Contains("data.contentMarkdown", error.Message, StringComparison.Ordinal);
        Assert.Contains("64000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidTitleOrOversizedContentNeverPassesAsACompleteContribution()
    {
        var tool = ResearchUpdateTool();
        var titleError = Assert.Throws<ArgumentException>(() => _catalog.Validate(tool,
            Arguments("contribution", new { contentMarkdown = "Wissenschaftlicher Text" })));
        Assert.Contains("data.title fehlt", titleError.Message, StringComparison.Ordinal);
        var contentError = Assert.Throws<ArgumentException>(() => _catalog.Validate(tool,
            Arguments("contribution", new { title = "Entwurf", contentMarkdown = new string('a', 64_001) })));
        Assert.Contains("data.contentMarkdown", contentError.Message, StringComparison.Ordinal);
        _catalog.Validate(tool, Arguments("contribution", new { title = "Entwurf", contentMarkdown = new string('a', 64_000) }));
    }

    [Theory]
    [InlineData("coding/Qwen3.8", null)]
    [InlineData("coding/Qwen3.8", ToolContextProfiles.Current)]
    [InlineData("coding/Qwen3.8@subagent", ToolContextProfiles.Current)]
    [InlineData("coding/DeepSeek", ToolContextProfiles.Current)]
    public void TransportRetainsDisjointCompleteKindAlternativesAndRequiredContent(string model, string? profile)
    {
        var spec = ResearchUpdateTool();
        var function = ModelRuntimeClient.PrepareTransportTools(model,
            [spec.ToLmDefinition() with { ContextProfileVersion = profile }])[0].GetProperty("function");
        var schema = function.GetProperty("parameters");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("anyOf", out _));
        Assert.False(schema.TryGetProperty("oneOf", out _));
        Assert.False(schema.TryGetProperty("allOf", out _));
        Assert.False(schema.TryGetProperty("if", out _));
        var alternatives = schema.GetProperty("properties").GetProperty("changes").GetProperty("items").GetProperty("anyOf");
        Assert.Equal(3, alternatives.GetArrayLength());
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alternative in alternatives.EnumerateArray())
        {
            Assert.Equal("object", alternative.GetProperty("type").GetString());
            Assert.False(alternative.GetProperty("additionalProperties").GetBoolean());
            if (alternative.GetProperty("properties").TryGetProperty("patch", out var patch))
            {
                Assert.Equal(RequiredPatchChangeFields, Strings(alternative.GetProperty("required")));
                Assert.False(alternative.GetProperty("properties").TryGetProperty("data", out _));
                Assert.Equal(1, alternative.GetProperty("properties").GetProperty("expectedRevision").GetProperty("minimum").GetInt32());
                Assert.Equal(1, patch.GetProperty("minProperties").GetInt32());
                Assert.False(patch.GetProperty("additionalProperties").GetBoolean());
                Assert.False(patch.TryGetProperty("required", out _));
                Assert.True(patch.GetProperty("properties").TryGetProperty("review", out _));
                continue;
            }
            Assert.Equal(RequiredChangeFields, Strings(alternative.GetProperty("required")));
            var properties = alternative.GetProperty("properties");
            Assert.Equal(200, properties.GetProperty("id").GetProperty("maxLength").GetInt32());
            Assert.Equal(0, properties.GetProperty("expectedRevision").GetProperty("minimum").GetInt32());
            var data = properties.GetProperty("data");
            Assert.False(data.GetProperty("additionalProperties").GetBoolean());
            foreach (var kind in Strings(properties.GetProperty("kind").GetProperty("enum")))
            {
                Assert.True(kinds.Add(kind), "An object kind must match exactly one schema alternative.");
                var expected = kind is "section" or "contribution" ? ManuscriptFields : AssertionFields;
                Assert.Equal(expected, Strings(data.GetProperty("required")));
                foreach (var required in expected)
                    Assert.Equal(1, data.GetProperty("properties").GetProperty(required).GetProperty("minLength").GetInt32());
            }
            Assert.Equal(64_000, data.GetProperty("properties").GetProperty("contentMarkdown").GetProperty("maxLength").GetInt32());
            Assert.Equal(16_000, data.GetProperty("properties").GetProperty("statement").GetProperty("maxLength").GetInt32());
            Assert.True(data.GetProperty("properties").TryGetProperty("method", out _));
        }
        Assert.Equal(ScientificKinds, kinds.Order(StringComparer.Ordinal));
        Assert.Contains("data.contentMarkdown", function.GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("data.statement", function.GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataPatchDoesNotRequireRepeatingExistingManuscriptAndCannotCreateNewObjects()
    {
        var tool = ResearchUpdateTool();
        _catalog.Validate(tool, PatchArguments(new { status = "completed" }));
        _catalog.Validate(tool, PatchArguments(new { reason = (string?)null }));
        _catalog.Validate(tool, PatchArguments(new { status = "draft", review = new { itemRevision = 2,
            sourceAssessment = "Originalbelege gelesen", calculationAssessment = "Rechnung geprüft",
            contradictionAssessment = "Offene Grenze dokumentiert", scope = "Modellannahmen" } }));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(tool, PatchArguments(new { status = "completed" }, 0)));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(tool, PatchArguments(new { unsupported = (string?)null })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(tool, PatchArguments(new { sourceIds = InvalidSourceIds })));
    }

    [Fact]
    public void AmbiguousDataAndPatchOrReviewWithoutExplicitStatusHasAnActionableError()
    {
        var tool = ResearchUpdateTool();
        var ambiguous = JsonSerializer.SerializeToElement(new { projectId = "research-test", changes = new[]
        {
            new { id = "section", kind = "section", expectedRevision = 1,
                data = new { title = "Titel", contentMarkdown = "Text" }, patch = new { status = "completed" } },
        } });
        Assert.Throws<ArgumentException>(() => _catalog.Validate(tool, ambiguous));
        var error = Assert.Throws<ArgumentException>(() => _catalog.Validate(tool, PatchArguments(new { review = new { itemRevision = 2,
            sourceAssessment = "Quelle", calculationAssessment = "Rechnung", contradictionAssessment = "Grenze", scope = "Modell" } })));
        Assert.Contains("ausdrücklich status", error.Message, StringComparison.Ordinal);
        Assert.Contains("patch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleOnlyUpdateRemainsSupportedAndUnknownDataNeverBecomesScientificText()
    {
        var tool = ResearchUpdateTool();
        _catalog.Validate(tool, JsonSerializer.SerializeToElement(new { projectId = "research-test", title = "Wissenschaftlicher Titel", changes = Array.Empty<object>() }));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(tool,
            Arguments("contribution", new { title = "Titel", contentMarkdown = "Inhalt", body = "Unbekanntes Feld" })));
    }

    private AgentToolSpec ResearchUpdateTool() => _catalog.Resolve(ClientToolNames.ResearchUpdate,
        _catalog.GetAvailableTools(new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new RunMessage("user", [new ContentPart("text", Text: "Forschung")])],
            ClientCapabilities: ["research.deliverables"], AllowedServerTools: [],
            ResearchOptions: new(ProtocolVersion: 2))));

    private static JsonElement Arguments(string kind, object data) => JsonSerializer.SerializeToElement(new
    {
        projectId = "research-test",
        changes = new[] { new { id = "child-draft", kind, expectedRevision = 0, data } },
    });

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(static item => item.GetString()!).ToArray();
    private static JsonElement PatchArguments(object patch, long expectedRevision = 1) => JsonSerializer.SerializeToElement(new
    {
        projectId = "research-test", changes = new[] { new { id = "section", kind = "section", expectedRevision, patch } },
    });
}
