using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.App.Services;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificQuantitativeDeliverablesTests
{
    private const string Observable = "Detektordichte";
    private const string Reference = "Integrierter Born-Fluss auf identischer Detektorfläche";

    [Theory]
    [InlineData(0.02, 0.01, 0.02, true)]
    [InlineData(0.05, 0.02, 0.03, true)]
    [InlineData(0.36, 0.01, 0.02, false)]
    [InlineData(0.02, 0.12, 0.02, false)]
    [InlineData(0.02, 0.01, 0.20, false)]
    public async Task CompletionRequiresMeasuredAccuracyConvergenceAndSamplingUncertainty(
        double accuracy, double convergence, double uncertainty, bool expected)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await CreateAsync(environment.Directory, accuracy, convergence, uncertainty);

        var result = await VerifyAsync(fixture);

        Assert.Equal(expected, result.GetProperty("success").GetBoolean());
        Assert.Equal(expected ? "satisfied" : "unresolved", result.GetProperty("research").GetProperty("goalStatus").GetString());
        Assert.True(result.GetProperty("publication").GetProperty("ready").GetBoolean());
        if (!expected) Assert.NotEmpty(result.GetProperty("research").GetProperty("goalMissing").EnumerateArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("foreign-output")]
    [InlineData("foreign-project")]
    [InlineData("foreign-observable")]
    [InlineData("foreign-reference")]
    [InlineData("changed-script")]
    [InlineData("missing-frozen-script")]
    [InlineData("invalid-json")]
    [InlineData("oversized")]
    [InlineData("unsafe-path")]
    [InlineData("missing-definition")]
    [InlineData("missing-metric")]
    public async Task ProcessSucceededDoesNotReplaceCurrentBoundQuantitativeEvidence(string failure)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await CreateAsync(environment.Directory);
        var experiment = fixture.Experiments[0];
        var receipt = JsonNode.Parse(experiment.StdoutEvidence)!;
        switch (failure)
        {
            case "missing": File.Delete(fixture.MetricsPath); break;
            case "changed": await File.AppendAllTextAsync(fixture.MetricsPath, " "); break;
            case "foreign-output": receipt["runs"]![0]!["outputHashes"] = new JsonObject(); break;
            case "foreign-project": experiment = experiment with { ProjectId = "research-other" }; break;
            case "changed-script": await File.AppendAllTextAsync(fixture.ScriptPath, "# changed"); break;
            case "missing-frozen-script": File.Delete(Path.Combine(fixture.Root, "snapshots", "execution-run-1", "frozen", "work", "simulation.py")); break;
            case "invalid-json":
            case "oversized":
                await File.WriteAllTextAsync(fixture.MetricsPath, failure == "invalid-json" ? "{" : new string(' ', 1024 * 1024 + 1));
                receipt["runs"]![0]!["outputHashes"]!["work/validation.json"] = await HashAsync(fixture.MetricsPath);
                break;
            case "missing-metric":
            case "foreign-observable":
            case "foreign-reference":
                var measurement = JsonNode.Parse(await File.ReadAllTextAsync(fixture.MetricsPath))!;
                if (failure == "missing-metric") measurement["metrics"]!.AsObject().Remove("convergence");
                else measurement[failure == "foreign-observable" ? "observable" : "reference"] = "Andere Messgröße beziehungsweise Referenz";
                await File.WriteAllTextAsync(fixture.MetricsPath, measurement.ToJsonString());
                receipt["runs"]![0]!["outputHashes"]!["work/validation.json"] = await HashAsync(fixture.MetricsPath);
                break;
            case "unsafe-path": fixture = ChangeRequirement(fixture, data => data["quantitativeValidation"]!["artifactPath"] = "../validation.json"); break;
            case "missing-definition": fixture = ChangeRequirement(fixture, data => data.AsObject().Remove("quantitativeValidation")); break;
        }
        fixture = fixture with { Experiments = [experiment with { StdoutEvidence = receipt.ToJsonString() }] };

        var result = await VerifyAsync(fixture);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("unresolved", result.GetProperty("research").GetProperty("goalStatus").GetString());
        Assert.Contains("req-double-slit", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneCompleteAssociatedExperimentSufficesWithoutMixingReceipts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await CreateAsync(environment.Directory);
        var valid = fixture.Experiments[0];
        var invalidReceipt = JsonNode.Parse(valid.StdoutEvidence)!;
        invalidReceipt["runs"]![0]!["outputHashes"] = new JsonObject();
        var invalid = valid with { Id = "experiment-incomplete", StdoutEvidence = invalidReceipt.ToJsonString() };
        fixture = ChangeRequirement(fixture, data => data["experimentIds"] = new JsonArray(invalid.Id, valid.Id));
        fixture = fixture with { Experiments = [invalid, valid] };

        Assert.True((await VerifyAsync(fixture)).GetProperty("success").GetBoolean());

        fixture = ChangeRequirement(fixture, data => data["experimentIds"] = new JsonArray(invalid.Id));
        var unrelated = await VerifyAsync(fixture);
        Assert.False(unrelated.GetProperty("success").GetBoolean());
        Assert.Contains("outputHashes", unrelated.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredOpenLimitRemainsUnresolvedEvenWithCurrentPublicationAndExecution()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = ChangeRequirement(await CreateAsync(environment.Directory), data =>
        {
            data["status"] = "openLimit";
            data["reason"] = "Bisher nur qualitative Übereinstimmung; quantitative Genauigkeit ist nicht erreicht.";
        });

        var result = await VerifyAsync(fixture);

        Assert.True(result.GetProperty("publication").GetProperty("ready").GetBoolean());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("unresolved", result.GetProperty("research").GetProperty("goalStatus").GetString());
        Assert.Contains("openLimit", result.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.Snapshot.Publication!.PdfPath));
    }

    [Theory]
    [InlineData("literature")]
    [InlineData("animation")]
    [InlineData("symbolic")]
    [InlineData("python symbolic")]
    [InlineData("")]
    public async Task ExistingQuantitativeContractCannotBeBypassedByChangingOrOmittingMethod(string method)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = ChangeRequirement(await CreateAsync(environment.Directory, accuracy: 0.36), data =>
        {
            if (method.Length == 0) data.AsObject().Remove("method");
            else data["method"] = method;
        });

        var result = await VerifyAsync(fixture);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("accuracy", result.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("unresolved", result.GetProperty("research").GetProperty("goalStatus").GetString());
    }

    [Theory]
    [InlineData("calculation", false)]
    [InlineData("technical", true)]
    [InlineData("definition", true)]
    public async Task UnrelatedRequiredGoalCannotHideNumericalPublicationSection(string classification, bool expected)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = ChangeRequirement(await CreateAsync(environment.Directory), data =>
        {
            data["method"] = "documentation";
            data.AsObject().Remove("quantitativeValidation");
            data.AsObject().Remove("experimentIds");
        });
        var section = fixture.State.Items[0];
        var data = JsonNode.Parse(section.Data.GetRawText())!;
        data["classification"] = classification;
        data["experimentIds"] = new JsonArray("experiment-1");
        fixture = fixture with { State = fixture.State with
            { Items = [section with { Data = JsonSerializer.SerializeToElement(data) }, fixture.State.Items[1]] } };

        var result = await VerifyAsync(fixture);

        Assert.Equal(expected, result.GetProperty("success").GetBoolean());
        if (!expected) Assert.Contains("sec-double-slit", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("symbolic", true)]
    [InlineData("sympy", true)]
    [InlineData("python symbolic", true)]
    [InlineData("numeric symbolic", false)]
    [InlineData("simulation symbolic", false)]
    [InlineData("python", false)]
    [InlineData("formal", false)]
    public async Task ExactSymbolicPythonEvidenceNeedsNoInventedNumericalConvergenceMetrics(string method, bool expected)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = ChangeRequirement(await CreateAsync(environment.Directory), data =>
        {
            data["title"] = "Identität exakt herleiten";
            data["method"] = method;
            data.AsObject().Remove("quantitativeValidation");
        });
        var section = fixture.State.Items[0];
        var sectionData = JsonNode.Parse(section.Data.GetRawText())!;
        sectionData["classification"] = "calculation";
        sectionData["experimentIds"] = new JsonArray("experiment-1");
        var experiment = fixture.Experiments[0];
        var receipt = JsonNode.Parse(experiment.StdoutEvidence)!;
        await File.WriteAllTextAsync(fixture.MetricsPath, "{\"identity\":\"(x+1)^2-x^2-2*x-1=0\",\"verified\":true}");
        receipt["runs"]![0]!["outputHashes"]!["work/validation.json"] = await HashAsync(fixture.MetricsPath);
        fixture = fixture with
        {
            State = fixture.State with { Items = [section with { Data = JsonSerializer.SerializeToElement(sectionData) }, fixture.State.Items[1]] },
            Experiments = [experiment with { StdoutEvidence = receipt.ToJsonString() }],
        };

        var result = await VerifyAsync(fixture);

        Assert.Equal(expected, result.GetProperty("success").GetBoolean());
        Assert.Equal(expected ? "satisfied" : "unresolved", result.GetProperty("research").GetProperty("goalStatus").GetString());
    }

    private static Task<JsonElement> VerifyAsync(Fixture fixture) => ScientificDeliverablesVerifier.VerifyAsync("research-test",
        fixture.Snapshot, fixture.State, new ResearchResultSnapshot([], fixture.Experiments, [], []));

    private static Fixture ChangeRequirement(Fixture fixture, Action<JsonNode> update)
    {
        var requirement = fixture.State.Items[1];
        var data = JsonNode.Parse(requirement.Data.GetRawText())!;
        update(data);
        return fixture with { State = fixture.State with
            { Items = [fixture.State.Items[0], requirement with { Data = JsonSerializer.SerializeToElement(data) }] } };
    }

    private static async Task<Fixture> CreateAsync(string directory, double accuracy = 0.02, double convergence = 0.01, double uncertainty = 0.02)
    {
        var root = Path.Combine(directory, "research-test");
        var work = Path.Combine(root, "work");
        var frozen = Path.Combine(root, "snapshots", "execution-run-1", "frozen", "work");
        var publicationDirectory = Path.Combine(root, "publications", "project-hash", "r1");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(frozen);
        Directory.CreateDirectory(publicationDirectory);
        var script = Path.Combine(work, "simulation.py");
        await File.WriteAllTextAsync(script, "# deterministic numerical validation fixture\n");
        File.Copy(script, Path.Combine(frozen, "simulation.py"));
        var metrics = Path.Combine(work, "validation.json");
        await File.WriteAllTextAsync(metrics, JsonSerializer.Serialize(new
            { observable = Observable, reference = Reference, metrics = new { accuracy, convergence, uncertainty } }));
        var scriptHash = await HashAsync(script);
        var outputHash = await HashAsync(metrics);
        var now = DateTimeOffset.UtcNow;
        var receipt = JsonSerializer.Serialize(new { runs = new[] { new { runId = "run-1", snapshotId = "execution-run-1",
            exitCode = 0, timedOut = false, executedScriptPath = "work/simulation.py", scriptSha256 = scriptHash,
            inputHashes = new Dictionary<string, string> { ["work/simulation.py"] = scriptHash },
            outputHashes = new Dictionary<string, string> { ["work/validation.json"] = outputHash } } } });
        var experiment = new ResearchExperiment("experiment-1", "research-test", "{}", "[]", "[]", "{}",
            "research.code.execute work/simulation.py", "{}", receipt, "", "[]", "ProcessSucceeded", now, now);
        var section = new ResearchWorkingItem("sec-double-slit", "section", 1, null, JsonSerializer.SerializeToElement(new
        {
            title = "Doppelspalt", contentMarkdown = "Quantitativer Vergleich im dokumentierten Geltungsbereich.",
            status = "supported", classification = "technical", reason = "Technische Beschreibung der Messmethode.",
            review = new { itemRevision = 1, sourceAssessment = "Keine externe Bestätigungsbehauptung.",
                calculationAssessment = "Numerische Akzeptanz wird unabhängig geprüft.", contradictionAssessment = "Explizite Grenzen.",
                scope = "Quantitativer Simulationsvergleich." },
        }), now);
        var requirement = new ResearchWorkingItem("req-double-slit", "requirement", 1, null, JsonSerializer.SerializeToElement(new
        {
            title = "Born-Regel quantitativ reproduzieren", method = "numeric", required = true, status = "completed",
            experimentIds = new[] { experiment.Id }, quantitativeValidation = new
            {
                observable = Observable, reference = Reference,
                artifactPath = "work/validation.json", sampling = true,
                checks = new[] { new { metric = "accuracy", kind = "accuracy", maximum = 0.05 },
                    new { metric = "convergence", kind = "convergence", maximum = 0.02 },
                    new { metric = "uncertainty", kind = "uncertainty", maximum = 0.03 } },
            },
        }), now);
        var state = new ResearchWorkingState("research-test", 1, 1, "Doppelspalt", [section, requirement], now);
        var pdf = Path.Combine(publicationDirectory, "Publikation.pdf");
        var markdown = Path.Combine(publicationDirectory, "Publikation.md");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7\n" + new string(' ', 1200));
        await File.WriteAllTextAsync(markdown, "# Doppelspalt\n\nQuantitativer Vergleich.");
        var publication = new ScientificPublicationArtifact("research-test", 1, markdown, pdf, false, now, "hash", SectionDelta: true);
        return new(root, script, metrics, state, new ScientificPresentationSnapshot(1, publication, null, null, null), [experiment]);
    }

    private static async Task<string> HashAsync(string path) => Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private sealed record Fixture(string Root, string ScriptPath, string MetricsPath, ResearchWorkingState State,
        ScientificPresentationSnapshot Snapshot, IReadOnlyList<ResearchExperiment> Experiments);
}
