using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class NativeWorkingPlanProjectionTests
{
    [Fact]
    public void CanonicalSavedListsWinOverTheSingleChangedRequestItem()
    {
        var input = JsonSerializer.SerializeToElement(new { steps = new[] { new { id = "b", title = "Zweiter Schritt", status = "in_progress" } } });
        var output = JsonSerializer.SerializeToElement(new
        {
            storedSteps = new[] { new { id = "a", title = "Erster Schritt", status = "completed" }, new { id = "b", title = "Zweiter Schritt", status = "in_progress" }, new { id = "c", title = "Dritter Schritt", status = "pending" } },
            storedAcceptanceCriteria = new[] { new { id = "check", title = "Ergebnis prüfen", status = "pending" } },
        });
        var plan = NativeWorkingPlanProjection.Read(output, input);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal("a", plan.Steps[0].Id);
        Assert.Equal("completed", plan.Steps[0].Status);
        Assert.Equal("Ergebnis prüfen", Assert.Single(plan.AcceptanceCriteria).Title);
        var blocks = NativeToolResultFormatter.Receipt("coding.updatePlan", input, output, "completed").ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Equal("✓ Erster Schritt\n◌ Zweiter Schritt\n○ Dritter Schritt", blocks[0].Text);
        Assert.DoesNotContain("in_progress", blocks[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("pending", blocks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySavedPlanIsNotReplacedByStaleInput()
    {
        var plan = NativeWorkingPlanProjection.Read(JsonSerializer.Deserialize<JsonElement>("""{"storedSteps":[],"storedAcceptanceCriteria":[]}"""),
            JsonSerializer.Deserialize<JsonElement>("""{"steps":[{"id":"old","title":"Veraltet","status":"pending"}]}"""));
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.AcceptanceCriteria);
    }

    [Fact]
    public void LegacyInputIsReadableAndMalformedOrUnrelatedReceiptsStayEmpty()
    {
        var legacy = new AssistantToolStep("plan", "coding.updatePlan", "completed", InputJson:
            """{"steps":[{"id":"a","title":"Erster Schritt","status":"complete"},{"id":"b","title":"Zweiter Schritt","status":"pending"}]}""", OutputJson: "{broken}");
        var plan = NativeWorkingPlanProjection.FromToolStep(legacy);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("completed", plan.Steps[0].Status);
        Assert.Empty(NativeWorkingPlanProjection.FromToolStep(legacy with { Tool = "coding.read" }).Steps);
        Assert.Empty(NativeWorkingPlanProjection.FromToolStep(null).Steps);
    }
}
