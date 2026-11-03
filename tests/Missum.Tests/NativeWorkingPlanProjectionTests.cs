using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class NativeWorkingPlanProjectionTests
{
    private static readonly string[] ExpectedPhases = ["Phase 1: Ursachen prüfen", "Phase 2: Änderung erstellen", "Phase 3: Ergebnis prüfen"];
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
        Assert.Equal("✓ Phase 1: Erster Schritt\n◌ Phase 2: Zweiter Schritt\n○ Phase 3: Dritter Schritt", blocks[0].Text);
        Assert.DoesNotContain("in_progress", blocks[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("pending", blocks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseLabelsFollowStoredOrderWithoutChangingSavedTitlesOrDuplicatingPrefixes()
    {
        var output = JsonSerializer.Deserialize<JsonElement>("""
            {"storedSteps":[
                {"id":"a","title":"Phase 0 Ursachen prüfen","status":"completed"},
                {"id":"b","title":"Phase 8: Änderung erstellen","status":"in_progress"},
                {"id":"c","title":"Phase 2: Phase 9: Ergebnis prüfen","status":"pending"}],
             "storedAcceptanceCriteria":[{"id":"ac","title":"Phase 3: Genauigkeit","status":"pending"}]}
            """);
        var first = NativeWorkingPlanProjection.Read(output);
        Assert.Equal(ExpectedPhases,
            first.Steps.Select(item => item.Title));
        Assert.Equal("Phase 3: Genauigkeit", Assert.Single(first.AcceptanceCriteria).Title);
        Assert.Equal("Phase 0 Ursachen prüfen", output.GetProperty("storedSteps")[0].GetProperty("title").GetString());

        // Coordinator DTOs are projected again by the native view.
        var dto = JsonSerializer.SerializeToElement(new { steps = first.Steps, acceptanceCriteria = first.AcceptanceCriteria },
            JsonSerializerOptions.Web);
        var projectedAgain = NativeWorkingPlanProjection.Read(dto);
        Assert.Equal(first.Steps, projectedAgain.Steps);
        Assert.Equal(first.AcceptanceCriteria, projectedAgain.AcceptanceCriteria);
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
