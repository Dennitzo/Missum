using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class ScientificResearchProgressTests
{
    private const string SourceUrl = "https://www.sqlite.org/wal.html";
    private const string Excerpt = "Readers do not block writers and a writer does not block readers.";
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    [Fact]
    public async Task SuccessfulFetchIsDurableBeforeFinalCheckpointAndReplayDoesNotDuplicateOrCertifyIt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (run, project) = await CreateRunAsync(environment);
        var repository = environment.Get<IScientificResearchRepository>();
        var store = new ScientificResearchProgressStore(repository, environment.Get<IMissumAiRunRepository>());
        await store.ApplyAsync(run, Start(run, project.Id));
        var fetched = Fetch(run, 20);
        await store.ApplyAsync(run, fetched);
        var archive = await repository.LoadArchiveSnapshotAsync(project.Id);
        Assert.Equal(SourceUrl, Assert.Single(archive.Works).CanonicalUrl);
        Assert.Equal("awaitingReview", archive.Works[0].ScreeningStatus);
        var evidence = Assert.Single(archive.Evidence);
        Assert.Equal(Excerpt, evidence.ExactExcerpt);
        Assert.Equal(Hash(Excerpt), evidence.ContentHash);
        Assert.Equal(ScientificResearchProgressStore.EvidenceLevel, evidence.EvidenceLevel);
        Assert.Equal("researchProgress", archive.Report?.ReportKind);
        Assert.Equal("unresolved", archive.Report?.ConclusionStatus);
        Assert.Equal("active", (await repository.GetProjectAsync(project.Id))?.Status);
        var checkpoint = await repository.GetLatestCheckpointAsync(project.Id);
        Assert.Equal(run.ServerRunId, checkpoint?.RunId);
        Assert.Equal("sources.active", checkpoint?.Stage);
        var result = await repository.LoadResultSnapshotAsync(project.Id);
        Assert.Empty(result.Claims);
        Assert.Empty(result.Verifications);

        // A reconstructed event consumer uses the durable cursor, not an in-memory set.
        var restored = new ScientificResearchProgressStore(repository, environment.Get<IMissumAiRunRepository>());
        await restored.ApplyAsync(run, fetched);
        await restored.ApplyAsync(run, Start(run, project.Id));
        Assert.Equal(checkpoint, await repository.GetLatestCheckpointAsync(project.Id));
        Assert.Single((await repository.LoadArchiveSnapshotAsync(project.Id)).Evidence);

        // Failed requests and search/snippet-only results are not source evidence.
        await restored.ApplyAsync(run, Fetch(run, 21, success: false));
        await restored.ApplyAsync(run, Fetch(run, 22, found: false));
        await restored.ApplyAsync(run, Fetch(run, 23, untrusted: false));
        await restored.ApplyAsync(run, Event(run, 24, RunEventTypes.ServerToolCompleted,
            new { tool = "web.search", success = true, result = new { results = new[] { new { url = SourceUrl, snippet = "Only a search snippet" } } } }));
        Assert.Equal(checkpoint, await repository.GetLatestCheckpointAsync(project.Id));
    }

    [Fact]
    public async Task FinalCheckpointReplacesIntermediateEvidenceAndCannotBeOverwrittenByLateFetchOrReplay()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (run, project) = await CreateRunAsync(environment);
        var repository = environment.Get<IScientificResearchRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var store = new ScientificResearchProgressStore(repository, runs);
        await store.ApplyAsync(run, Start(run, project.Id));
        await store.ApplyAsync(run, Fetch(run, 20));
        var result = JsonSerializer.SerializeToElement(new
        {
            projectId = project.Id, profile = "web", conclusionStatus = "provisionallySupported",
            problem = new { interpretedQuestion = "WAL concurrency" },
            sources = new[] { new { id = "S1", title = "SQLite WAL", url = SourceUrl } },
            findings = new[] { new { sourceId = "S1", claim = "WAL separates readers and writers.", excerpt = Excerpt } },
            researchGraph = new { nodes = new[] { new { id = "question-1", nodeType = "question", title = "Concurrency", status = "provisionallySupported" } }, edges = System.Array.Empty<object>() },
            checkpoint = new { id = "final-" + run.ServerRunId, phase = "synthesis" },
        }, Json);
        var finalEvent = Event(run, 30, RunEventTypes.ResearchCheckpointCreated, new { result });
        using var service = new MissumAiAssistantService(null!, null!, null!, null!, runs, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, NullLogger<MissumAiAssistantService>.Instance,
            scientificResearch: repository);
        await service.PersistResearchResultAsync(run, finalEvent, result, CancellationToken.None);
        var finalArchive = await repository.LoadArchiveSnapshotAsync(project.Id);
        Assert.Equal("scientificMarkdown", finalArchive.Report?.ReportKind);
        Assert.Equal("verifiedExcerpt", Assert.Single(finalArchive.Evidence).EvidenceLevel);
        Assert.Equal("WAL separates readers and writers.", Assert.Single(finalArchive.Evidence).NormalizedStatement);
        var finalCheckpoint = await repository.GetLatestCheckpointAsync(project.Id);
        Assert.Equal("synthesis", finalCheckpoint?.Stage);
        await store.ApplyAsync(run, Fetch(run, 40));
        await store.EndAsync(run, "cancelled");
        await service.PersistResearchResultAsync(run, finalEvent, result, CancellationToken.None);
        Assert.Equal(finalCheckpoint, await repository.GetLatestCheckpointAsync(project.Id));
        Assert.Equal(finalArchive.Report, (await repository.LoadArchiveSnapshotAsync(project.Id)).Report);

        // A later explicit research invocation in the same coding run is a new
        // phase, unlike replaying the already stored final tool result.
        await store.ApplyAsync(run, Start(run, project.Id) with { Id = 45 });
        Assert.Empty((await repository.LoadArchiveSnapshotAsync(project.Id)).Evidence);
        await store.ApplyAsync(run, Fetch(run, 46));
        await service.PersistResearchResultAsync(run,
            Event(run, 48, RunEventTypes.ResearchCheckpointCreated, new { result }), result, CancellationToken.None);
        Assert.Equal("scientificMarkdown", (await repository.LoadArchiveSnapshotAsync(project.Id)).Report?.ReportKind);
        Assert.NotEqual(finalCheckpoint?.Id, (await repository.GetLatestCheckpointAsync(project.Id))?.Id);

        // The server may reuse a question-derived checkpoint ID on a later run.
        // Its local identity must still belong to this new run, not the old report.
        var (nextRun, _) = await CreateRunAsync(environment, run.SessionId, run.CreatedAt.AddSeconds(1));
        await store.ApplyAsync(nextRun, Start(nextRun, project.Id));
        await store.ApplyAsync(nextRun, Fetch(nextRun, 50));
        await service.PersistResearchResultAsync(nextRun,
            Event(nextRun, 60, RunEventTypes.ResearchCheckpointCreated, new { result }), result, CancellationToken.None);
        var nextCheckpoint = await repository.GetLatestCheckpointAsync(project.Id);
        Assert.Equal(nextRun.ServerRunId, nextCheckpoint?.RunId);
        Assert.NotEqual(finalCheckpoint?.Id, nextCheckpoint?.Id);
    }

    [Fact]
    public async Task NewServerAttemptCanReuseLocalRunButOldAttemptCanNoLongerChangeResearch()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (run, project) = await CreateRunAsync(environment);
        var repository = environment.Get<IScientificResearchRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var store = new ScientificResearchProgressStore(repository, runs);
        await store.ApplyAsync(run, Start(run, project.Id));
        await store.ApplyAsync(run, Fetch(run, 20));
        var retry = run with { ServerRunId = "run-retry-" + Guid.NewGuid().ToString("N") };
        await runs.UpdateAsync(run.Id, retry.ServerRunId, 0, "running");
        await store.ApplyAsync(retry, Start(retry, project.Id));
        Assert.Empty((await repository.LoadArchiveSnapshotAsync(project.Id)).Evidence);
        await store.ApplyAsync(retry, Fetch(retry, 21));
        var current = await repository.GetLatestCheckpointAsync(project.Id);
        await store.ApplyAsync(run, Fetch(run, 1000));
        await store.EndAsync(run, "cancelled");
        Assert.False(await store.CanPersistResultAsync(run, Event(run, 1001, RunEventTypes.ResearchCheckpointCreated, new { }), project.Id));
        Assert.Equal(retry.ServerRunId, current?.RunId);
        Assert.Equal(current, await repository.GetLatestCheckpointAsync(project.Id));
    }

    [Fact]
    public async Task CancellationRetainsUnverifiedSourcesAndNewRunRejectsOlderRunAndOtherSessionEvents()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (oldRun, project) = await CreateRunAsync(environment);
        var repository = environment.Get<IScientificResearchRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var store = new ScientificResearchProgressStore(repository, runs);
        await store.ApplyAsync(oldRun, Start(oldRun, project.Id));
        await store.ApplyAsync(oldRun, Fetch(oldRun, 20));
        await store.EndAsync(oldRun, "cancelled");
        Assert.Equal("cancelled", (await repository.GetProjectAsync(project.Id))?.Status);
        Assert.Single((await repository.LoadArchiveSnapshotAsync(project.Id)).Evidence);
        await store.ApplyAsync(oldRun, Fetch(oldRun, 21));
        Assert.Equal("cancelled", (await repository.GetProjectAsync(project.Id))?.Status);

        var (newRun, _) = await CreateRunAsync(environment, oldRun.SessionId, oldRun.CreatedAt.AddSeconds(1));
        await store.ApplyAsync(newRun, Start(newRun, project.Id));
        Assert.Empty((await repository.LoadArchiveSnapshotAsync(project.Id)).Evidence);
        await store.ApplyAsync(newRun, Fetch(newRun, 40));
        var current = await repository.GetLatestCheckpointAsync(project.Id);
        await store.ApplyAsync(oldRun, Start(oldRun, project.Id) with { Id = 1000 });
        await store.ApplyAsync(oldRun, Fetch(oldRun, 1001));
        await store.EndAsync(oldRun, "failed");
        Assert.False(await store.CanPersistResultAsync(oldRun, Event(oldRun, 1002, RunEventTypes.ResearchCheckpointCreated, new { }), project.Id));
        var (otherRun, _) = await CreateRunAsync(environment);
        await store.ApplyAsync(otherRun, Start(otherRun, project.Id));
        await store.ApplyAsync(newRun, Fetch(otherRun, 1003));
        Assert.False(await store.CanPersistResultAsync(otherRun, Event(otherRun, 1004, RunEventTypes.ResearchCheckpointCreated, new { }), project.Id));
        Assert.Equal(current, await repository.GetLatestCheckpointAsync(project.Id));
        Assert.Single((await repository.LoadArchiveSnapshotAsync(project.Id)).Evidence);
        await store.ApplyAsync(newRun, Event(newRun, 41, RunEventTypes.RunFailed, new { errorCode = "fixture.failure" }));
        Assert.Equal("blocked", (await repository.GetProjectAsync(project.Id))?.Status);
        Assert.Equal("sources.failed", (await repository.GetLatestCheckpointAsync(project.Id))?.Stage);
        Assert.Equal("retrievedExcerpt", Assert.Single((await repository.LoadArchiveSnapshotAsync(project.Id)).Evidence).EvidenceLevel);
    }

    private static async Task<(MissumAiRunRecord Run, ScientificResearchProject Project)> CreateRunAsync(
        TestEnvironment environment, Guid? sessionId = null, DateTimeOffset? startedAt = null)
    {
        var chats = environment.Get<IChatRepository>();
        var session = sessionId ?? (await chats.CreateSessionAsync("Forschung", ChatMode.ClaudeScience)).Id;
        var assistant = await chats.AddMessageAsync(session, ChatRole.Assistant, "", MessageStatus.Streaming);
        var now = startedAt ?? DateTimeOffset.UtcNow;
        var run = new MissumAiRunRecord(Guid.NewGuid(), session, assistant.Id, null, Guid.NewGuid().ToString("N"),
            "run-" + Guid.NewGuid().ToString("N"), 0, "running", "fixture/general", null, now, now);
        await environment.Get<IMissumAiRunRepository>().CreateAsync(run);
        var repository = environment.Get<IScientificResearchRepository>();
        var projectId = $"research-{session:N}";
        var project = await repository.GetProjectAsync(projectId) ?? new ScientificResearchProject(projectId, session,
            "web", "WAL?", "WAL?", "readOnlyResearch", "multiPath", "active", 1, 1, now, now);
        await repository.UpsertProjectAsync(project);
        return (run, project);
    }

    private static RunEvent Start(MissumAiRunRecord run, string projectId) => Event(run, 10,
        RunEventTypes.ResearchProblemInterpreted, new { projectId, state = "deepResearchInterpretation" });
    private static RunEvent Fetch(MissumAiRunRecord run, long id, bool success = true, bool found = true, bool untrusted = true) =>
        Event(run, id, RunEventTypes.ServerToolCompleted, new
        {
            tool = "web.fetch", success, result = new { url = SourceUrl, found, isUntrusted = untrusted,
                retrievedAt = DateTimeOffset.UtcNow, matches = new[] { new { text = Excerpt, query = "readers" } } },
        });
    private static RunEvent Event(MissumAiRunRecord run, long id, string type, object data) =>
        new(id, run.ServerRunId!, type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data, Json));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
