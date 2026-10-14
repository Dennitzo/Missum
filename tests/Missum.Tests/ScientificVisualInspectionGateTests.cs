using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificVisualInspectionGateTests
{
    [Fact]
    public async Task NativeCompletionRequiresAnActualReviewOfTheCurrentGeneratedPlot()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var work = Path.Combine(environment.Directory, "work"); Directory.CreateDirectory(work);
        var pdf = Path.Combine(environment.Directory, "publication.pdf");
        var source = Path.Combine(environment.Directory, "publication.md");
        var script = Path.Combine(work, "plot.py");
        var png = Path.Combine(work, "plot.png");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7\n" + new string(' ', 1200));
        await File.WriteAllTextAsync(source, "# Ergebnis");
        await File.WriteAllTextAsync(script, "print('plot')");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lZsAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(png, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var publication = new ScientificPublicationArtifact("science", 1, source, pdf, true, DateTimeOffset.UtcNow, "source");
        var artifact = new ScientificSimulationArtifact("plot", "Plot", png, script, null,
            "Forschungsexperiment · exp-1 · ProcessSucceeded", true, hash, ProjectRoot: environment.Directory);
        var snapshot = new ScientificPresentationSnapshot(1, publication,
            new("science", 1, "ready", "", [artifact], DateTimeOffset.UtcNow), null, null);
        var missing = await ScientificDeliverablesVerifier.VerifyWithVisualInspectionAsync("science", snapshot, null, null, [], [], true);
        Assert.False(missing.GetProperty("success").GetBoolean());
        Assert.Contains("Vision", missing.GetProperty("message").GetString());
        var upload = ScientificVisualInspection.UploadBinding("science", environment.Directory, JsonSerializer.SerializeToElement(new
            { uploadId = "plot-image", sha256 = hash, sourcePath = png, fromWorkspace = true }), DateTimeOffset.UtcNow)!;
        const string analysis = "Achsen und Randverhalten mit den Rohdaten prüfen.";
        var receipt = ScientificVisualInspection.Review("science", [upload], JsonSerializer.SerializeToElement(new
        {
            visualReview = new { protocol = "missum-visual-review-v1", kind = "image", status = "analyzed", exactIdentity = true,
                sourceUploadId = "plot-image", sourceSha256 = hash, modelId = "vision", analysis,
                analysisSha256 = ScientificVisualInspection.Digest(analysis), completedAt = DateTimeOffset.UtcNow },
        }), DateTimeOffset.UtcNow)!;
        var recorded = new ResearchResultSnapshot([], [], [receipt], []);
        var inspected = await ScientificDeliverablesVerifier.VerifyWithVisualInspectionAsync("science", snapshot, null, recorded, [], [], true);
        Assert.True(inspected.GetProperty("success").GetBoolean());

        // Legacy manifests without a root must fail closed in the mandatory
        // native path; callers explicitly opting out retain legacy behavior.
        var unscoped = snapshot with { Simulation = snapshot.Simulation! with { Artifacts = [artifact with { ProjectRoot = null }] } };
        var unsupportedScope = await ScientificDeliverablesVerifier.VerifyWithVisualInspectionAsync("science", unscoped, null, recorded, [], [], true);
        Assert.False(unsupportedScope.GetProperty("success").GetBoolean());
        Assert.Contains("Projektzuordnung", unsupportedScope.GetProperty("message").GetString());
        var legacy = await ScientificDeliverablesVerifier.VerifyAsync("science", unscoped);
        Assert.True(legacy.GetProperty("success").GetBoolean());

        var scriptHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(script)));
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(png));
        var execution = new ScientificExecutionEvidence(environment.Directory, "exp-1", "measured-plot", "work/plot.py",
            scriptHash, "current-measured-plot", new Dictionary<string, string> { ["work/plot.py"] = scriptHash },
            new Dictionary<string, string> { ["work/plot.png"] = hash }, modified.AddSeconds(-1), modified.AddSeconds(1));
        var recoverable = snapshot with { Simulation = snapshot.Simulation! with
            { Artifacts = [artifact with { ProjectRoot = null, Execution = execution }] } };
        var executionScope = await ScientificDeliverablesVerifier.VerifyWithVisualInspectionAsync("science",
            recoverable, null, recorded, [], [], true);
        Assert.True(executionScope.GetProperty("success").GetBoolean());
        var missingExecutionReview = await ScientificDeliverablesVerifier.VerifyWithVisualInspectionAsync("science",
            recoverable, null, null, [], [], true);
        Assert.False(missingExecutionReview.GetProperty("success").GetBoolean());
        Assert.Contains("work/plot.png", missingExecutionReview.GetProperty("message").GetString());
    }
}
