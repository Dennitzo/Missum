using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class NormalizedConversationPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResearchActivityMetadataSurvivesThinLegacyAndIndexedHeaders(bool legacy)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Research activity");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Research result", MessageStatus.Completed);
        var research = new AssistantToolStep("deep-tool", "web.deepResearch", "completed", new string('r', 100_000), Explanation: "Rechercheauftrag");
        var companion = new AssistantToolStep("research-view", "subagent", "completed", OutputJson: JsonSerializer.Serialize(new
        {
            agentId = "deep-research:test", runId = "deep-research-run:test", parentRunId = "run-parent", sessionId = session.Id,
            title = "Deep Research", status = "completed", kind = "deepResearch", originToolStepId = research.Id, originMessageId = message.Id, transcriptRevision = 17,
            unusedTranscript = new string('x', 100_000),
        }));
        var steps = new[] { research, companion }.Concat(Enumerable.Range(0, 65)
            .Select(index => new AssistantToolStep("ordinary-" + index, "coding.read", "completed"))).ToArray();
        if (legacy) await SetLegacyAsync(environment, message.Id, steps);
        else foreach (var step in steps) await chats.SaveToolStepDeltaAsync(message.Id, step);
        var page = (await snapshots.GetPageAsync(session.Id))!;
        Assert.DoesNotContain(page.Conversation.Messages[0].ToolSteps!, step => step.Id == research.Id || step.Id == companion.Id);
        var auxiliary = Assert.Single(await snapshots.GetAuxiliaryMessagesAsync(session.Id));
        Assert.Empty(auxiliary.Content);
        Assert.Equal(2, auxiliary.ToolSteps!.Count);
        var header = auxiliary.ToolSteps.Single(step => step.Id == companion.Id);
        using var metadata = JsonDocument.Parse(header.OutputJson!);
        Assert.Equal("deepResearch", metadata.RootElement.GetProperty("kind").GetString());
        Assert.Equal(research.Id, metadata.RootElement.GetProperty("originToolStepId").GetString());
        Assert.Equal(message.Id, metadata.RootElement.GetProperty("originMessageId").GetGuid());
        Assert.Equal(17, metadata.RootElement.GetProperty("transcriptRevision").GetInt64());
        Assert.False(metadata.RootElement.TryGetProperty("unusedTranscript", out _));
        Assert.Null(auxiliary.ToolSteps.Single(step => step.Id == research.Id).Detail);
        Assert.Equal(research, await snapshots.GetToolStepAsync(session.Id, message.Id, research.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagedArtifactsRetainTheirOriginalOffsetOutsideTheToolWindowWithoutChangingStoredMetadata(bool legacy)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var session = await chats.CreateSessionAsync("Image before old tools");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Before image and after image", MessageStatus.Completed);
        var steps = Enumerable.Range(0, 81).Select(index => new AssistantToolStep("step-" + index,
            "coding.read", "completed", new string('x', 1000), ContentOffset: index == 0 ? 3 : 20)).ToArray();
        if (legacy) await SetLegacyAsync(environment, message.Id, steps);
        else foreach (var step in steps) await chats.SaveToolStepDeltaAsync(message.Id, step);
        var bytes = "Image data"u8.ToArray();
        using var image = new MemoryStream(bytes, writable: false);
        var artifact = await artifacts.ImportAsync(message.Id, "old-image", "image.png", "image/png",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, "media", steps[0].Id,
            new Dictionary<string, string> { ["sourceContentOffset"] = "9999" }, image);
        var revision = (await chats.GetMessageTextAsync(message.Id))!.Revision;

        var page = (await snapshots.GetPageAsync(session.Id))!;
        Assert.DoesNotContain(page.Conversation.Messages[0].ToolSteps!, step => step.Id == steps[0].Id);
        Assert.Equal("3", Assert.Single(page.Conversation.Artifacts[message.Id]).Metadata!["sourceContentOffset"]);
        Assert.Equal("3", Assert.Single((await snapshots.GetAsync(session.Id))!.Artifacts[message.Id]).Metadata!["sourceContentOffset"]);
        Assert.Equal("9999", (await artifacts.GetAsync(artifact.Id))!.Metadata!["sourceContentOffset"]);
        Assert.Equal(revision, (await chats.GetMessageTextAsync(message.Id))!.Revision);
    }

    [Fact]
    public async Task OldSubagentReceiptIsAvailableOutsideTheVisibleToolWindowWithoutLoadingItsTranscript()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Old child tab");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Answer", MessageStatus.Completed);
        var child = new AssistantToolStep("old-child", "subagent", "completed", "Delegated task",
            OutputJson: JsonSerializer.Serialize(new
            {
                agentId = "agent-old", runId = "run-child", parentRunId = "run-main", sessionId = session.Id,
                title = "Completed work", status = "completed", unusedTranscript = new string('x', 100_000),
            }));
        await chats.SaveToolStepDeltaAsync(message.Id, child);
        for (var index = 0; index < 80; index++)
            await chats.SaveToolStepDeltaAsync(message.Id, new("tool-" + index, "coding.read", "completed"));
        var page = (await snapshots.GetPageAsync(session.Id))!;
        Assert.DoesNotContain(Assert.Single(page.Conversation.Messages).ToolSteps!, step => step.Tool == "subagent");
        var auxiliary = await snapshots.GetAuxiliaryMessagesAsync(session.Id);
        var header = Assert.Single(Assert.Single(auxiliary).ToolSteps!);
        Assert.Empty(auxiliary[0].Content);
        Assert.Equal(message.Id, auxiliary[0].Id);
        Assert.Equal(child.Id, header.Id);
        Assert.True(header.OutputJson!.Length < 400);
        Assert.Equal(child, await snapshots.GetSubagentReceiptAsync(session.Id, message.Id, "agent-old"));
        Assert.Null(await snapshots.GetSubagentReceiptAsync(Guid.NewGuid(), message.Id, "agent-old"));
    }

    [Fact]
    public async Task OneLargeMessageHasBoundedToolPagesWithoutLosingAnyToolOrPayload()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Long single run");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Original answer", MessageStatus.Streaming);
        for (var index = 0; index < 150; index++)
            await chats.SaveToolStepDeltaAsync(message.Id, new("step-" + index, "coding.read", "completed",
                new string('x', 10_000), ContentOffset: 3, UpdatedAt: message.CreatedAt.AddSeconds(index)));

        var page = (await snapshots.GetPageAsync(session.Id))!;
        Assert.Equal(64, Assert.Single(page.Conversation.Messages).ToolSteps!.Count);
        var window = page.ToolStepWindows![message.Id];
        Assert.Equal(150, window.TotalSteps);
        Assert.True(window.HasMore);
        var collected = new List<AssistantToolStep>(page.Conversation.Messages[0].ToolSteps!);
        while (window.HasMore)
        {
            var older = (await snapshots.GetToolStepsPageAsync(session.Id, message.Id, beforeOrdinal: window.BeforeOrdinal))!;
            collected.InsertRange(0, older.Steps);
            window = older.Window;
        }
        Assert.Equal(150, collected.Count);
        Assert.Equal(150, collected.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(collected, step => { Assert.True(step.DetailsDeferred); Assert.Null(step.Detail); });
        Assert.Equal(10_000, (await snapshots.GetToolStepAsync(session.Id, message.Id, "step-0"))!.Detail!.Length);
        Assert.Equal(150, (await chats.GetMessageAsync(message.Id))!.ToolSteps!.Count);
        Assert.Null(await snapshots.GetToolStepsPageAsync(Guid.NewGuid(), message.Id));
        Assert.Equal("[]", await ScalarAsync(environment, "SELECT tool_steps_json FROM chat_messages WHERE id=$id", message.Id));
    }

    [Fact]
    public async Task LegacyImportIsPerMessageResumableAndDoesNotChangeHistoryOrRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Legacy import");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Preserved", MessageStatus.Completed);
        var unrelated = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Other", MessageStatus.Completed);
        var step = new AssistantToolStep("old", "coding.read", "completed", new string('x', 200_000), ContentOffset: 2);
        await SetLegacyAsync(environment, message.Id, [step]);
        await SetLegacyAsync(environment, unrelated.Id, [step]);
        var before = await chats.GetMessageAsync(message.Id);
        await chats.NormalizeToolStepsAsync(message.Id);
        await chats.NormalizeToolStepsAsync(message.Id);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await chats.GetMessageAsync(message.Id)));
        Assert.Equal("1", await ScalarAsync(environment, "SELECT COUNT(*) FROM chat_tool_step_messages WHERE message_id=$id", message.Id));
        Assert.Equal("0", await ScalarAsync(environment, "SELECT COUNT(*) FROM chat_tool_step_messages WHERE message_id=$id", unrelated.Id));
        await chats.SaveToolStepDeltaAsync(message.Id, step with { Detail = "Only this row changed", UpdatedAt = DateTimeOffset.UtcNow });
        Assert.Equal("Only this row changed", (await chats.GetMessageAsync(message.Id))!.ToolSteps![0].Detail);
        Assert.Equal(200_000, (await chats.GetMessageAsync(unrelated.Id))!.ToolSteps![0].Detail!.Length);
    }

    [Fact]
    public async Task StreamCommitPersistsMessageStepsAndReplayCursorTogetherAndRejectsOlderReplay()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Atomic stream");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Before", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var run = await environment.Get<IMissumAiRunRepository>().CreateAsync(new(Guid.NewGuid(), session.Id, message.Id,
            null, "atomic-key", "run-atomic", 0, "running", null, null, now, now));
        var writer = Assert.IsAssignableFrom<IStreamingConversationRepository>(chats);
        var step = new AssistantToolStep("read", "coding.read", "completed", "All code", ContentOffset: 4);
        var batch = new ConversationStreamBatch(message.Id, "After update", MessageStatus.Streaming, [step],
            run.Id, "run-atomic", 42, "running");
        await writer.CommitStreamAsync(batch);
        Assert.Equal(42, (await environment.Get<IMissumAiRunRepository>().GetAsync(run.Id))!.LastEventId);
        Assert.Equal("After update", (await chats.GetMessageAsync(message.Id))!.Content);
        Assert.Equal(step, Assert.Single((await chats.GetMessageAsync(message.Id))!.ToolSteps!));
        await writer.CommitStreamAsync(batch with { Content = "Older replay", LastEventId = 41 });
        Assert.Equal("After update", (await chats.GetMessageAsync(message.Id))!.Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitStreamAsync(batch with
        {
            Content = "Wrong owner", LastEventId = 43, ServerRunId = "other-run",
        }));
        Assert.Equal(42, (await environment.Get<IMissumAiRunRepository>().GetAsync(run.Id))!.LastEventId);
        Assert.Equal("After update", (await chats.GetMessageAsync(message.Id))!.Content);
    }

    [Fact]
    public async Task InvalidBatchRollsBackItsNewStepAndCursor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Atomic rollback");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Long enough", MessageStatus.Streaming);
        await chats.SaveToolStepDeltaAsync(message.Id, new("anchor", "coding.read", "completed", ContentOffset: 10));
        var now = DateTimeOffset.UtcNow;
        var run = await environment.Get<IMissumAiRunRepository>().CreateAsync(new(Guid.NewGuid(), session.Id, message.Id,
            null, "rollback-key", "run-rollback", 0, "running", null, null, now, now));
        var writer = Assert.IsAssignableFrom<IStreamingConversationRepository>(chats);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitStreamAsync(new(message.Id, "short",
            MessageStatus.Streaming, [new("new", "coding.read", "completed", ContentOffset: 0)], run.Id, "run-rollback", 10, "running")));
        Assert.Equal(0, (await environment.Get<IMissumAiRunRepository>().GetAsync(run.Id))!.LastEventId);
        Assert.Equal("Long enough", (await chats.GetMessageAsync(message.Id))!.Content);
        Assert.Equal("anchor", Assert.Single((await chats.GetMessageAsync(message.Id))!.ToolSteps!).Id);
    }

    [Fact]
    public async Task MetadataReadsDoNotLoadLargeToolDetailsAndSourceLookupUsesNormalizedSteps()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Metadata");
        var user = await chats.AddMessageAsync(session.Id, ChatRole.User, "Question", MessageStatus.Completed);
        var answer = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Answer", MessageStatus.Completed);
        await chats.SaveToolStepDeltaAsync(answer.Id, new("web", "web.fetch", "completed", new string('x', 300_000),
            OutputJson: "{\"url\":\"https://example.org/paper\",\"title\":\"Source\"}"));
        Assert.Empty((await chats.GetMessageTextAsync(answer.Id))!.ToolSteps!);
        Assert.Equal(user.Id, (await chats.GetFirstUserMessageAsync(session.Id))!.Id);
        var context = await chats.ListContextMessagesAsync(session.Id, 1);
        Assert.Equal(answer.Id, Assert.Single(context).Id);
        Assert.Empty(context[0].ToolSteps!);
        var sources = await environment.Get<IConversationSnapshotRepository>().GetSourcesAsync(session.Id);
        Assert.Equal("https://example.org/paper", Assert.Single(sources).Url);
    }

    private static async Task SetLegacyAsync(TestEnvironment environment, Guid id, AssistantToolStep[] steps)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = environment.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chat_messages SET tool_steps_json=$steps WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(steps, JsonSerializerOptions.Web));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(TestEnvironment environment, string sql, Guid id)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = environment.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
