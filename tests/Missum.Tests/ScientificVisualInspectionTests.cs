using System.Text.Json;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class ScientificVisualInspectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ActualVisionReviewIsBoundToTheExactProjectImageAndInvalidatedByItsNextContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "science-visual-binding");
        var sha = new string('a', 64);
        var upload = JsonSerializer.SerializeToElement(new { uploadId = "image-1", sha256 = sha,
            sourcePath = Path.Combine(root, "work", "plot.png"), fromWorkspace = true });
        var binding = ScientificVisualInspection.UploadBinding("science", root, upload, Now)!;
        const string analysis = "Die y-Achse zeigt Randartefakte; mit Randmassen vergleichen.";
        var media = Media("image-1", sha, analysis);
        var receipt = ScientificVisualInspection.Review("science", [binding], media, Now)!;
        Assert.True(ScientificVisualInspection.HasCurrentReview("science", "work/plot.png", sha, [receipt]));
        Assert.False(ScientificVisualInspection.HasCurrentReview("science", "work/plot.png", new string('b', 64), [receipt]));
        Assert.False(ScientificVisualInspection.HasCurrentReview("foreign", "work/plot.png", sha, [receipt]));
        using var evidence = JsonDocument.Parse(receipt.EvidenceJson);
        Assert.False(evidence.RootElement.GetProperty("quantitativeValidation").GetBoolean());
    }

    [Fact]
    public void OtherUploadWindowCaptureAndHtmlCannotSatisfyTheProjectPlotInspection()
    {
        var root = Path.Combine(Path.GetTempPath(), "science-visual-binding");
        var sha = new string('a', 64);
        var binding = ScientificVisualInspection.UploadBinding("science", root, JsonSerializer.SerializeToElement(new
            { uploadId = "exact-image", sha256 = sha, sourcePath = Path.Combine(root, "work", "plot.png"), fromWorkspace = true }), Now)!;
        Assert.Null(ScientificVisualInspection.Review("science", [binding], Media("other-image", sha, "Other attachment"), Now));
        Assert.Null(ScientificVisualInspection.UploadBinding("science", root, JsonSerializer.SerializeToElement(new
            { uploadId = "capture", sha256 = sha, sourcePath = Path.Combine(root, "work", "plot.png"), fromWorkspace = false }), Now));
        Assert.Null(ScientificVisualInspection.UploadBinding("science", root, JsonSerializer.SerializeToElement(new
            { uploadId = "html", sha256 = sha, sourcePath = Path.Combine(root, "work", "simulation.html"), fromWorkspace = true }), Now));
    }

    private static JsonElement Media(string upload, string hash, string analysis) => JsonSerializer.SerializeToElement(new
    {
        visualReview = new { protocol = "missum-visual-review-v1", kind = "image", status = "analyzed", exactIdentity = true,
            sourceUploadId = upload, sourceSha256 = hash, modelId = "vision-model", analysis,
            analysisSha256 = ScientificVisualInspection.Digest(analysis), completedAt = Now },
    });
}
