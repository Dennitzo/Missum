using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ProjectContextTests
{
    [Fact]
    public async Task NewRunAdmissionWaitsForAnIdleCacheResetAndThenPreemptsBackgroundSummary()
    {
        using var work = new ProjectContextSummaryWork();
        using var reset = await work.EnterAdmissionAsync(CancellationToken.None);
        var newRunAdmission = work.EnterAdmissionAsync(CancellationToken.None);
        Assert.False(newRunAdmission.IsCompleted);
        reset.Dispose();
        using var main = await newRunAdmission;
        var summary = work.TryBegin(CancellationToken.None);
        Assert.NotNull(summary);
        work.Preempt();
        Assert.True(summary.IsCancellationRequested);
        work.End(summary);
    }

    [Fact]
    public void OriginalProjectHistoryIsOnlyOfferedWithBoundProjectScopeAndClientSupport()
    {
        var catalog = new AgentToolCatalog();
        var request = Request(Snapshot("Project finding", 1)) with { ClientCapabilities = ["project.history"] };
        var history = Assert.Single(catalog.GetAvailableTools(request), tool => tool.Name == ClientToolNames.ProjectHistory);
        Assert.False(history.ServerSide);
        Assert.Equal(ToolRiskClass.ReadOnly, history.RiskClass);
        Assert.DoesNotContain(catalog.GetAvailableTools(request with { ProjectContext = null }), tool => tool.Name == ClientToolNames.ProjectHistory);
        Assert.DoesNotContain(catalog.GetAvailableTools(request with { ClientCapabilities = [] }), tool => tool.Name == ClientToolNames.ProjectHistory);
        Assert.DoesNotContain("projectKey", history.Schema.GetRawText(), StringComparison.Ordinal);
        catalog.Validate(history, JsonSerializer.SerializeToElement(new { operation = "search", query = "evidence", limit = 5 }));
        catalog.Validate(history, JsonSerializer.SerializeToElement(new { operation = "read", messageId = Guid.NewGuid().ToString("D") }));
        Assert.Throws<ArgumentException>(() => catalog.Validate(history, JsonSerializer.SerializeToElement(new { operation = "read", messageId = "not-an-id" })));
    }

    [Fact]
    public void ProjectKnowledgeIsAStablePrefixWhileNewKnowledgeIsAppended()
    {
        var first = Snapshot("Original finding", 1);
        var latest = Snapshot("Original finding; another verified result", 2);
        var initial = new AgentRunCheckpoint([new("system", "Policy"), new("user", "Task")], 0, 0, 0, 0);
        var bound = ProjectContextPolicy.Bind(initial, first, "epoch-a");
        Assert.Equal("Policy", bound.Messages[0].Content);
        Assert.True(ProjectContextPolicy.IsBase(bound.Messages[1]));
        Assert.Equal("Task", bound.Messages[^1].Content);
        var updated = ProjectContextPolicy.Bind(bound, latest, "epoch-a");
        Assert.Equal(ProjectContextPolicy.Create(first), ProjectContextPolicy.Create(first with { Generation = 99 }));
        Assert.Equal(first, updated.ProjectContext);
        Assert.Equal(bound.Messages[1], updated.Messages[1]);
        Assert.Single(updated.Messages, ProjectContextPolicy.IsUpdate);
        Assert.Equal(updated.Messages, ProjectContextPolicy.Bind(updated, latest, "epoch-a").Messages);
        var rebased = ProjectContextPolicy.Rebase(updated.Messages, latest);
        Assert.Single(rebased, ProjectContextPolicy.IsBase);
        Assert.DoesNotContain(rebased, ProjectContextPolicy.IsUpdate);
        Assert.Contains(latest.Text, rebased[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchKeysAreIndependentAndKeepModelCompatibilityOutsideTheLogicalScope()
    {
        var first = ModelRuntimeClient.BuildSessionCacheKey("session-a", "coding", "C:/Work", "project-a");
        Assert.StartsWith("missum-project-session-v1-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, ModelRuntimeClient.BuildSessionCacheKey("session-b", "coding", "C:/Work", "project-a"));
        Assert.NotEqual(first, ModelRuntimeClient.BuildSessionCacheKey("session-a", "coding", "C:/Work", "project-b"));
        Assert.NotEqual(first, ModelRuntimeClient.BuildSessionCacheKey("session-a", "coding", "C:/Other", "project-a"));
        Assert.Equal(first, ModelRuntimeClient.BuildSessionCacheKey("session-a", "CODING", "c:\\work\\", "project-a"));
    }

    [Fact]
    public void InvalidSnapshotHashIsRejectedWithoutRewritingItsText()
    {
        var valid = Snapshot("An unconfirmed hypothesis remains unconfirmed.", 3);
        ProjectContextPolicy.Validate(valid);
        Assert.Throws<ArgumentException>(() => ProjectContextPolicy.Validate(valid with { Text = "Different statement" }));
        Assert.Throws<ArgumentException>(() => ProjectContextPolicy.Validate(valid with { Generation = -1 }));
    }

    [Fact]
    public void MillionTokenModelDoesNotAcquireAnArtificialWorkingContextCap()
    {
        var project = ProjectContextPolicy.Create(Snapshot("Verified finding", 1));
        LmChatMessage[] messages = [new("system", "Policy"), project, new("user", "Task"),
            new("assistant", new string('x', 500_000)), new("user", "Continue")];
        Assert.Null(CodingContextCompactor.Plan(messages, 1_048_576));
        var prepared = ContextPlanner.Prepare(messages, 1_048_576, null, preserveConversationPrefix: true);
        Assert.False(prepared.WasCompacted);
        Assert.Equal(messages, prepared.Messages);
    }

    [Fact]
    public void CompactionPreservesCanonicalProjectPrefixAndCurrentTask()
    {
        var project = ProjectContextPolicy.Create(Snapshot("Project-specific confirmed finding", 1));
        LmChatMessage[] messages = [new("system", "Policy"), project, new("user", "Old task"),
            new("assistant", new string('x', 110_000)), new("user", "Current task")];
        var plan = CodingContextCompactor.Plan(messages, 32768);
        Assert.NotNull(plan);
        var compacted = CodingContextCompactor.Complete(plan, "Prior result, evidence references and unresolved issue");
        Assert.Equal(messages.Take(2), compacted.Take(2));
        Assert.Equal("Current task", compacted[^1].Content);
        Assert.DoesNotContain(plan.SummaryRequest, message => message.Content?.Contains(ProjectContextPolicy.Marker, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EpochResetRetiresOnlyContinuationHeadsAndPreservesArchivedHistory()
    {
        using var fixture = new TestServerContext();
        var repository = new RunRepository(fixture.Database, new RunEventNotifier());
        var context = Snapshot("Verified project knowledge", 1);
        var request = Request(context);
        var original = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var checkpoint = ProjectContextPolicy.Bind(new([new("system", "Policy"), new("user", "Task"),
            new("assistant", "Verified reply")], 1, 0, 200, 30), context, await repository.GetContextCacheEpochAsync());
        await repository.SaveCheckpointAsync(original, checkpoint);
        Assert.True(await repository.FinalizeConversationAsync(original, new(null, "model", 200, 30)));
        Assert.Null(await repository.GetCheckpointAsync(original));
        var second = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        Assert.NotNull(await repository.GetSessionContextAsync(second, request with
        { CodingOptions = request.CodingOptions! with { WorkspacePath = "c:\\project\\" } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ResetContextCacheEpochAsync("epoch-new"));
        await repository.CancelAsync(second);
        Assert.True(await repository.ResetContextCacheEpochAsync("epoch-new"));
        Assert.False(await repository.ResetContextCacheEpochAsync("epoch-new"));
        var third = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        Assert.Null(await repository.GetSessionContextAsync(third, request));
        Assert.Equal(RunState.Completed, (await repository.GetAsync(original))!.State);
        Assert.Contains(await repository.GetEventsAfterAsync(original, 0), item => item.Type == RunEventTypes.RunCompleted);
        var otherProject = request with { ProjectContext = context with { ProjectKey = "another-project" } };
        Assert.Null(await repository.GetSessionContextAsync(third, otherProject));
    }

    private static RunRequest Request(ProjectContextRef context) => new(MissumAiProtocol.Version, RunMode.Coding,
        [new("user", [new("text", "Task")])], SessionId: "session", ProjectContext: context,
        CodingOptions: new(WorkspacePath: "C:/Project", ContinueSessionContext: true));

    private static ProjectContextRef Snapshot(string text, long generation) => new("project", generation,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), text);
}
