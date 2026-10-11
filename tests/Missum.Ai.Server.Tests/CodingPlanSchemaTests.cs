using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CodingPlanSchemaTests
{
    [Fact]
    public void FullItemHeadersCreateBothListsTogetherAndRetainUntouchedItemsOnUpdates()
    {
        var catalog = new AgentToolCatalog();
        var tool = CodingWorkingStateTools.CreateTools().Single();
        var create = JsonSerializer.Deserialize<JsonElement>("""
            {"steps":[{"id":"s1","title":"Inspect the current section","status":"in_progress"},
                      {"id":"s2","title":"Review the result","status":"pending"}],
             "acceptanceCriteria":[{"id":"a1","title":"Preserve the user's research criteria","status":"pending"}]}
            """);
        catalog.Validate(tool, create);
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Research"), create);
        Assert.Equal(2, state.Plan.Count);
        Assert.Single(state.AcceptanceCriteria);

        var update = JsonSerializer.Deserialize<JsonElement>("""
            {"steps":[{"id":"s1","title":"Inspect the current section","status":"pending"},
                      {"id":"s2","title":"Review the result","status":"in_progress"}],
             "acceptanceCriteria":[{"id":"a1","title":"Preserve the user's research criteria","status":"in_progress"}]}
            """);
        catalog.Validate(tool, update);
        var changed = CodingWorkingStateReducer.ApplyPlanUpdate(state, update);
        Assert.Equal("in_progress", changed.Plan.Single(static item => item.Id == "s2").Status);
        Assert.Equal("in_progress", Assert.Single(changed.AcceptanceCriteria).Status);
        Assert.Equal(state.AcceptanceCriteria[0].Title, changed.AcceptanceCriteria[0].Title);

        var criteriaOnly = JsonSerializer.Deserialize<JsonElement>("""
            {"acceptanceCriteria":[{"id":"a1","title":"Preserve the user's research criteria","status":"pending"}]}
            """);
        catalog.Validate(tool, criteriaOnly);
        var final = CodingWorkingStateReducer.ApplyPlanUpdate(changed, criteriaOnly);
        Assert.Equal(changed.Plan, final.Plan);
        Assert.Equal("pending", Assert.Single(final.AcceptanceCriteria).Status);
    }

    [Fact]
    public void StricterModelSchemaKeepsStoredLegacyStatusDeltasCompatible()
    {
        var catalog = new AgentToolCatalog();
        var tool = CodingWorkingStateTools.CreateTools().Single();
        var state = CodingWorkingState.Create("Research") with
        {
            Plan = [new("s1", "Inspect the current section", "pending", [])],
            AcceptanceCriteria = [new("a1", "Preserve the user's research criteria", "pending", [])],
        };
        var legacy = JsonSerializer.Deserialize<JsonElement>("""
            {"steps":[{"id":"s1","status":"in_progress"}],
             "acceptanceCriteria":[{"id":"a1","status":"in_progress"}]}
            """);
        catalog.Validate(tool, legacy);
        var changed = CodingWorkingStateReducer.ApplyPlanUpdate(state, legacy);
        Assert.Equal(state.Plan[0].Title, changed.Plan[0].Title);
        Assert.Equal(state.AcceptanceCriteria[0].Title, changed.AcceptanceCriteria[0].Title);
        Assert.Equal("in_progress", changed.Plan[0].Status);
        Assert.Equal("in_progress", changed.AcceptanceCriteria[0].Status);

        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(
            CodingWorkingState.Create("New run"), legacy));
    }
}
