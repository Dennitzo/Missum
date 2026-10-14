using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificQuantitativeValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.001)]
    [InlineData(0.05)]
    [InlineData(0.2)]
    [InlineData(5)]
    [InlineData(1000000)]
    public void AcceptanceUsesTheExplicitProjectLimitWithoutAGeneralPercentageThreshold(double maximum)
    {
        var definition = MutableDefinition();
        definition["checks"]![0]!["maximum"] = maximum;
        var contract = Json(definition);
        Assert.Null(ScientificQuantitativeValidation.ValidateDefinition(contract));
        Assert.Empty(ScientificQuantitativeValidation.Assess(contract, Artifact(JsonSerializer.Serialize(new
        {
            metrics = new { distribution_error = maximum, refinement_error = 0.01, sampling_error = 0.1 },
        }))));
        var aboveLimit = maximum + Math.Max(maximum * 0.01, 0.001);
        var errors = ScientificQuantitativeValidation.Assess(contract, Artifact(JsonSerializer.Serialize(new
        {
            metrics = new { distribution_error = aboveLimit, refinement_error = 0.01, sampling_error = 0.1 },
        })));
        Assert.Single(errors);
        Assert.Contains("distribution_error", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void HostChecksEveryMetricInsteadOfTrustingASuccessDeclarationOrCorrelation()
    {
        var definition = Definition();
        Assert.Null(ScientificQuantitativeValidation.ValidateDefinition(definition));
        Assert.Empty(ScientificQuantitativeValidation.Assess(definition,
            Artifact("""{"metrics":{"distribution_error":0.05,"refinement_error":0.01,"sampling_error":0.1},"passed":false}""")));
        var errors = ScientificQuantitativeValidation.Assess(definition,
            Artifact("""{"metrics":{"distribution_error":0.35,"refinement_error":0.05,"sampling_error":0.15,"pearson":0.99},"passed":true}"""));
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, error => error.Contains("distribution_error", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("refinement_error", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("sampling_error", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"metrics\":{\"distribution_error\":-1,\"refinement_error\":0.01,\"sampling_error\":0.1}}")]
    [InlineData("{\"metrics\":{\"distribution_error\":1e999,\"refinement_error\":0.01,\"sampling_error\":0.1}}")]
    [InlineData("{\"metrics\":{\"distribution_error\":\"NaN\",\"refinement_error\":0.01,\"sampling_error\":0.1}}")]
    [InlineData("{\"metrics\":{\"distribution_error\":0,\"refinement_error\":0.01}}")]
    [InlineData("{\"metrics\":{\"distribution_error\":0,\"distribution_error\":0.9,\"refinement_error\":0.01,\"sampling_error\":0.1}}")]
    [InlineData("{\"metrics\":{\"distribution_error\":0},\"metrics\":{}}")]
    [InlineData("{\"metrics\":{\"distribution_error\":{\"value\":0},\"refinement_error\":0.01,\"sampling_error\":0.1}}")]
    public void MissingAmbiguousOrInvalidMeasurementsCannotPass(string artifact)
        => Assert.NotEmpty(ScientificQuantitativeValidation.Assess(Definition(), Artifact(artifact)));

    [Theory]
    [InlineData("pearson")]
    [InlineData("pearson_distance")]
    [InlineData("one_minus_correlation")]
    [InlineData("KORRELATION")]
    public void CorrelationCannotReplaceAnAccuracyMeasurement(string metric)
    {
        var definition = MutableDefinition();
        definition["checks"]![0]!["metric"] = metric;
        Assert.Contains("keine quantitative Genauigkeit", ScientificQuantitativeValidation.ValidateDefinition(Json(definition))!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("uncertainty")]
    [InlineData("convergence")]
    [InlineData("accuracy")]
    public void RequiredKindsCannotBeOmitted(string kind)
    {
        var definition = MutableDefinition();
        var checks = definition["checks"]!.AsArray();
        var matching = checks.First(item => item!["kind"]!.GetValue<string>() == kind);
        checks.Remove(matching);
        Assert.NotNull(ScientificQuantitativeValidation.ValidateDefinition(Json(definition)));
    }

    [Theory]
    [InlineData("../validation.json")]
    [InlineData("work/../validation.json")]
    [InlineData("work\\validation.json")]
    [InlineData("C:/work/validation.json")]
    [InlineData("work/validation.html")]
    [InlineData("work/validation.json\n")]
    public void OnlyBoundedRelativeJsonArtifactsArePermitted(string path)
    {
        var definition = MutableDefinition();
        definition["artifactPath"] = path;
        Assert.NotNull(ScientificQuantitativeValidation.ValidateDefinition(Json(definition)));
    }

    [Fact]
    public void NonSamplingDeterministicCalculationDoesNotNeedAnUncertaintyCriterion()
    {
        var definition = MutableDefinition();
        definition["sampling"] = false;
        definition["checks"]!.AsArray().RemoveAt(2);
        Assert.Null(ScientificQuantitativeValidation.ValidateDefinition(Json(definition)));
        Assert.Empty(ScientificQuantitativeValidation.Assess(Json(definition),
            Artifact("""{"metrics":{"distribution_error":0.05,"refinement_error":0.01}}""")));
    }

    [Theory]
    [InlineData("observable", null)]
    [InlineData("reference", null)]
    [InlineData("observable", "Eine andere Verteilung")]
    [InlineData("reference", "Eine andere Referenz")]
    public void EvenPassingMetricsMustBelongToTheExactObservableAndReference(string field, string? value)
    {
        var artifact = JsonNode.Parse(Artifact("""{"metrics":{"distribution_error":0,"refinement_error":0,"sampling_error":0}}""").GetRawText())!.AsObject();
        if (value is null) artifact.Remove(field); else artifact[field] = value;
        var errors = ScientificQuantitativeValidation.Assess(Definition(), Json(artifact));
        Assert.Single(errors);
        Assert.Contains("quantitativeValidation." + field, errors[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("weaken")]
    [InlineData("rename")]
    [InlineData("remove_check")]
    [InlineData("kind")]
    [InlineData("observable")]
    [InlineData("reference")]
    [InlineData("sampling")]
    [InlineData("optional")]
    public async Task RequiredQuantitativeGoalCannotBeRemovedOrWeakenedByMetadataPatch(string alteration)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, original) = await SeedAsync(environment);
        var definition = MutableDefinition();
        switch (alteration)
        {
            case "weaken": definition["checks"]![0]!["maximum"] = 0.5; break;
            case "rename": definition["checks"]![0]!["metric"] = "different_error"; break;
            case "remove_check": definition["checks"]!.AsArray().RemoveAt(2); break;
            case "kind": definition["checks"]![2]!["kind"] = "accuracy"; break;
            case "observable": definition["observable"] = "Andere Verteilung"; break;
            case "reference": definition["reference"] = "Neue einfache Referenz"; break;
            case "sampling": definition["sampling"] = false; break;
        }
        var patch = new JsonObject { ["quantitativeValidation"] = alteration == "remove" ? null : definition };
        if (alteration == "optional") patch["required"] = false;
        var result = await store.ApplyWorkingUpdateAsync(project, "weaken", null, null,
            [new("double-slit", "requirement", 1, Patch: Json(patch))]);
        Assert.False(result.Success);
        Assert.Contains(result.Conflicts, conflict => conflict.Code == "quantitative_goal_preserved");
        Assert.Equal(original.Revision, result.State.Revision);
        Assert.Equal(original.Items[0].Data.GetRawText(), (await store.LoadWorkingStateAsync(project)).Items[0].Data.GetRawText());
    }

    [Fact]
    public async Task StrongerChecksNewArtifactsAndNormalStatusProgressRemainPossible()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, _) = await SeedAsync(environment);
        var definition = MutableDefinition();
        definition["artifactPath"] = "artifacts/refinement-2.json";
        definition["checks"]![0]!["maximum"] = 0.01;
        definition["checks"]!.AsArray().Add(JsonNode.Parse("""{"metric":"normalization_error","kind":"accuracy","maximum":0.001}"""));
        var result = await store.ApplyWorkingUpdateAsync(project, "stricter", null, null,
            [new("double-slit", "requirement", 1, Patch: Json(new { quantitativeValidation = Json(definition), status = "unresolved", nextCheck = "Gitter verfeinern" }))]);
        Assert.True(result.Success);
        var loaded = await store.LoadWorkingStateAsync(project);
        Assert.Equal("artifacts/refinement-2.json", loaded.Items[0].Data.GetProperty("quantitativeValidation").GetProperty("artifactPath").GetString());
        Assert.Equal(4, loaded.Items[0].Data.GetProperty("quantitativeValidation").GetProperty("checks").GetArrayLength());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OmittedRequiredStillMeansRequiredAndCannotBypassTheContract(bool includeRequired)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, original) = await SeedAsync(environment, includeRequired);
        var weakened = await store.ApplyWorkingUpdateAsync(project, "optional", null, null,
            [new("double-slit", "requirement", 1, Patch: Json(new { required = false }))]);
        Assert.False(weakened.Success);
        Assert.Contains(weakened.Conflicts, conflict => conflict.Code == "quantitative_goal_preserved");
        Assert.Equal(original.Revision, weakened.State.Revision);
        var replaced = await store.ApplyWorkingUpdateAsync(project, "omit-implicit", null, null,
            [new("double-slit", "requirement", 1, Json(new { statement = "Verteilung quantitativ reproduzieren", status = "unresolved",
                method = "numeric simulation", quantitativeValidation = Definition() }))]);
        Assert.True(replaced.Success);
        Assert.False(replaced.State.Items[0].Data.TryGetProperty("required", out _));
        var removed = await store.ApplyWorkingUpdateAsync(project, "remove-after-omission", null, null,
            [new("double-slit", "requirement", 2, Patch: Json(new { quantitativeValidation = (object?)null }))]);
        Assert.False(removed.Success);
        Assert.Contains(removed.Conflicts, conflict => conflict.Code == "quantitative_goal_preserved");
    }

    private static JsonObject MutableDefinition() => JsonNode.Parse(Definition().GetRawText())!.AsObject();
    private static JsonElement Definition() => Json("""
        {"observable":"Normierte Auftreffverteilung am Schirm","reference":"Zeitintegrierter positiver Strom desselben Schirmmodells","artifactPath":"work/validation.json","sampling":true,"checks":[{"metric":"distribution_error","kind":"accuracy","maximum":0.05},{"metric":"refinement_error","kind":"convergence","maximum":0.01},{"metric":"sampling_error","kind":"uncertainty","maximum":0.1}]}
        """);
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Artifact(string metrics)
    {
        // Keep duplicate/invalid metric tokens verbatim so malformed-artifact tests exercise the real parser.
        return Json("{\"observable\":" + JsonSerializer.Serialize(Definition().GetProperty("observable").GetString())
            + ",\"reference\":" + JsonSerializer.Serialize(Definition().GetProperty("reference").GetString()) + "," + metrics[1..]);
    }

    private static async Task<(IScientificResearchStateRepository Store, string Project, ResearchWorkingState Initial)> SeedAsync(TestEnvironment environment, bool includeRequired = true)
    {
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Quantitative Prüfung", ChatMode.ClaudeScience);
        var repository = environment.Get<IScientificResearchRepository>();
        var store = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var project = "research-" + session.Id.ToString("N");
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertProjectAsync(new(project, session.Id, "scientificEvidence", "Quantitative Genauigkeit nachweisen", "Prüfung",
            "codingWorkspaceResearch", "multiPath", "active", 1, 2, now, now, environment.Directory));
        var data = JsonSerializer.SerializeToNode(new { statement = "Verteilung quantitativ reproduzieren", status = "active",
            required = true, method = "numeric simulation", quantitativeValidation = Definition() })!.AsObject();
        if (!includeRequired) data.Remove("required");
        var initial = await store.ApplyWorkingUpdateAsync(project, "seed", null, "Doppelspalt",
            [new("double-slit", "requirement", 0, Json(data))]);
        Assert.True(initial.Success);
        return (store, project, initial.State);
    }
}
