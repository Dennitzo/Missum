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
}
