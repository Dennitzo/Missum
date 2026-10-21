using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class SessionOnlyContextTests
{
    [Theory]
    [InlineData(RunMode.Coding)]
    [InlineData(RunMode.General)]
    public async Task SameProjectSessionsKeepIndependentCheckpointsAndOriginalEvents(RunMode mode)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var first = Request(mode, "session-a");
        var prior = (await repository.CreateAsync(first, null)).Snapshot.RunId;
        await repository.SaveCheckpointAsync(prior, new([new("system", "Policy"), new("user", "Only A knows ALPHA")], 1, 0, 10, 2));
        await repository.AppendEventAsync(prior, RunEventTypes.TextDelta, new TextDeltaEvent("Original history stays"));
        await repository.CancelAsync(prior);
        var second = Request(mode, "session-b");
        var secondRun = (await repository.CreateAsync(second, null)).Snapshot.RunId;
        Assert.Null(mode == RunMode.Coding
            ? await repository.GetSessionContextAsync(secondRun, second)
            : (await repository.GetGeneralSessionContextAsync(secondRun, second))?.Checkpoint);
        var resumed = (await repository.CreateAsync(first, null)).Snapshot.RunId;
        var restored = mode == RunMode.Coding ? await repository.GetSessionContextAsync(resumed, first)
            : (await repository.GetGeneralSessionContextAsync(resumed, first))?.Checkpoint;
        Assert.NotNull(restored);
        Assert.Contains(restored.Messages, message => message.Content == "Only A knows ALPHA");
        Assert.Contains(await repository.GetEventsAfterAsync(prior, 0), item => item.Type == RunEventTypes.TextDelta);
    }

    [Fact]
    public async Task RetiredSharedCheckpointsCannotReenterSessionContextButHistoryRemainsReadable()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = Request(RunMode.Coding, "existing");
        var prior = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        await repository.SaveCheckpointAsync(prior, new([new("user", "Old mixed project summary")], 1, 0, 10, 2));
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE run_checkpoints SET checkpoint_json=json_set(json_remove(checkpoint_json,'$.contextScope'),
                    '$.projectContext',json('{"projectKey":"old"}')) WHERE run_id=$run;
                UPDATE coding_session_contexts SET checkpoint_json=(SELECT checkpoint_json FROM run_checkpoints WHERE run_id=$run)
                    WHERE run_id=$run;
                UPDATE runs SET request_json=json_set(request_json,'$.projectContext',json('{"projectKey":"old"}')) WHERE run_id=$run;
                """;
            command.Parameters.AddWithValue("$run", prior);
            await command.ExecuteNonQueryAsync();
        }
        await repository.AppendEventAsync(prior, RunEventTypes.TextDelta, new TextDeltaEvent("Preserved original response"));
        await repository.CancelAsync(prior);
        Assert.Null(await repository.GetCheckpointAsync(prior));
        Assert.Equal(request.SessionId, (await repository.GetRequestAsync(prior))!.SessionId);
        var next = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        Assert.Null(await repository.GetSessionContextAsync(next, request));
        Assert.Contains(await repository.GetEventsAfterAsync(prior, 0), item => item.Type == RunEventTypes.TextDelta);
    }

    [Fact]
    public void RemovedSharingFieldsAndToolsAreRejectedAndKvUsesAFreshSessionNamespace()
    {
        var json = JsonSerializer.Serialize(Request(RunMode.Coding, "a"), MissumAiProtocol.CreateJsonOptions());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunRequest>(json[..^1] + ",\"projectContext\":{}}", MissumAiProtocol.CreateJsonOptions()));
        var key = ModelRuntimeClient.BuildSessionCacheKey("a", "coding", "C:/same-project");
        Assert.StartsWith("missum-session-v3-", key);
        Assert.NotEqual(key, ModelRuntimeClient.BuildSessionCacheKey("b", "coding", "C:/same-project"));
        Assert.DoesNotContain(new AgentToolCatalog().GetAvailableTools(Request(RunMode.Coding, "a")), tool => tool.Name == "project.history");
    }

    private static RunRequest Request(RunMode mode, string session) => new(MissumAiProtocol.Version, mode,
        [new("user", [new("text", "Task")])], SessionId: session, WorkspacePath: "C:/same-project",
        CodingOptions: mode == RunMode.Coding ? new(WorkspacePath: "C:/same-project", ContinueSessionContext: true) : null);
}
