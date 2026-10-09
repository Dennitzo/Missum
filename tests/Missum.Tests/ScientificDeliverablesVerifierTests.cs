using System.Security.Cryptography;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificDeliverablesVerifierTests
{
    [Theory]
    [InlineData("animation", "Interaktive Simulation", true)]
    [InlineData("simulation", "Echtzeit-Simulation", true)]
    [InlineData("simulation", "Numerischer Prüfplot", false)]
    [InlineData("python numeric simulation", "Echtzeit-Simulation", false)]
    [InlineData("formalproof simulation", "Interaktive Simulation", false)]
    public async Task InteractiveSourceSatisfiesOnlyExplicitTechnicalDeliveryAndNeverScientificExecution(
        string method, string title, bool expectedReady)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (publication, _) = await WriteDeliverablesAsync(environment.Directory);
        var artifact = await WriteInteractiveArtifactAsync(environment.Directory);
        var state = InteractiveState(method, title);
        var snapshot = new ScientificPresentationSnapshot(1, publication with { SectionDelta = true },
            new("research-test", 1, "ready", "", [artifact], DateTimeOffset.UtcNow), null, null);

        var result = await ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot, state, null);

        Assert.Equal(expectedReady, result.GetProperty("success").GetBoolean());
        Assert.False(result.GetProperty("simulation").GetProperty("executed").GetBoolean());
        Assert.True(result.GetProperty("simulation").GetProperty("interactiveReady").GetBoolean());
        var verified = Assert.Single(result.GetProperty("simulation").GetProperty("interactiveArtifacts").EnumerateArray());
        Assert.Equal(artifact.Sha256, verified.GetProperty("sha256").GetString());
        Assert.True(verified.GetProperty("sourceArtifact").GetBoolean());
        Assert.False(verified.GetProperty("executed").GetBoolean());
        Assert.Empty(result.GetProperty("simulation").GetProperty("artifacts").EnumerateArray());
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("scope")]
    [InlineData("structure")]
    public async Task InteractiveDeliveryRejectsChangedForeignOrNoninteractiveSource(string failure)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (publication, _) = await WriteDeliverablesAsync(environment.Directory);
        var artifact = await WriteInteractiveArtifactAsync(environment.Directory);
        if (failure == "changed") await File.AppendAllTextAsync(artifact.ImagePath, "<!-- changed -->");
        if (failure == "scope") artifact = artifact with { ProjectRoot = environment.Directory };
        if (failure == "structure")
        {
            await File.WriteAllTextAsync(artifact.ImagePath, "<!doctype html><html><script>let t=1</script><p>Keine Simulation</p></html>");
            artifact = artifact with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(artifact.ImagePath))) };
        }
        var snapshot = new ScientificPresentationSnapshot(1, publication with { SectionDelta = true },
            new("research-test", 1, "ready", "", [artifact], DateTimeOffset.UtcNow), null, null);
        var result = await ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot,
            InteractiveState("animation", "Echtzeit-Simulation"), null);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.False(result.GetProperty("simulation").GetProperty("interactiveReady").GetBoolean());
        Assert.Empty(result.GetProperty("simulation").GetProperty("interactiveArtifacts").EnumerateArray());
    }

    private static ResearchWorkingState InteractiveState(string method, string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new("research-test", 1, 1, "Erdmagnetfeld",
             [new("grundlagen", "section", 1, null, JsonSerializer.SerializeToElement(new
                { title = "Modellannahmen", contentMarkdown = "Explizit hypothetisches Modell.", status = "openLimit",
                    classification = "hypothesis", reason = "Keine Aussage über empirische Bestätigung.",
                    review = new { itemRevision = 1, sourceAssessment = "Als hypothetisches Modell ohne externe Bestätigungsbehauptung geprüft.",
                        calculationAssessment = "Keine numerischen oder formalen Schlussfolgerungen behauptet.",
                        contradictionAssessment = "Der illustrative Geltungsbereich ist ausdrücklich begrenzt.", scope = "Technische Animation, keine bestätigte Theorie." } }), now),
             new("simulation", "requirement", 1, null, JsonSerializer.SerializeToElement(new
                { title, method, required = true, status = "completed" }), now)], now);
    }

    private static async Task<ScientificSimulationArtifact> WriteInteractiveArtifactAsync(string directory)
    {
        var root = Path.Combine(directory, "research-test");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        var path = Path.Combine(work, "animation.html");
        await File.WriteAllTextAsync(path, "<!doctype html><html><canvas></canvas><script>let t=0;requestAnimationFrame(()=>t++);</script></html>");
        return new("html", "Echtzeit", path, path, null, "Interaktive Simulation · Projektdatei", false,
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))),
            Kind: "interactive", ContentType: "text/html", ProjectRoot: root);
    }

    [Fact]
    public async Task MissingFilesAndEvidenceOverviewDoNotCompleteResearch()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var empty = await ScientificDeliverablesVerifier.VerifyAsync("research-test", null);
        Assert.False(empty.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(empty.GetProperty("publication").GetProperty("error").GetString()));
        var (publication, image) = await WriteDeliverablesAsync(environment.Directory);
        var snapshot = new ScientificPresentationSnapshot(1, publication,
            new("research-test", 1, "ready", "", [image with { Provenance = "Evidenzbasis der Publikationen" }], DateTimeOffset.UtcNow), null, null);
        var overview = await ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot);
        Assert.True(overview.GetProperty("publication").GetProperty("ready").GetBoolean());
        Assert.False(overview.GetProperty("success").GetBoolean());
        Assert.Contains("zugeordnet", overview.GetProperty("simulation").GetProperty("error").GetString(), StringComparison.Ordinal);
        File.Delete(publication.PdfPath);
        var missing = await ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot);
        Assert.False(missing.GetProperty("publication").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task RealCurrentFilesAreVerifiedAndChangedImagesAreRejected()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (publication, image) = await WriteDeliverablesAsync(environment.Directory);
        var snapshot = new ScientificPresentationSnapshot(1, publication,
            new("research-test", 1, "ready", "", [image], DateTimeOffset.UtcNow), null, null);
        var verified = await ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot);
        Assert.True(verified.GetProperty("success").GetBoolean());
        Assert.True(verified.GetProperty("simulation").GetProperty("executed").GetBoolean());
        Assert.Equal(image.Sha256, verified.GetProperty("simulation").GetProperty("artifacts")[0].GetProperty("sha256").GetString());
        await File.AppendAllTextAsync(image.ImagePath, "changed");
        var changed = await ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot);
        Assert.False(changed.GetProperty("success").GetBoolean());
        Assert.Contains("SHA-256", changed.GetProperty("simulation").GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(changed.GetProperty("simulation").GetProperty("artifacts").EnumerateArray());
        var other = await ScientificDeliverablesVerifier.VerifyAsync("research-other", snapshot);
        Assert.False(other.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RendererErrorsAndCancellationCannotBeReportedAsSuccess()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (publication, image) = await WriteDeliverablesAsync(environment.Directory);
        var snapshot = new ScientificPresentationSnapshot(1, publication,
            new("research-test", 1, "ready", "", [image], DateTimeOffset.UtcNow), "PDF konnte nicht gerendert werden", null);
        var result = await ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot);
        Assert.False(result.GetProperty("success").GetBoolean());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScientificDeliverablesVerifier.VerifyAsync("research-test", snapshot with { PublicationError = null }, cancelled.Token));
    }

    [Fact]
    public void VerificationProposalIsReadOnlyAndRejectsExtraArguments()
    {
        var proposal = new ToolProposal("proposal-test", "run-test", ClientToolNames.ResearchDeliverablesVerify,
            System.Text.Json.JsonSerializer.SerializeToElement(new { projectId = "research-test" }),
            ToolRiskClass.ReadOnly, "Forschungsergebnisse prüfen", DateTimeOffset.UtcNow.AddMinutes(1));
        LocalToolBroker.ValidateProposal(proposal);
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(proposal with { RiskClass = ToolRiskClass.Process }));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(proposal with
        { Arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { projectId = "research-test", path = "unrelated.pdf" }) }));
    }

    private static async Task<(ScientificPublicationArtifact Publication, ScientificSimulationArtifact Image)> WriteDeliverablesAsync(string root)
    {
        var pdf = Path.Combine(root, "Publikation.pdf");
        var markdown = Path.Combine(root, "Publikation.md");
        var imagePath = Path.Combine(root, "result.png");
        var script = Path.Combine(root, "simulation.py");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7\n" + new string(' ', 1200));
        await File.WriteAllTextAsync(markdown, "# Fachlicher Titel\n\n## Ergebnisse\nNachvollziehbare Berechnung.");
        await File.WriteAllTextAsync(script, "import matplotlib.pyplot as plt\nplt.plot([0, 1])\n");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lZsAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(imagePath, png);
        return (new("research-test", 1, markdown, pdf, true, DateTimeOffset.UtcNow, "content-hash"),
            new("plot", "Python-Ergebnis", imagePath, script, null, "Forschungsexperiment · experiment-1 · ProcessSucceeded", true,
                Convert.ToHexStringLower(SHA256.HashData(png))));
    }
}
