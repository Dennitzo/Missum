using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Missum.Core.Chat;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ConversationPromptIndexTests
{
    [Fact]
    public async Task FirstStoredPromptIsCountedOnceAndIdenticalLaterPromptsRemainDistinct()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Prompt identities");
        Assert.Empty(await snapshots.GetPromptsAsync(session.Id));
        var first = await chats.AddMessageAsync(session.Id, ChatRole.User, "Prüfe die Gleichung.", MessageStatus.Completed);
        await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Antwort", MessageStatus.Streaming);
        await chats.AddMessageAsync(session.Id, ChatRole.User, "  \r\n\t", MessageStatus.Completed);
        foreach (var read in new[] { 1, 2 })
        {
            var prompt = Assert.Single(await snapshots.GetPromptsAsync(session.Id));
            Assert.Equal(first.Id, prompt.Id);
            Assert.Null(prompt.ParentMessageId);
            Assert.Equal(prompt, (await snapshots.GetPromptPageAsync(session.Id, prompt.Id))!.TargetPrompt);
        }
        var second = await chats.AddMessageAsync(session.Id, ChatRole.User, first.Content, MessageStatus.Completed);
        var prompts = await snapshots.GetPromptsAsync(session.Id);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), prompts.Select(prompt => prompt.Id).Order());
        Assert.All(prompts, prompt => Assert.Equal(first.Content, prompt.Preview));
        Assert.Empty(await snapshots.GetPromptsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task PersistedSteeringCountsOnceAcrossReplayAndPreservesChronologyAndScope()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Stored steering");
        var other = await chats.CreateSessionAsync("Other session");
        var first = await chats.AddMessageAsync(session.Id, ChatRole.User, "Start", MessageStatus.Completed);
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Vorher. Nachher.", MessageStatus.Streaming);
        var last = await chats.AddMessageAsync(session.Id, ChatRole.User, "Nächster Auftrag", MessageStatus.Completed);
        var at = DateTimeOffset.Parse("2026-10-01T00:00:00+00:00", CultureInfo.InvariantCulture);
        await SetCreatedAsync(environment, first.Id, at);
        await SetCreatedAsync(environment, parent.Id, at.AddSeconds(1));
        await SetCreatedAsync(environment, last.Id, at.AddSeconds(5));
        var firstSteer = new AssistantToolStep("steering-a", "assistant.steering", "running", "Derselbe Text",
            ContentOffset: 7, StartedAt: at.AddSeconds(2), UpdatedAt: at.AddSeconds(2));
        await chats.SaveToolStepDeltaAsync(parent.Id, firstSteer);
        await chats.SaveToolStepDeltaAsync(parent.Id, firstSteer with { Status = "completed", UpdatedAt = at.AddSeconds(4) });
        await chats.SaveToolStepDeltaAsync(parent.Id, firstSteer with { Id = "steering-b", Status = "completed" });
        await chats.SaveToolStepDeltaAsync(parent.Id, firstSteer with { Id = "steering-failed", Status = "failed" });
        await chats.SaveToolStepDeltaAsync(parent.Id, firstSteer with { Id = "steering-empty", Detail = " \n\t" });
        await chats.SaveToolStepDeltaAsync(parent.Id, firstSteer with { Id = "ordinary-tool", Tool = "coding.read" });

        var prompts = await snapshots.GetPromptsAsync(session.Id);
        Assert.Equal(4, prompts.Count);
        Assert.Equal(first.Id, prompts[0].Id);
        Assert.Equal(SteeringPromptIdentity.Create(parent.Id, "steering-a"), prompts[1].Id);
        Assert.Equal(SteeringPromptIdentity.Create(parent.Id, "steering-b"), prompts[2].Id);
        Assert.Equal(last.Id, prompts[3].Id);
        Assert.Equal(prompts[1].CreatedAt, prompts[2].CreatedAt);
        Assert.Equal("Derselbe Text", prompts[1].Preview);
        Assert.Equal(prompts[1].Preview, prompts[2].Preview);
        Assert.Equal(parent.Id, prompts[1].ParentMessageId);
        Assert.Equal("steering-a", prompts[1].StepId);
        Assert.Equal(7, prompts[1].ContentOffset);
        Assert.True(prompts[1].StepOrdinal < prompts[2].StepOrdinal);
        Assert.Equal(prompts, await snapshots.GetPromptsAsync(session.Id));
        Assert.Empty(await snapshots.GetPromptsAsync(other.Id));
        Assert.Null(await snapshots.GetPromptPageAsync(other.Id, prompts[1].Id));
        Assert.Null(await snapshots.GetPromptPageAsync(other.Id, first.Id));
        Assert.Null(await snapshots.GetPromptPageAsync(session.Id, SteeringPromptIdentity.Create(parent.Id, "steering-failed")));
    }

    [Fact]
    public async Task OldSteeringTargetLoadsBoundedParentAndStepWindowWithoutToolBodiesOrHistoryMutation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Old steering anchor");
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Vorher. Nachher.", MessageStatus.Completed);
        await SetCreatedAsync(environment, parent.Id, DateTimeOffset.Parse("2000-01-01T00:00:00+00:00", CultureInfo.InvariantCulture));
        var detail = new string('p', 1000);
        await chats.SaveToolStepDeltaAsync(parent.Id, new("steering-old", "assistant.steering", "completed", detail,
            ContentOffset: 7, StartedAt: DateTimeOffset.UtcNow));
        for (var index = 0; index < 128; index++)
            await chats.SaveToolStepDeltaAsync(parent.Id, new("read-" + index, "coding.read", "completed",
                OutputJson: JsonSerializer.Serialize(new { content = new string('x', 10_000) })));
        for (var index = 0; index < 30; index++)
            await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Später " + index, MessageStatus.Completed);
        var before = await chats.GetMessageAsync(parent.Id);
        var revision = (await chats.GetSessionAsync(session.Id))!.ConversationRevision;
        Assert.DoesNotContain((await snapshots.GetPageAsync(session.Id))!.Conversation.Messages, message => message.Id == parent.Id);
        var prompt = Assert.Single(await snapshots.GetPromptsAsync(session.Id));
        Assert.Equal(256, prompt.Preview.Length);
        var page = (await snapshots.GetPromptPageAsync(session.Id, prompt.Id))!;
        Assert.Equal(prompt, page.TargetPrompt);
        Assert.InRange(page.Conversation.Messages.Count, 1, 24);
        var target = Assert.Single(page.Conversation.Messages, message => message.Id == parent.Id);
        Assert.Equal(64, target.ToolSteps!.Count);
        Assert.Equal(detail, Assert.Single(target.ToolSteps, step => step.Id == "steering-old").Detail);
        Assert.All(target.ToolSteps.Where(step => step.Tool == "coding.read"), step =>
        {
            Assert.True(step.DetailsDeferred);
            Assert.Null(step.OutputJson);
            Assert.Null(step.InputJson);
        });
        Assert.Equal(129, page.ToolStepWindows![parent.Id].TotalSteps);
        Assert.Equal(0L, page.ToolStepWindows[parent.Id].BeforeOrdinal);
        Assert.Equal(revision, (await chats.GetSessionAsync(session.Id))!.ConversationRevision);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await chats.GetMessageAsync(parent.Id)));
    }

    [Fact]
    public async Task LegacySteeringRetainsTheSameIdentityAfterIndexedNormalization()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Legacy steering");
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Antwort", MessageStatus.Completed);
        var step = new AssistantToolStep("steering-legacy", "assistant.steering", "completed", "Gespeicherte Anweisung",
            ContentOffset: 2, StartedAt: parent.CreatedAt.AddSeconds(1));
        await using (var connection = new SqliteConnection("Data Source=" + environment.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE chat_messages SET tool_steps_json=$steps WHERE id=$message;";
            command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(new[] { step }, JsonSerializerOptions.Web));
            command.Parameters.AddWithValue("$message", parent.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        var legacy = Assert.Single(await snapshots.GetPromptsAsync(session.Id));
        Assert.Equal(SteeringPromptIdentity.Create(parent.Id, step.Id), legacy.Id);
        Assert.Equal(legacy, (await snapshots.GetPromptPageAsync(session.Id, legacy.Id))!.TargetPrompt);
        await chats.NormalizeToolStepsAsync(parent.Id);
        Assert.Equal(legacy, Assert.Single(await snapshots.GetPromptsAsync(session.Id)));
        Assert.Equal(legacy, (await snapshots.GetPromptPageAsync(session.Id, legacy.Id))!.TargetPrompt);
    }

    private static async Task SetCreatedAsync(TestEnvironment environment, Guid id, DateTimeOffset createdAt)
    {
        await using var connection = new SqliteConnection("Data Source=" + environment.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chat_messages SET created_at=$created WHERE id=$id;";
        command.Parameters.AddWithValue("$created", createdAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }
}
