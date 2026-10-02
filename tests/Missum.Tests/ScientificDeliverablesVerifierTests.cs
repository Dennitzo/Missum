using System.Security.Cryptography;
using Missum.Ai.Contracts;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class ScientificDeliverablesVerifierTests
{
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
