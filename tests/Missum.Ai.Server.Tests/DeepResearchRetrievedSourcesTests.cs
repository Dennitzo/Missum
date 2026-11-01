using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class DeepResearchRetrievedSourcesTests
{
    private const string SourceUrl = "https://docs.python.org/3/library/pathlib.html";
    private const string OriginalWindow = "Path.exists returns whether the path exists. Path.is_file returns whether the path is a regular file.";
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task RetainsRetrievedOriginalWindowsWithoutPromotingThemToAssessedFindings(
        bool synthesisFails, bool supportedFinding)
    {
        var task = $"Prüfe Path.exists anhand der Originalquelle {SourceUrl}.";
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(new(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", task)])], ClientCapabilities: ["coding"],
            AllowedServerTools: ["web.search", "web.fetch", "web.deepResearch"]));
        var execution = await CodingDeepResearchPipeline.ExecuteAsync(task, 1, 1, "fixture-model", 32_768, 6, 4,
            catalog.Resolve("web.search", available), catalog.Resolve("web.fetch", available),
            (request, _) =>
            {
                var name = Assert.Single(request.Tools).Name;
                if (name == CodingDeepResearchPipeline.PlanToolName)
                    return Task.FromResult(Tool(name, new { questions = Array.Empty<object>(), sourceLimit = 1 }));
                if (name == "web.fetch")
                    return Task.FromResult(Tool(name, new { url = SourceUrl, query = "exists" }));
                Assert.Equal(CodingDeepResearchPipeline.SynthesisToolName, name);
                if (synthesisFails) throw new InvalidDataException("Die Recherche-Teilphase lieferte kein gültiges JSON-Ergebnis.");
                return Task.FromResult(Tool(name, new
                {
                    summary = supportedFinding ? "Die gelesene Stelle erklärt Path.exists." : "Die fachliche Frage bleibt offen.",
                    findings = supportedFinding
                        ? new[] { new { claim = "Path.exists prüft die Existenz eines Pfads.", evidenceId = "S1-E1" } }
                        : [],
                    uncertainties = Array.Empty<string>(),
                }));
            },
            (call, _) =>
            {
                Assert.Equal("web.fetch", call.Name);
                return Task.FromResult(Result(new TargetedWebFetchResult(SourceUrl, "text/html", "found", true,
                    OriginalWindow.Length, [new("exists", 1, 0, OriginalWindow.Length, OriginalWindow)], [], null,
                    false, "Untrusted data", true, DateTimeOffset.UtcNow, [])));
            },
            (tool, arguments) => catalog.Validate(tool, arguments), static (_, _) => Task.CompletedTask);

        var result = execution.Result.Result;
        var source = Assert.Single(result.GetProperty("retrievedSources").EnumerateArray());
        Assert.Equal("S1", source.GetProperty("id").GetString());
        Assert.Equal(SourceUrl, source.GetProperty("url").GetString());
        Assert.Equal("Vom Nutzer angegebene Original-URL", source.GetProperty("title").GetString());
        Assert.Equal(OriginalWindow, Assert.Single(source.GetProperty("windows").EnumerateArray()).GetString());
        Assert.Equal("web.fetch", source.GetProperty("retrievalTool").GetString());
        Assert.Equal("unassessed", source.GetProperty("assessmentStatus").GetString());
        Assert.True(source.GetProperty("isUntrusted").GetBoolean());
        Assert.Equal(supportedFinding ? 1 : 0, result.GetProperty("findings").GetArrayLength());
        Assert.Equal(supportedFinding ? 1 : 0, result.GetProperty("sources").GetArrayLength());
        if (!supportedFinding) Assert.Equal("unresolved", result.GetProperty("conclusionStatus").GetString());
        if (synthesisFails)
        {
            Assert.False(execution.Result.Succeeded);
            Assert.Equal("web.deepResearch.incomplete", result.GetProperty("errorCode").GetString());
        }
    }

    private static LmChatResult Tool(string name, object arguments) => new(null,
        [new("fixture-call", name, JsonSerializer.SerializeToElement(arguments, Json))], 10, 5);

    private static AgentToolExecutionResult Result(object value) => new(JsonSerializer.SerializeToElement(value, Json), []);
}
