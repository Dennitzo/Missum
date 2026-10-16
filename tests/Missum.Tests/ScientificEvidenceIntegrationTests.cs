using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificEvidenceIntegrationTests
{
    private const string Project = "research-evidence";
    private static readonly string[] SourceIds = ["source"];
    private static readonly string[] EvidenceIds = ["excerpt"];
    private static readonly string[] CheckIds = ["proof", "simulation-check"];
    private static readonly string[] ExperimentIds = ["lean-run", "simulation-run"];
    private static readonly string[] AllMethods = ["web", "lean", "simulation"];
    private static readonly string[] SimulationMethod = ["simulation"];
    private static readonly string[] SimulationExperimentIds = ["simulation-run"];
    private static readonly string[] AllowedAxioms = ["propext"];

    [Fact]
    public void AReviewedSourceBackedClaimDoesNotRequireOptionalLean()
    {
        var report = ScientificResearchReview.Assess(State(new
        {
            title = "Identität", contentMarkdown = "$x + x = 2x$", status = "supported", review = Review(),
            sourceIds = SourceIds, evidenceIds = EvidenceIds,
        }), Sources(), Excerpts(), [], []);
        Assert.True(report.Ready);
        Assert.DoesNotContain(report.Issues, item => item.Code == "evidence_lean_missing");
        Assert.DoesNotContain(report.Issues, item => item.Code == "evidence_web_missing");
    }

    [Fact]
    public void AFormalCompilerRunCannotStandInForSimulationOfTheEquations()
    {
        var report = ScientificResearchReview.Assess(State(new
        {
            title = "Modell", contentMarkdown = "Dokumentierter Nachweis.", status = "supported", classification = "technical",
            assumptions = "Nur formaler Satz.", review = Review(), evidenceRequirements = SimulationMethod, experimentIds = ExperimentIds,
        }), [], [], [Experiment("lean-run", "math.formalProof")], [Proof()]);
        Assert.False(report.Ready);
        Assert.Contains(report.Issues, item => item.Code == "evidence_simulation_missing");
    }

    [Fact]
    public void EachDeclaredEvidenceMethodNeedsItsOwnActualBoundEvidence()
    {
        var state = State(new
        {
            title = "Kinematik", contentMarkdown = "Die Modellgleichung wird geprüft.", status = "supported", review = Review(),
            evidenceRequirements = AllMethods, sourceIds = SourceIds, evidenceIds = EvidenceIds,
            checkIds = CheckIds, experimentIds = ExperimentIds,
            proofDescriptions = new[] { new { checkId = "proof", description = "Die Addition gleicher Terme entspricht dem doppelten Term." } },
        });
        var experiments = new[] { Experiment("lean-run", "math.formalProof"), Experiment("simulation-run", "research.code.execute") };
        var execution = new ResearchVerification("simulation-check", Project, "experiment", "simulation-run", "execution", "research.code.execute",
            "passed", experiments[1].StdoutEvidence, DateTimeOffset.UnixEpoch);
        Assert.True(ScientificResearchReview.Assess(state, Sources(), Excerpts(), experiments, [Proof(), execution]).Ready);
        Assert.Contains(ScientificResearchReview.Assess(state, Sources(), [], experiments, [Proof(), execution]).Issues,
            item => item.Code == "evidence_web_missing");
        Assert.DoesNotContain(ScientificResearchReview.Assess(state, Sources(), Excerpts(), experiments, []).Issues,
            item => item.Code == "evidence_lean_missing");
        Assert.True(ScientificResearchReview.Assess(state, Sources(), Excerpts(), experiments, [execution]).Ready);
        Assert.Contains(ScientificResearchReview.Assess(state, Sources(), Excerpts(), experiments, [Proof()]).Issues,
            item => item.Code == "evidence_simulation_missing");
        Assert.Contains(ScientificResearchReview.Assess(state, Sources(), Excerpts(), [], [Proof()]).Issues,
            item => item.Code == "evidence_simulation_missing");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("axioms")]
    [InlineData("source-hash")]
    [InlineData("statement")]
    [InlineData("initialization")]
    [InlineData("wrong-project")]
    public void LeanEvidenceRejectsUntrustedOrUnboundSuccessLabels(string defect)
    {
        var proof = Proof();
        var data = JsonNode.Parse(proof.EvidenceJson)!;
        switch (defect)
        {
            case "status": proof = proof with { Status = "KernelAccepted" }; break;
            case "axioms": data["lean"]!["proofs"]![0]!["axioms"] = new JsonArray("sorryAx"); break;
            case "source-hash": data["runs"]![0]!["inputHashes"]!["work/lean/Proof.lean"] = new string('c', 64); break;
            case "statement": data["lean"]!["proofs"] = new JsonArray(); break;
            case "initialization": data["lean"]!["operation"] = "initialize"; break;
            case "wrong-project": proof = proof with { ProjectId = "other" }; break;
        }
        proof = proof with { EvidenceJson = data.ToJsonString() };
        Assert.False(ScientificLeanEvidence.TryRead(proof, Project, out _));
    }

    [Fact]
    public void PublicationShowsExplainedRealLeanProofAtItsOwnTopic()
    {
        var state = State(new
        {
            title = "Rechenregel", contentMarkdown = "Die Aussage erklärt eine algebraische Identität.", status = "supported", order = 0,
            checkIds = CheckIds, experimentIds = ExperimentIds,
            proofDescriptions = new[] { new { checkId = "proof", description = "Sei $x\\in\\mathbb{R}$. Aus dem Distributivgesetz folgt $2x=(1+1)x=x+x$. Also gilt $x+x=2x$." } },
        });
        state = state with { Items = [state.Items[0], new("next", "section", 1, null,
            Json(new { title = "Anderes Thema", contentMarkdown = "Weitere Fragen.", order = 1 }), DateTimeOffset.UnixEpoch)] };
        var text = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [], [], []), [Proof()]);
        Assert.Contains("Aus dem Distributivgesetz folgt $2x=(1+1)x=x+x$", text);
        Assert.Contains("### Mathematischer Beweis (mit Lean geprüft)", text);
        Assert.Contains("Beweisdatei: lean/Proof.lean", text);
        Assert.Contains("Prüfbeleg: proof", text);
        Assert.DoesNotContain("∀ (x : ℝ)", text);
        Assert.DoesNotContain("double_add", text);
        Assert.DoesNotContain("```", text);
        Assert.True(text.IndexOf("### Mathematischer Beweis", StringComparison.Ordinal) < text.IndexOf("## Anderes Thema", StringComparison.Ordinal));
        Assert.Contains("Modellannahmen", text);
        var untrusted = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [], [Proof()], []));
        Assert.DoesNotContain("### Mathematischer Beweis", untrusted);
    }

    [Fact]
    public void LegacyProofWithoutExplanationRemainsReadableWithoutImposingACompletionGate()
    {
        var state = State(new { title = "Identität", contentMarkdown = "$x+x=2x$", checkIds = CheckIds, experimentIds = ExperimentIds });
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [], [], []), [Proof()]);
        Assert.Contains("$x+x=2x$", markdown);
        Assert.Contains("mathematische Ausarbeitung mit Voraussetzungen", markdown);
        Assert.Contains("hier noch zu ergänzen", markdown);
        Assert.Contains("Prüfnachweis: Lean 4.30.0", markdown);
        Assert.Contains("Beweisdatei: lean/Proof.lean", markdown);
        Assert.DoesNotContain("∀ (x : ℝ)", markdown);
        Assert.DoesNotContain("double_add", markdown);
        Assert.DoesNotContain("Arbeitsfassung", markdown);
        Assert.DoesNotContain("```", markdown);
    }

    [Theory]
    [InlineData("Dieser Satz beschreibt das Experiment.")]
    [InlineData("Eine numerische Rechnung ist kein Beweis.")]
    [InlineData("Für diese Hypothese liegt kein Lemma vor.")]
    [InlineData("Die Messung ergab $r=0.93$.")]
    [InlineData("Die Abweichung beträgt $\\delta=9,44°$.")]
    [InlineData("Es wurde $p<0.01$ gemessen.")]
    public void ProseAndMeasuredScalarsDoNotImposeAnUnrelatedLeanProof(string contentMarkdown)
    {
        var item = State(new { title = "Beobachtung", contentMarkdown, status = "supported" }).Items[0];
        Assert.DoesNotContain("lean", ScientificEvidenceRequirements.For(item));
    }

    [Theory]
    [InlineData("$x+x=2x$")]
    [InlineData("\\[a(b+c)=ab+ac\\]")]
    [InlineData("$$x+x=2$$")]
    public void ActualSymbolicAssertionsDoNotRequireLean(string contentMarkdown)
    {
        var item = State(new { title = "Algebra", contentMarkdown, status = "supported" }).Items[0];
        Assert.DoesNotContain("lean", ScientificEvidenceRequirements.For(item));
    }

    [Fact]
    public void LegacyAssignedPlotStillRendersAtItsTopicWithoutRequiringNewStateMetadata()
    {
        var state = State(new { title = "Bewegung", contentMarkdown = "Der vorhandene Modellstand bleibt lesbar.",
            status = "supported", experimentIds = SimulationExperimentIds });
        var experiment = Experiment("simulation-run", "research.code.execute") with
        {
            StdoutEvidence = JsonSerializer.Serialize(new { runs = new[] { new { runId = "measured", snapshotId = "current",
                exitCode = 0, inputHashes = new Dictionary<string, string> { ["work/model.py"] = new('a', 64) },
                outputHashes = new Dictionary<string, string> { ["artifacts/model.png"] = new('b', 64) } } } }),
        };
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [experiment], [], []));
        Assert.Contains("Der vorhandene Modellstand bleibt lesbar", markdown);
        Assert.Contains("artifacts/model.png", markdown);
        Assert.DoesNotContain("Arbeitsfassung", markdown);
        Assert.DoesNotContain("## Simulationsabbildungen", markdown);
    }

    [Fact]
    public void OldReviewPolicyCannotCertifyTheNewEvidenceContract()
    {
        var state = State(new { title = "Definition", contentMarkdown = "Eine Definition.", status = "completed",
            classification = "definition", assumptions = "Definitorisch.", review = Review() });
        var report = ScientificResearchReview.Assess(state, [], [], [], []);
        var node = JsonSerializer.SerializeToNode(report, JsonSerializerOptions.Web)!;
        var receipt = new ResearchVerification("review", Project, "publication-review", Project, "review", ScientificResearchReview.Method,
            "ReviewedWithEvidence", node.ToJsonString(), DateTimeOffset.UnixEpoch);
        Assert.True(ScientificResearchReview.HasCurrentReview(state, [receipt]));
        node.AsObject().Remove("evidencePolicy");
        Assert.False(ScientificResearchReview.HasCurrentReview(state, [receipt with { EvidenceJson = node.ToJsonString() }]));
    }

    [Fact]
    public async Task SimulationExplanationPatchPersistsAndInvalidatesThePublication()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Evidenz", ChatMode.ClaudeScience);
        var repository = environment.Get<IScientificResearchRepository>();
        var states = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var now = DateTimeOffset.UtcNow;
        var projectId = "research-" + session.Id.ToString("N");
        await repository.UpsertProjectAsync(new(projectId, session.Id, "scientificEvidence", "Prüfe das Modell", "Modell",
            "codingWorkspaceResearch", "multiPath", "active", 2, 1, now, now, environment.Directory));
        await repository.SaveExperimentAsync(Experiment("simulation-run", "research.code.execute") with { ProjectId = projectId });
        var data = Json(new
        {
            title = "Bewegung", contentMarkdown = "Die Gleichung beschreibt den ausgewählten Modellfall.", status = "draft",
            experimentIds = SimulationExperimentIds, evidenceRequirements = SimulationMethod,
            simulationDescriptions = new[] { new { experimentId = "simulation-run", equations = "$x(t)=t$", description = "Erste Einordnung." } },
        });
        var stored = await states.ApplyWorkingUpdateAsync(projectId, "create", null, "Modell", [new("section", "section", 0, data)]);
        Assert.True(stored.Success);
        var changed = await states.ApplyWorkingUpdateAsync(projectId, "explain", null, null, [new("section", "section", 1,
            Patch: Json(new { simulationDescriptions = new[] { new { experimentId = "simulation-run", equations = "$x(t)=t$",
                description = "Die konstante Geschwindigkeit reproduziert die analytische Gerade im betrachteten Bereich." } } }))]);
        Assert.True(changed.Success);
        Assert.Equal(stored.State.PublicationRevision + 1, changed.State.PublicationRevision);
        var loaded = await states.LoadWorkingStateAsync(projectId);
        Assert.Contains("analytische Gerade", loaded.Items[0].Data.GetProperty("simulationDescriptions")[0].GetProperty("description").GetString());
        Assert.Equal("simulation", loaded.Items[0].Data.GetProperty("evidenceRequirements")[0].GetString());
    }

    private static ResearchWorkingState State(object data) => new(Project, 1, 1, "Nachweisprüfung",
        [new("section", "section", 1, null, Json(data), DateTimeOffset.UnixEpoch)], DateTimeOffset.UnixEpoch);
    private static object Review() => new { itemRevision = 1, sourceAssessment = "Originalquelle verglichen.",
        calculationAssessment = "Passende Rechnungen geprüft.", contradictionAssessment = "Widersprüche abgeglichen.", scope = "Nur das Modell." };
    private static ResearchLiteratureEntry[] Sources() => [new("source", Project, "Originalarbeit", "https://example.org/paper", "published", "{}", "included", "fullTextExcerpt", DateTimeOffset.UnixEpoch)];
    private static ResearchEvidenceRecord[] Excerpts() => [new("excerpt", Project, "source", "Originaler Vergleich.", "Aussage", new string('a', 64), "fullTextExcerpt", "{}", DateTimeOffset.UnixEpoch)];
    private static ResearchExperiment Experiment(string id, string command) => new(id, Project, "", "[]", "[]", "{}", command, "{}",
        JsonSerializer.Serialize(new { runs = new[] { new { runId = "measured", exitCode = 0, inputHashes = new { }, outputHashes = new { } } } }),
        "", "[]", "ProcessSucceeded", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private static ResearchVerification Proof() => new("proof", Project, "experiment", "lean-run", "formal", "math.formalProof", "passed", JsonSerializer.Serialize(new
    {
        projectId = Project, success = true, formalVerification = "KernelAccepted",
        lean = new { success = true, operation = "check", formalVerification = "KernelAccepted", axiomAudit = "passed", exitCode = 0,
            unexpectedAxioms = Array.Empty<string>(), source = "lean/Proof.lean", sourceFileSha256 = new string('a', 64),
            checkedSource = "lean/Audit.lean", checkedSourceSha256 = new string('b', 64), leanVersion = "4.30.0",
            proofs = new[] { new { declaration = "double_add", statement = "∀ (x : ℝ), x + x = 2 * x", axioms = AllowedAxioms } } },
        runs = new[] { new { runId = "real", snapshotId = "measured", exitCode = 0, inputHashes = new Dictionary<string, string>
            { ["work/lean/Proof.lean"] = new('a', 64), ["work/lean/Audit.lean"] = new('b', 64) } } },
    }), DateTimeOffset.UnixEpoch);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
