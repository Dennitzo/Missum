using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class UnrealVisionReviewTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void ReviewRequiresAnalysisOfTheExactCurrentCapture(
        bool hasAnalysis, bool exactUpload, bool currentOperation, bool expected)
    {
        const string hash = "7871b8af2781b51837e046f95b05a2a14fdd1341f481d6389c28c9d1849482a5";
        var now = DateTimeOffset.UtcNow;
        var arguments = JsonSerializer.SerializeToElement(new
        {
            visionReview = new { verdict = "verified", operationId = "edit-1", analyzedSha256 = hash },
        });
        var steps = new List<AssistantToolStep>
        {
            new("capture", "unreal.view.capture", "completed", StartedAt: now,
                OutputJson: JsonSerializer.Serialize(new { operationId = currentOperation ? "edit-1" : "old", sha256 = hash })),
        };
        if (hasAnalysis)
        {
            steps.Add(new("analysis", "media.analyze", "completed", StartedAt: now.AddSeconds(1),
                OutputJson: JsonSerializer.Serialize(new
                {
                    analysis = "Die Szene zeigt die angeforderte Kamera.",
                    primaryUpload = new { sha256 = hash, exactRequestedUpload = exactUpload },
                })));
        }
        Assert.Equal(expected, MissumAiAssistantService.UnrealReviewHasVisionReceipt(arguments, steps, now));
    }

    [Theory]
    [InlineData("unreal.blueprint.edit")]
    [InlineData("unreal.camera.create")]
    public void LaterChangeInvalidatesAnEarlierCapture(string mutation)
    {
        const string hash = "7871b8af2781b51837e046f95b05a2a14fdd1341f481d6389c28c9d1849482a5";
        var now = DateTimeOffset.UtcNow;
        var arguments = JsonSerializer.SerializeToElement(new
        {
            visionReview = new { verdict = "verified", operationId = "edit-1", analyzedSha256 = hash },
        });
        var steps = new[]
        {
            new AssistantToolStep("capture", "unreal.view.capture", "completed", StartedAt: now,
                OutputJson: JsonSerializer.Serialize(new { operationId = "edit-1", sha256 = hash })),
            new AssistantToolStep("analysis", "media.analyze", "completed", StartedAt: now.AddSeconds(1),
                OutputJson: JsonSerializer.Serialize(new
                {
                    analysis = "Sichtprüfung abgeschlossen.",
                    primaryUpload = new { sha256 = hash, exactRequestedUpload = true },
                })),
            new AssistantToolStep("edit", mutation, "completed", StartedAt: now.AddSeconds(2)),
        };

        Assert.False(MissumAiAssistantService.UnrealReviewHasVisionReceipt(arguments, steps, now));
    }
}
