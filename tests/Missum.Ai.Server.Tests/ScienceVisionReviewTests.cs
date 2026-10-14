using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceVisionReviewTests
{
    [Fact]
    public void SuccessfulExactImageReviewBindsSourceModelAndActualAnalysis()
    {
        const string analysis = "Die y-Achse ist abgeschnitten. Prüfe die gespeicherten Randmassen.";
        var upload = Upload();
        var receipt = AgentToolExecutor.CreateVisualReviewReceipt(upload, upload.UploadId, "coding/DeepSeek~actual", analysis);
        var value = Assert.IsType<JsonElement>(receipt);
        Assert.Equal("missum-visual-review-v1", value.GetProperty("protocol").GetString());
        Assert.Equal(upload.UploadId, value.GetProperty("sourceUploadId").GetString());
        Assert.Equal(upload.Sha256, value.GetProperty("sourceSha256").GetString());
        Assert.Equal("coding/DeepSeek~actual", value.GetProperty("modelId").GetString());
        Assert.Equal(analysis, value.GetProperty("analysis").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(analysis))),
            value.GetProperty("analysisSha256").GetString());
        Assert.False(value.GetProperty("quantitativeValidation").GetBoolean());
    }

    [Fact]
    public void FallbackToDifferentUploadNeverCreatesAPlotReviewReceipt()
    {
        Assert.Null(AgentToolExecutor.CreateVisualReviewReceipt(Upload(), "upload-other", "vision/model", "Sieht korrekt aus."));
    }

    [Theory]
    [InlineData("video/mp4", "a", "Analyse", "model")]
    [InlineData("image/png", "a", "", "model")]
    [InlineData("image/png", "a", "Analyse", "")]
    [InlineData("image/png", "bad", "Analyse", "model")]
    public void NonimageEmptyOrUnboundAnalysisIsNotAReview(string mediaType, string hash, string analysis, string model)
    {
        var upload = Upload() with { MediaType = mediaType, Sha256 = hash == "a" ? new string('a', 64) : hash };
        Assert.Null(AgentToolExecutor.CreateVisualReviewReceipt(upload, upload.UploadId, model, analysis));
    }

    [Fact]
    public void PlotPromptUsesScientificDiagnosticsWithoutMandatoryObjectGeometry()
    {
        var prompt = ModelRuntimeClient.VisionSystemInstructions("Prüfe Achsen, Einheiten und Trajektorien dieses Doppelspalt-Plots.");
        Assert.Contains("Wissenschaftliche Plotprüfung", prompt, StringComparison.Ordinal);
        Assert.Contains("Rohdaten-/Codeprüfung", prompt, StringComparison.Ordinal);
        Assert.Contains("keine quantitative Abnahme", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Kopfbreite", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Kurze Pauschalurteile sind unzulässig", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryImageKeepsTaskSpecificVisualInstructions()
    {
        var prompt = ModelRuntimeClient.VisionSystemInstructions("Beschreibe die Hausfassade und deren sichtbare Schäden.");
        Assert.DoesNotContain("Wissenschaftliche Plotprüfung", prompt, StringComparison.Ordinal);
        Assert.Contains("konkreten Prüffrage", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Kopfbreite", prompt, StringComparison.Ordinal);
    }

    private static UploadCompleted Upload() => new("upload-0123456789abcdef0123456789abcdef", "plot.png", "image/png",
        128, new string('a', 64), DateTimeOffset.UtcNow.AddHours(1));
}
