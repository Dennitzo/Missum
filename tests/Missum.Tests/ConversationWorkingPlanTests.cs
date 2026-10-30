using System.Text.Json;
using Microsoft.Data.Sqlite;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ConversationWorkingPlanTests
{
    private static readonly string[] PendingScientificFields = ["question", "strategy"];

    [Fact]
    public async Task ProvisionalSciencePlanSurvivesAFailedUpdateAndReloadsAsTheCompleteCanonicalPlan()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Vorläufiger Science-Plan", ChatMode.ClaudeScience);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var accepted = Plan("initial", new { steps = new[] { new { id = "a", title = "Gezielt lesen" }, new { id = "b", title = "Gegenprüfen" } } }, new
        {
            success = true,
            storedSteps = new[] { new { id = "a", title = "Gezielt lesen", status = "in_progress" }, new { id = "b", title = "Gegenprüfen", status = "pending" } },
            storedAcceptanceCriteria = Array.Empty<object>(),
            scienceStages = new[] { new { id = "a", question = "", strategy = "", detailsPending = true } },
            pendingScienceDetails = new[] { new { id = "a", missingFields = PendingScientificFields } },
        });
        await chats.SaveToolStepAsync(message.Id, accepted);
        await chats.SaveToolStepAsync(message.Id, Plan("invalid-close", new { steps = new[] { new { id = "a", status = "completed" } } },
            new { success = false, error = "Wissenschaftliche Details und Nachweise fehlen." }) with { Status = "failed" });

        var stored = (await snapshots.GetWorkingPlanAsync(session.Id))!;
        var current = NativeWorkingPlanProjection.FromToolStep(stored);
        Assert.Equal(accepted.Id, stored.Id);
        Assert.Equal(["Gezielt lesen", "Gegenprüfen"], current.Steps.Select(step => step.Title));
        Assert.Equal(["in_progress", "pending"], current.Steps.Select(step => step.Status));
        Assert.Empty(current.AcceptanceCriteria);
        Assert.Null(stored.InputJson);
        Assert.DoesNotContain("Wissenschaftliche Details und Nachweise fehlen", stored.OutputJson, StringComparison.Ordinal);
        var reloaded = (await snapshots.GetWorkingPlanAsync(session.Id))!;
        Assert.Equal(stored.OutputJson, reloaded.OutputJson);
        Assert.Equal(stored.OutputJson,
            (await snapshots.GetWorkingPlanAtAsync(session.Id, message.Id, "invalid-close"))!.OutputJson);
    }

    [Fact]
    public async Task CurrentPlanLoadsOutsideTheLazyHistoryWindowWithoutOtherToolPayloads()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Langer Verlauf");
        var first = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Plan", MessageStatus.Completed);
        await chats.SaveToolStepAsync(first.Id, Plan("plan", new { steps = new[] { new { id = "b", title = "Zweiter Schritt", status = "in_progress" } } }, new
        {
            success = true,
            storedSteps = new[] { new { id = "a", title = "Erster Schritt", status = "completed" }, new { id = "b", title = "Zweiter Schritt", status = "in_progress" }, new { id = "c", title = "Dritter Schritt", status = "pending" } },
            storedAcceptanceCriteria = new[] { new { id = "check", title = "Gegenprüfung", status = "pending" } },
        }) with { Detail = new string('d', 100_000) });
        for (var index = 0; index < 30; index++)
            await chats.AddMessageAsync(session.Id, ChatRole.User, "Spätere Nachricht " + index, MessageStatus.Completed);
        var last = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Späterer Lauf", MessageStatus.Completed);
        await chats.SaveToolStepAsync(last.Id, new("read", "coding.read", "completed", Detail: new string('x', 500_000), OutputJson: "{\"content\":\"kein Plan\"}"));

        var page = (await snapshots.GetPageAsync(session.Id))!;
        Assert.DoesNotContain(page.Conversation.Messages, message => message.Id == first.Id);
        var savedRevision = (await chats.GetSessionAsync(session.Id))!.ConversationRevision;
        var receipt = (await snapshots.GetWorkingPlanAsync(session.Id))!;
        var plan = NativeWorkingPlanProjection.FromToolStep(receipt);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Single(plan.AcceptanceCriteria);
        Assert.Null(receipt.Detail);
        Assert.Null(receipt.InputJson);
        Assert.True(receipt.OutputJson!.Length < 1000);
        Assert.Equal(savedRevision, (await chats.GetSessionAsync(session.Id))!.ConversationRevision);
        Assert.Null(await snapshots.GetWorkingPlanAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task LegacyDeltasMergeAcrossNormalizedAndLegacyStorageWithoutLosingTitles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Alter Plan");
        var first = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Plan", MessageStatus.Completed);
        await chats.SaveToolStepAsync(first.Id, Plan("old", new
        {
            steps = new[] { new { id = "a", title = "Analysieren", status = "pending" }, new { id = "b", title = "Prüfen", status = "pending" } },
            acceptanceCriteria = new[] { new { id = "check", title = "Gegenprobe", status = "pending" } },
        }, new { success = true, storedStepIds = StepIds, storedAcceptanceCriterionIds = CriterionIds }));
        var next = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Aktualisiert", MessageStatus.Completed);
        var delta = Plan("delta", new { steps = new[] { new { id = "a", status = "in_progress" } } },
            new { success = true, storedStepIds = StepIds, storedAcceptanceCriterionIds = CriterionIds, updatedSteps = new[] { new { id = "a", status = "in_progress" } } });
        await SetLegacyStepsAsync(environment, next.Id, [delta,
            Plan("failed", new { steps = new[] { new { id = "b", title = "Falscher Versuch", status = "completed" } } }, new { success = false }) with { Status = "failed" }]);
        var revision = (await chats.GetSessionAsync(session.Id))!.ConversationRevision;
        var before = (await snapshots.GetWorkingPlanAsync(session.Id))!;
        var plan = NativeWorkingPlanProjection.FromToolStep(before);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("Analysieren", plan.Steps[0].Title);
        Assert.Equal("in_progress", plan.Steps[0].Status);
        Assert.Equal("Prüfen", plan.Steps[1].Title);
        Assert.Equal("Gegenprobe", Assert.Single(plan.AcceptanceCriteria).Title);
        Assert.Equal(revision, (await chats.GetSessionAsync(session.Id))!.ConversationRevision);
        Assert.Equal(before.OutputJson, (await snapshots.GetWorkingPlanAtAsync(session.Id, next.Id, "delta"))!.OutputJson);
        await chats.NormalizeToolStepsAsync(next.Id);
        Assert.Equal(before.OutputJson, (await snapshots.GetWorkingPlanAsync(session.Id))!.OutputJson);
    }

    [Fact]
    public async Task StoredIdsSeparateANewRunAndACompleteReceiptSupersedesAllEarlierChanges()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Neue Laufplanung");
        var first = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Erster Lauf", MessageStatus.Completed);
        await chats.SaveToolStepAsync(first.Id, Plan("old", new { steps = new[] { new { id = "old", title = "Früherer Lauf", status = "pending" } } }, new { success = true }));
        var next = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Neuer Lauf", MessageStatus.Completed);
        await chats.SaveToolStepAsync(next.Id, Plan("new", new { steps = new[] { new { id = "new", title = "Aktueller Auftrag", status = "in_progress" } } },
            new { success = true, storedStepIds = NewStepIds, storedAcceptanceCriterionIds = System.Array.Empty<string>() }));
        var plan = NativeWorkingPlanProjection.FromToolStep(await snapshots.GetWorkingPlanAsync(session.Id));
        Assert.Equal("Aktueller Auftrag", Assert.Single(plan.Steps).Title);
        await chats.SaveToolStepAsync(next.Id, Plan("clear", new { explanation = "Plan zurückgesetzt" }, new
        {
            success = true, storedSteps = System.Array.Empty<object>(), storedAcceptanceCriteria = System.Array.Empty<object>(),
        }));
        Assert.Empty(NativeWorkingPlanProjection.FromToolStep(await snapshots.GetWorkingPlanAsync(session.Id)).Steps);
    }

    [Fact]
    public async Task HistoricalToolTabGetsTheCompletePlanAtThatExactStepRatherThanTheCurrentPlan()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Historische Werkzeugansicht");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Planentwicklung", MessageStatus.Completed);
        await chats.SaveToolStepAsync(message.Id, Plan("initial", new
        {
            steps = new[] { new { id = "a", title = "Analysieren", status = "pending" }, new { id = "b", title = "Prüfen", status = "pending" } },
        }, new { success = true, storedStepIds = StepIds }));
        await chats.SaveToolStepAsync(message.Id, Plan("middle", new { steps = new[] { new { id = "a", status = "in_progress" } } },
            new { success = true, storedStepIds = StepIds }));
        await chats.SaveToolStepAsync(message.Id, Plan("latest", new { explanation = "Neu geplant" }, new
        {
            success = true,
            storedSteps = new[] { new { id = "z", title = "Späterer Stand", status = "in_progress" } },
            storedAcceptanceCriteria = System.Array.Empty<object>(),
        }));
        var historical = NativeWorkingPlanProjection.FromToolStep(await snapshots.GetWorkingPlanAtAsync(session.Id, message.Id, "middle"));
        Assert.Equal(2, historical.Steps.Count);
        Assert.Equal("Analysieren", historical.Steps[0].Title);
        Assert.Equal("in_progress", historical.Steps[0].Status);
        Assert.Equal("pending", historical.Steps[1].Status);
        Assert.Equal("Späterer Stand", Assert.Single(NativeWorkingPlanProjection.FromToolStep(await snapshots.GetWorkingPlanAsync(session.Id)).Steps).Title);
        Assert.Null(await snapshots.GetWorkingPlanAtAsync(Guid.NewGuid(), message.Id, "middle"));
        Assert.Null(await snapshots.GetWorkingPlanAtAsync(session.Id, message.Id, "missing"));
    }

    private static readonly string[] StepIds = ["a", "b"];
    private static readonly string[] NewStepIds = ["new"];
    private static readonly string[] CriterionIds = ["check"];

    private static AssistantToolStep Plan(string id, object input, object output) => new(id, "coding.updatePlan", "completed",
        InputJson: JsonSerializer.Serialize(input), OutputJson: JsonSerializer.Serialize(output));

    private static async Task SetLegacyStepsAsync(TestEnvironment environment, Guid messageId, AssistantToolStep[] steps)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = environment.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chat_messages SET tool_steps_json=$steps WHERE id=$id;";
        command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(steps, JsonSerializerOptions.Web));
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }
}
