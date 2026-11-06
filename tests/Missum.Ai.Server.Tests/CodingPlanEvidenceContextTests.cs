using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CodingPlanEvidenceContextTests
{
    private static readonly string[] ExpectedCandidates = ["fourth", "third", "second"];

    [Fact]
    public void SourceProjectionKeepsExactTextAndLabelsPlanAndOutputReferencesSeparately()
    {
        const string source = "Größe = \\Box\r\n  \\nabla ";
        const string outputId = "ev-a65a806f8f99b25900b589d47df71fbb";
        var call = new LmToolCall("native-call", "coding.read", JsonSerializer.SerializeToElement(new { path = "Publikation.md" }));
        var raw = JsonSerializer.Serialize(new
        {
            status = "completed",
            result = new { path = "Publikation.md", content = source, contentFormat = "exact", evidenceId = outputId },
        });
        var state = CodingWorkingStateReducer.ObserveToolResult(Start(), call with { Id = "plan-read" }, raw);
        var projected = ScienceSkill.ProjectToolEvidence(raw, state, "plan-read");
        var metadata = JsonSerializer.Deserialize<JsonElement>(projected).GetProperty("_missumEvidence");
        Assert.Equal("plan-read", metadata.GetProperty("planEvidenceId").GetString());
        Assert.Equal(outputId, metadata.GetProperty("outputEvidenceId").GetString());
        Assert.Equal("Publikation.md", metadata.GetProperty("path").GetString());
        Assert.Contains(metadata.GetProperty("eligibleKinds").EnumerateArray(), item => item.GetString() == "publication");
        var native = ModelRuntimeClient.PrepareNativeRequestMessages([
            new("user", "Prüfe die Publikation."), new("assistant", ToolCalls: [call]), new("tool", projected, ToolCallId: call.Id),
        ]);
        Assert.Equal(call.Id, native[^1].ToolCallId);
        Assert.Contains("\"planEvidenceId\":\"plan-read\"", native[^1].Content, StringComparison.Ordinal);
        Assert.Contains("```\n" + source + "\n```", native[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredFinalResultStillCarriesItsPlanEvidence()
    {
        var call = new LmToolCall("command", "coding.command", JsonSerializer.SerializeToElement(new { executable = "python" }));
        const string raw = "{\"status\":\"completed\",\"result\":{\"success\":true,\"exitCode\":0}}";
        var state = CodingWorkingStateReducer.ObserveToolResult(Start(), call with { Id = "plan-command" }, raw);
        var content = ScienceSkill.ProjectToolEvidence(raw, state, "plan-command");
        var result = CommandMonitoringPolicy.ToolResult(call, content, new(null, 0, null, [call.Id]));
        var text = result.Content!;
        var envelope = JsonSerializer.Deserialize<JsonElement>(text[(text.LastIndexOf('\n') + 1)..]);
        var metadata = JsonSerializer.Deserialize<JsonElement>(envelope.GetProperty("result").GetString()!).GetProperty("_missumEvidence");
        Assert.True(envelope.GetProperty("authoritativeFinalResult").GetBoolean());
        Assert.Equal("plan-command", metadata.GetProperty("planEvidenceId").GetString());
        Assert.Equal("Exitcode 0", metadata.GetProperty("shortResult").GetString());
        Assert.DoesNotContain(metadata.GetProperty("eligibleKinds").EnumerateArray(), item => item.GetString() == "publication");
    }

    [Fact]
    public void DelegatedResultShowsOnlyUsableReceiptsOfItsCompletedChild()
    {
        var state = Start() with
        {
            Evidence = [
                Receipt("child-read", "coding.read", 3, "Publikation.md") with { Historical = true, SourceRunId = "child" },
                Receipt("other-child-read", "coding.read", 4) with { Historical = true, SourceRunId = "other" },
                Receipt("child-output-reader", "coding.readOutput", 5) with { Historical = true, SourceRunId = "child" },
                Receipt("failed-child-command", "coding.command", 6) with { Success = false, Historical = true, SourceRunId = "child" },
            ],
        };
        var raw = JsonSerializer.SerializeToElement(new { status = "completed", runId = "child", result = "Original" });
        var projected = ScienceSkill.ProjectDelegatedEvidence(raw, state);
        var descriptor = Assert.Single(projected.GetProperty("delegatedPlanEvidence").EnumerateArray());
        Assert.Equal("child-read", descriptor.GetProperty("planEvidenceId").GetString());
        Assert.Equal("Original", projected.GetProperty("result").GetString());
        Assert.False(raw.TryGetProperty("delegatedPlanEvidence", out _));

        var failed = JsonSerializer.SerializeToElement(new { status = "failed", runId = "child" });
        Assert.False(ScienceSkill.ProjectDelegatedEvidence(failed, state).TryGetProperty("delegatedPlanEvidence", out _));
    }

    [Fact]
    public void ActiveCheckGetsAtMostThreeMatchingFreshCandidatesAndStableSnapshot()
    {
        var path = "Publikation.md";
        var state = Start([new("publish", "publication", true, [], [path])], reopenedAt: 1) with
        {
            Evidence = [
                Receipt("stale", "coding.edit", 1, path),
                Receipt("first", "coding.edit", 2, path),
                Receipt("second", "coding.edit", 3, path),
                Receipt("third", "coding.read", 4, path),
                Receipt("fourth", "coding.write", 5, path),
                Receipt("render", "coding.command", 6),
                Receipt("other", "coding.read", 7, "other.md"),
            ],
        };
        var messages = new List<LmChatMessage> { new("user", "Prüfe die Publikation.") };
        ScienceSkill.EnsurePlanEvidenceContext(messages, state);
        var snapshot = messages[1].Content!;
        var row = Assert.Single(snapshot.Split('\n'), line => line.StartsWith('{'));
        var data = JsonSerializer.Deserialize<JsonElement>(row);
        Assert.Equal("publish", data.GetProperty("checkId").GetString());
        Assert.Equal(ExpectedCandidates, data.GetProperty("candidates").EnumerateArray()
            .Select(candidate => candidate.GetProperty("planEvidenceId").GetString()));
        Assert.True(snapshot.Length <= CodingPlanEvidenceContext.MaximumCharacters);
        messages[1] = new("user", "Missum-Laufanweisung:\n" + snapshot);
        ScienceSkill.EnsurePlanEvidenceContext(messages, state);
        Assert.Equal(2, messages.Count);
        var completed = state with { Plan = [state.Plan[0] with { Status = "completed" }] };
        ScienceSkill.EnsurePlanEvidenceContext(messages, completed);
        ScienceSkill.EnsurePlanEvidenceContext(messages, completed);
        Assert.Equal(3, messages.Count);
        Assert.Equal(CodingPlanEvidenceContext.Empty, messages[2].Content);
    }

    [Fact]
    public void ShortlistPrioritizesUndeclaredActiveChecksWithinItsCharacterBudget()
    {
        var state = Start();
        var science = state.Science!;
        var longPath = "work/" + new string('ü', 500) + ".md";
        state = state with
        {
            Plan = [new("s2", "Später", "pending", []), .. state.Plan],
            Science = science with
            {
                Stages = [.. science.Stages,
                    new("s2", "Spätere Prüfung", "Lesen", [], [new("pending", "publication", true, [], [])], [], [])],
            },
            Evidence = Enumerable.Range(1, 7).Select(index => Receipt("read-" + index, "coding.read", index, longPath)
                with { Summary = new string('x', 1000) + "\r\nNicht anzeigen" }).ToArray(),
        };
        var snapshot = CodingPlanEvidenceContext.Build(state)!;
        Assert.True(snapshot.Length <= CodingPlanEvidenceContext.MaximumCharacters);
        Assert.Contains("Weitere Kandidaten über evidenceQuery abrufen.", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Nicht anzeigen", snapshot, StringComparison.Ordinal);
        var first = JsonSerializer.Deserialize<JsonElement>(snapshot.Split('\n').First(line => line.StartsWith('{')));
        Assert.Equal("s1", first.GetProperty("stageId").GetString());
        Assert.All(first.GetProperty("candidates").EnumerateArray(), candidate =>
            Assert.Equal(80, candidate.GetProperty("shortResult").GetString()!.Length));
    }

    [Fact]
    public void CompactionKeepsLatestFoldedShortlistAndDoesNotSummarizeEarlierShortlists()
    {
        var system = new LmChatMessage("system", "Unverändertes Präfix.");
        var task = new LmChatMessage("user", "Prüfe die Publikation.");
        var old = new LmChatMessage("system", CodingPlanEvidenceContext.Marker + "old candidate");
        var current = new LmChatMessage("user", "Missum-Laufanweisung:\n" + CodingPlanEvidenceContext.Marker + "new candidate");
        LmChatMessage[] messages = [system, new("user", "Älterer Auftrag"), old,
            new("assistant", new string('x', 12_000)), current, task, new("assistant", "Die Prüfung läuft.")];
        var plan = CodingContextCompactor.Plan(messages, 32_768, force: true)!;
        var compacted = CodingContextCompactor.Complete(plan, "Ältere Arbeit ist dokumentiert.");
        Assert.Same(system, compacted[0]);
        Assert.Same(task, plan.CurrentRequest);
        Assert.Contains(current, compacted);
        Assert.DoesNotContain(old, compacted);
        Assert.DoesNotContain(CodingPlanEvidenceContext.Marker, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
    }

    private static CodingWorkingState Start(IReadOnlyList<ScienceCheck>? checks = null, long reopenedAt = 0) =>
        CodingWorkingState.Create("Publikation prüfen") with
        {
            Plan = [new("s1", "Phase 1: Publikation prüfen", "in_progress", [])],
            Science = new(ScienceSkill.Profile, [new("s1", "Stimmt die Publikation?", "Inhalt lesen", [], checks ?? [], [], [],
                ReopenedAtSequence: reopenedAt)]),
        };

    private static CodingToolEvidence Receipt(string id, string tool, long sequence, string? path = null) =>
        new(id, tool, true, path, tool == "coding.command" ? 0 : null, "Prüfbeleg", sequence);
}
