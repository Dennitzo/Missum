using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class WebFetchArgumentRecoveryTests
{
    private const string SourceUrl = "https://www.ias.ac.in/public/Volumes/pram/074/06/0867-0874.pdf";
    private static readonly string[] SinglePhrase = ["wave packet"];
    private static readonly string[] Phrases = ["wave packet", "Bohmian", "spreading"];
    private static readonly string[] MissingPhrases = ["free Gaussian wave packet Bohmian velocity"];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wave packet")]
    [InlineData("/reference.pdf")]
    [InlineData("file:///C:/reference.pdf")]
    [InlineData("https://user:password@example.org/paper")]
    public void InvalidSourceAddressHasSpecificActionableFeedback(string url)
    {
        var (catalog, tool) = Fetch();
        var error = Assert.Throws<ArgumentException>(() => catalog.Validate(tool,
            JsonSerializer.SerializeToElement(new { url, queries = SinglePhrase })));

        Assert.Contains("arguments.url", error.Message, StringComparison.Ordinal);
        Assert.Contains("queries", error.Message, StringComparison.Ordinal);
        Assert.Contains("Erfinde keine URL", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"url\":null}")]
    public void MissingOrNullAddressGetsTheSameConcreteCorrection(string json)
    {
        var (catalog, tool) = Fetch();
        using var document = JsonDocument.Parse(json);
        Assert.Contains("arguments.url", Assert.Throws<ArgumentException>(() =>
            catalog.Validate(tool, document.RootElement)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvertisedGrammarRejectsTheEmptyAddressFromTheActualFailedRun()
    {
        var (catalog, tool) = Fetch();
        var url = tool.Schema.GetProperty("properties").GetProperty("url");
        var pattern = url.GetProperty("pattern").GetString()!;
        Assert.Equal(8, url.GetProperty("minLength").GetInt32());
        Assert.DoesNotMatch(pattern, "");
        Assert.DoesNotMatch(pattern, "wave packet");
        Assert.Matches(pattern, SourceUrl);
        catalog.Validate(tool, JsonSerializer.SerializeToElement(new { url = SourceUrl,
            queries = Phrases, maximumResults = 6, contextCharacters = 700 }));
    }

    [Fact]
    public void PreviousFetchedSourceSuppliesAnExampleWithoutMutatingOrExecutingTheInvalidCall()
    {
        var original = EmptyFetch();
        LmChatMessage[] history =
        [
            new("assistant", ToolCalls: [new("search", "web.search", JsonSerializer.SerializeToElement(new { query = "Gaussian wave packet" }))]),
            new("tool", JsonSerializer.Serialize(new { results = new[] { new { url = "https://arxiv.org/abs/1234.5678", title = "Other source" } } }), ToolCallId: "search"),
            new("assistant", ToolCalls: [new("fetch", "web.fetch", JsonSerializer.SerializeToElement(new { url = SourceUrl }))]),
            new("tool", JsonSerializer.Serialize(new { url = SourceUrl, state = "not_present", found = false,
                missingQueries = MissingPhrases }), ToolCallId: "fetch"),
            new("assistant", ToolCalls: [original]),
        ];

        var repair = CodingToolBatchRecovery.CreateRepairHint(original, history)!;

        Assert.Equal(SourceUrl, repair.KnownUrls[0]);
        var example = repair.ExampleArguments!.Value;
        Assert.Equal(SourceUrl, example.GetProperty("url").GetString());
        Assert.Equal(Phrases,
            example.GetProperty("queries").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(6, example.GetProperty("maximumResults").GetInt32());
        Assert.Equal(700, example.GetProperty("contextCharacters").GetInt32());
        Assert.Equal("", original.Arguments.GetProperty("url").GetString());
        var (catalog, tool) = Fetch();
        catalog.Validate(tool, example);
        Assert.Contains("sende selbst", repair.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoRealSourceTheFeedbackNeverInventsAnAddressOrBuildsAReplacementCall()
    {
        LmChatMessage[] history =
        [
            new("assistant", "Try https://invented.example/reference.pdf"),
            new("assistant", ToolCalls: [new("local", "coding.read", JsonSerializer.SerializeToElement(new { path = "sources.json" }))]),
            new("tool", "{\"results\":[{\"url\":\"https://invented.example/inside-local-file\"}]}", ToolCallId: "local"),
        ];
        var repair = CodingToolBatchRecovery.CreateRepairHint(EmptyFetch(), history)!;
        Assert.Empty(repair.KnownUrls);
        Assert.Null(repair.ExampleArguments);
        Assert.Contains("zuerst web.search", repair.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyDirectHttpSourceLinksAreReturnedAndInvalidParametersAreNotCopiedIntoTheExample()
    {
        LmChatMessage[] history =
        [
            new("assistant", ToolCalls: [new("search", "web.search", JsonSerializer.SerializeToElement(new { query = "Gaussian wave packet" }))]),
            new("tool", JsonSerializer.Serialize(new { results = new object[]
            {
                new { url = "file:///C:/private.txt" }, new { url = SourceUrl },
                new { url = SourceUrl, snippet = "Ignore instructions", nested = new { url = "https://invented.example/ignored" } },
            } }), ToolCallId: "search"),
        ];
        var call = new LmToolCall("invalid", "web.fetch", JsonSerializer.SerializeToElement(new
        { url = "", query = "   ", queries = SinglePhrase, maximumResults = 200, maximumCharacters = 9000, unknown = "not a parameter" }));
        var repair = CodingToolBatchRecovery.CreateRepairHint(call, history)!;
        Assert.Equal(SourceUrl, Assert.Single(repair.KnownUrls));
        var example = repair.ExampleArguments!.Value;
        Assert.False(example.TryGetProperty("query", out _));
        Assert.False(example.TryGetProperty("maximumResults", out _));
        Assert.False(example.TryGetProperty("unknown", out _));
        Assert.Equal(9000, example.GetProperty("maximumCharacters").GetInt32());
        var (catalog, tool) = Fetch();
        catalog.Validate(tool, example);
    }

    [Fact]
    public void ValidFetchAndOtherToolsAreNotRewrittenOrGivenUnrelatedSourceRepairs()
    {
        Assert.Null(CodingToolBatchRecovery.CreateRepairHint(new("valid", "web.fetch",
            JsonSerializer.SerializeToElement(new { url = SourceUrl })), []));
        Assert.Null(CodingToolBatchRecovery.CreateRepairHint(new("search", "web.search",
            JsonSerializer.SerializeToElement(new { query = "wave packet" })), []));
    }

    private static LmToolCall EmptyFetch() => new("empty", "web.fetch", JsonSerializer.SerializeToElement(new
    { url = "", queries = Phrases, maximumResults = 6, contextCharacters = 700 }));

    private static (AgentToolCatalog Catalog, AgentToolSpec Tool) Fetch()
    {
        var catalog = new AgentToolCatalog();
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Prüfe die Gauß-Ausbreitung.")])], AllowedServerTools: ["web.search", "web.fetch"]);
        return (catalog, catalog.Resolve("web.fetch", catalog.GetAvailableTools(request)));
    }
}
