using System.Text.Json;
using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativeContinuationPresentationTests
{
    [Theory]
    [InlineData("completed", "Lauf fortgesetzt")]
    [InlineData("failed", "Fortsetzen fehlgeschlagen")]
    [InlineData("denied", "Fortsetzen fehlgeschlagen")]
    [InlineData("cancelled", "Fortsetzung abgebrochen")]
    [InlineData("interrupted", "Fortsetzung abgebrochen")]
    [InlineData("pending", "Fortsetzung wird vorbereitet")]
    [InlineData("running", "Fortsetzung wird vorbereitet")]
    public void ReceiptIsAPlainStatusIndependentOfLazyDetails(string status, string expected)
    {
        var step = JsonSerializer.SerializeToElement(new
        {
            id = "resume-1",
            tool = "assistant.continuation",
            status,
            detailsDeferred = true,
            detail = new string('x', 100_000),
            inputJson = "{\"request\":\"Private historical content\"}",
            outputJson = "{\"message\":\"Private historical content\"}",
        });

        Assert.Equal(expected, NativeAssistantPage.ContinuationReceiptLabel(step));
    }

    [Fact]
    public void MissingStatusNeverClaimsAnAcceptedContinuation()
    {
        var step = JsonSerializer.SerializeToElement(new { tool = "assistant.continuation", detail = "Lauf fortgesetzt" });
        Assert.Equal("Fortsetzung wird vorbereitet", NativeAssistantPage.ContinuationReceiptLabel(step));
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("cancelled", false)]
    [InlineData("cancelled", true)]
    [InlineData("interrupted", false)]
    [InlineData("interrupted", true)]
    public void ManualCompactionReceiptIsNeverAnAiContinuationEvenWithDeferredDetails(string status, bool deferred)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            id = Guid.NewGuid(), role = "assistant", status, content = "", error = "Missum AI Server returned HTTP 404.",
            toolSteps = new[] { new { id = "manual-compaction:fixture", tool = "context.compact", status,
                detailsDeferred = deferred } },
        });

        Assert.False(NativeAssistantPage.IsResumableAssistantStatus(message));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    public void RealAiWorkWithAutomaticCompactionRemainsResumable(string status)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            id = Guid.NewGuid(), role = "assistant", status, content = "Bereits überprüfte Herleitung.",
            toolSteps = new[] { new { id = "context-compaction:run:42", tool = "context.compact", status,
                detailsDeferred = true } },
        });

        Assert.True(NativeAssistantPage.IsResumableAssistantStatus(message));
    }
}
