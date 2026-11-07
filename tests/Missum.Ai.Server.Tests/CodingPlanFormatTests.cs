using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Data;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class CodingPlanFormatTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingPlanReceiptIsRestoredOnlyFromACompletedJournalOperation(bool completed, bool pending)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(new(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", Text: "Task")])]), null)).Snapshot.RunId;
        var args = J("""{"steps":[{"id":"s1","title":"Phase 1: Task","status":"completed"}]}""");
        var call = new LmToolCall("model-plan", "coding.updatePlan", args);
        await repository.AppendEventAsync(run, RunEventTypes.ServerToolStarted,
            new { tool = call.Name, toolCallId = "actual-operation", arguments = args });
        if (completed)
            await repository.AppendEventAsync(run, RunEventTypes.ServerToolCompleted,
                new { tool = call.Name, toolCallId = "actual-operation", success = true,
                    result = new { success = true, storedSteps = new[] { new { id = "s1", title = "Phase 1: Task", status = "completed" } } } });
        await repository.SaveCheckpointAsync(run, new([new("user", "Task"), new("assistant", ToolCalls: [call])],
            1, 1, 0, 0, ActiveToolCalls: pending ? [call] : null, PreserveSessionPromptPrefix: true, PlanFormatVersion: CodingPlanFormat.CurrentVersion));
        var restored = (await repository.GetCheckpointAsync(run))!;
        Assert.Equal(completed && !pending ? 3 : 2, restored.Messages.Count);
        if (completed && !pending)
        {
            Assert.Equal(call.Id, restored.Messages[^1].ToolCallId);
            Assert.True(J(restored.Messages[^1].Content!).GetProperty("success").GetBoolean());
            Assert.False(restored.PreserveSessionPromptPrefix);
            await repository.SaveCheckpointAsync(run, restored);
            Assert.Equal(3, (await repository.GetCheckpointAsync(run))!.Messages.Count);
        }
    }

    [Fact]
    public void RetiredSavedToolCatalogsAreReplacedAndInvalidateTheEvaluatedPrefix()
    {
        var tool = new LmToolDefinition("coding.updatePlan", "Use evidenceIds", J("""{"type":"object","properties":{"evidenceQuery":{"type":"object"}}}"""));
        var checkpoint = new AgentRunCheckpoint([], 0, 0, 0, 0, ContextTools: [tool], LastModelTools: [tool], PreserveSessionPromptPrefix: true);
        var upgraded = CodingPlanFormat.UpgradeCheckpoint(checkpoint);
        Assert.False(upgraded.PreserveSessionPromptPrefix);
        Assert.DoesNotContain("evidenceQuery", upgraded.ContextTools![0].Parameters.GetRawText());
        Assert.DoesNotContain("evidenceIds", upgraded.LastModelTools![0].Description);
        Assert.Equal(2, upgraded.PlanFormatVersion);
    }

    [Fact]
    public void UpgradeChangesOnlyModelPlanMetadataAndPreservesConversationAndOutputs()
    {
        const string quotedRequest = "Die Ausgabe enthält evidenceIds. Bitte erkläre das Feld.";
        const string assistantText = "Der Beleg gehört zur Originalquelle.";
        var planArguments = J("""{"steps":[{"id":"s","title":"Phase 1: Prüfen","status":"completed","evidenceIds":["server-old"]}]}""");
        var checkpoint = new AgentRunCheckpoint([
            new("system", "Unveränderte grundlegende Rechte."),
            new("system", "MISSUM_CODING_PLAN_MILESTONES_V1\nVerwende evidenceIds."),
            new("user", quotedRequest),
            new("assistant", assistantText, ReasoningContent: "Original reasoning evidenceIds"),
            new("assistant", ToolCalls: [new("plan", "coding.updatePlan", planArguments)]),
            new("tool", """{"storedSteps":[{"id":"s","title":"Phase 1: Prüfen","status":"completed","evidenceIds":["server-old"]}]}""", ToolCallId: "plan"),
            new("tool", """{"path":"Publikation.md","content":"Quelle","evidence":{"evidenceId":"ev-original"},"_missumEvidence":{"planEvidenceId":"server-old"}}""", ToolCallId: "read"),
            new("tool", """{"findings":[{"evidenceId":"S1-E1","claim":"Originalbefund"}],"delegatedPlanEvidence":[{"planEvidenceId":"server-child"}]}""", ToolCallId: "research"),
            new("user", "MISSUM_CODING_PLAN_EVIDENCE_V1\nAlter Belegkandidat"),
        ], 4, 3, 200, 40, ActiveToolCalls: [new("active", "coding.updatePlan", planArguments)],
            PreserveSessionPromptPrefix: true, WorkingStatePromptIncluded: true);

        var upgraded = CodingPlanFormat.UpgradeCheckpoint(checkpoint, [quotedRequest]);

        Assert.Equal(2, upgraded.PlanFormatVersion);
        Assert.False(upgraded.PreserveSessionPromptPrefix);
        Assert.False(upgraded.WorkingStatePromptIncluded);
        Assert.Contains(upgraded.Messages, message => message.Content == quotedRequest);
        Assert.Contains(upgraded.Messages, message => message.Content == assistantText && message.ReasoningContent == "Original reasoning evidenceIds");
        Assert.DoesNotContain(upgraded.Messages, message => message.Content?.StartsWith("MISSUM_CODING_PLAN_", StringComparison.Ordinal) == true);
        var planCall = Assert.Single(upgraded.Messages.SelectMany(message => message.ToolCalls ?? []));
        Assert.False(planCall.Arguments.GetProperty("steps")[0].TryGetProperty("evidenceIds", out _));
        Assert.Equal("completed", planCall.Arguments.GetProperty("steps")[0].GetProperty("status").GetString());
        var read = J(upgraded.Messages.Single(message => message.ToolCallId == "read").Content!);
        Assert.Equal("ev-original", read.GetProperty("evidence").GetProperty("evidenceId").GetString());
        Assert.Equal("Quelle", read.GetProperty("content").GetString());
        Assert.False(read.TryGetProperty("_missumEvidence", out _));
        var research = J(upgraded.Messages.Single(message => message.ToolCallId == "research").Content!);
        Assert.Equal("S1-E1", research.GetProperty("findings")[0].GetProperty("evidenceId").GetString());
        Assert.False(research.TryGetProperty("delegatedPlanEvidence", out _));
        Assert.True(upgraded.ActiveToolCalls![0].Arguments.GetProperty("steps")[0].TryGetProperty("evidenceIds", out _));
        Assert.True(checkpoint.Messages[5].Content!.Contains("evidenceIds", StringComparison.Ordinal));
        Assert.Same(upgraded, CodingPlanFormat.UpgradeCheckpoint(upgraded));
    }

    [Fact]
    public void LegacyTypedStateKeepsPlanAndScientificMetadataWithoutEvidenceBindings()
    {
        const string legacy = """
            {"messages":[],"roundCount":1,"toolCallCount":2,"inputTokens":10,"outputTokens":2,"contextScope":"session-v3",
             "workingState":{"originalTask":"Prüfen","plan":[{"id":"s","title":"Phase 1: Prüfen","status":"in_progress","evidenceIds":["old"]}],
              "acceptanceCriteria":[],"facts":[{"text":"Befund","evidenceIds":["old"]}],"rejectedHypotheses":[],
              "evidence":[],"tests":[],"activeFiles":[],"failures":[
                 {"signature":"obsolete","tool":"coding.updatePlan","argumentHash":"h","error":"Unbekannte evidenceIds","count":2,"environmentRevision":0,"evidenceId":"old"},
                 {"signature":"genuine","tool":"coding.command","argumentHash":"h2","error":"syntax error","count":1,"environmentRevision":0,"evidenceId":"process"}],
              "phase":"exploration","environmentRevision":0,"sequence":4,"consecutivePlanNoOps":3,
              "science":{"profile":"claude-science-v1","stages":[{"id":"s","question":"Gilt A?","strategy":"Gegenprüfung","dependsOn":[],
                 "checks":[{"id":"q","kind":"numeric","required":true,"evidenceIds":["old"],"paths":["check.py"]}],
                 "openQuestions":["Offene Frage"],"blockers":[],"outcome":"open","reopenedAtSequence":3}]}}}
            """;
        var options = new JsonSerializerOptions(MissumAiProtocol.CreateJsonOptions()) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
        var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(legacy, options)!;
        var state = CodingPlanFormat.UpgradeCheckpoint(checkpoint).WorkingState!;
        var current = JsonSerializer.Serialize(state, options);
        Assert.DoesNotContain("evidenceIds", current, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reopenedAtSequence", current, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("s", Assert.Single(state.Plan).Id);
        Assert.Equal("Befund", Assert.Single(state.Facts).Text);
        Assert.Equal("numeric", Assert.Single(state.Science!.Stages[0].Checks).Kind);
        Assert.Equal("Offene Frage", Assert.Single(state.Science.Stages[0].OpenQuestions));
        Assert.Equal("genuine", Assert.Single(state.Failures).Signature);
        Assert.Equal(0, state.ConsecutivePlanNoOps);
    }

    [Fact]
    public async Task DatabaseUpgradeBacksUpArchiveAndPreservesRawJournalAndClientResults()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(new(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", Text: "Originalauftrag")])]), null)).Snapshot.RunId;
        const string original = """{"evidenceIds":["old"],"content":"Original"}""";
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE run_plan_evidence(run_id TEXT,evidence_id TEXT,receipt_json TEXT);
                INSERT INTO run_plan_evidence VALUES($run,'old',$original);
                INSERT INTO run_events(run_id,event_type,data_json,created_at) VALUES($run,'original.receipt',$original,'2026-11-06T00:00:00Z');
                INSERT INTO client_tool_results(proposal_id,run_id,result_json,created_at) VALUES('p',$run,$original,'2026-11-06T00:00:00Z');
                PRAGMA user_version=4;
                """;
            command.Parameters.AddWithValue("$run", run);
            command.Parameters.AddWithValue("$original", original);
            await command.ExecuteNonQueryAsync();
        }
        using var upgraded = new MissumAiDatabase(context.WrappedOptions);
        await using (var connection = await upgraded.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='run_plan_evidence';";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT data_json FROM run_events WHERE event_type='original.receipt';";
            Assert.Equal(original, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT result_json FROM client_tool_results WHERE proposal_id='p';";
            Assert.Equal(original, await command.ExecuteScalarAsync());
        }
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(context.Options.DatabasePath)!, "*.pre-plan-v2-*.bak"));
        await using var archive = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await archive.OpenAsync();
        await using var read = archive.CreateCommand();
        read.CommandText = "SELECT receipt_json FROM run_plan_evidence WHERE evidence_id='old';";
        Assert.Equal(original, await read.ExecuteScalarAsync());
    }

    private static JsonElement J(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
