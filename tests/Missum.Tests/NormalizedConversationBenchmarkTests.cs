using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class NormalizedConversationBenchmarkTests
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [Fact]
    public async Task OvernightSizedSingleMessageRetainsHistoryAndUsesBoundedReadsAndSingleStepWrites()
    {
        // A separate explicit performance run; portable builds do not call this or enable it.
        var reportPath = Environment.GetEnvironmentVariable("MISSUM_CONVERSATION_BENCHMARK_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath)) return;
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Overnight benchmark");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, new string('t', 160_000), MessageStatus.Completed);
        var body = new string('x', 8_600);
        var now = DateTimeOffset.UtcNow;
        var steps = Enumerable.Range(0, 2500).Select(index => new AssistantToolStep("step-" + index,
            "coding.read", "completed", body, ContentOffset: index * 60, UpdatedAt: now)).ToArray();
        var legacy = JsonSerializer.Serialize(steps, JsonSerializerOptions.Web);
        Assert.True(legacy.Length > 21 * 1024 * 1024);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = environment.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE chat_messages SET tool_steps_json=$steps WHERE id=$id;";
            command.Parameters.AddWithValue("$steps", legacy);
            command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        var clock = Stopwatch.StartNew();
        var first = (await snapshots.GetPageAsync(session.Id))!;
        var initialReadMilliseconds = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(64, Assert.Single(first.Conversation.Messages).ToolSteps!.Count);
        Assert.Equal(2500, first.ToolStepWindows![message.Id].TotalSteps);
        clock.Restart();
        await chats.NormalizeToolStepsAsync(message.Id);
        var normalizationMilliseconds = clock.Elapsed.TotalMilliseconds;
        var reads = new List<double>();
        var writes = new List<double>();
        for (var index = 0; index < 100; index++)
        {
            clock.Restart();
            var page = (await snapshots.GetPageAsync(session.Id))!;
            reads.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal(64, Assert.Single(page.Conversation.Messages).ToolSteps!.Count);
            clock.Restart();
            var changed = await chats.SaveToolStepDeltaAsync(message.Id, steps[2499] with
            {
                Detail = "Updated one row " + index, UpdatedAt = now.AddSeconds(index + 1),
            });
            writes.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal("step-2499", changed.Id);
            Assert.True(JsonSerializer.Serialize(changed).Length < 1_000);
        }
        var restored = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal(message.Content, restored.Content);
        Assert.Equal(2500, restored.ToolSteps!.Count);
        Assert.Equal(body, restored.ToolSteps[0].Detail);
        Assert.Equal("Updated one row 99", restored.ToolSteps[2499].Detail);
        var report = new
        {
            passed = true, messages = 1, toolSteps = 2500, legacyCharacters = legacy.Length,
            initialReadMilliseconds, normalizationMilliseconds,
            indexedPageP50Milliseconds = Percentile(reads, 0.5), indexedPageP95Milliseconds = Percentile(reads, 0.95),
            singleStepWriteP50Milliseconds = Percentile(writes, 0.5), singleStepWriteP95Milliseconds = Percentile(writes, 0.95),
            preservedFullMessageCharacters = restored.Content.Length, preservedToolSteps = restored.ToolSteps.Count,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ReportJsonOptions));
    }

    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * percentile) - 1];
    }
}
