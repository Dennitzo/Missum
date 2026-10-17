using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Memory;
using Missum.Core.Models;
using Missum.Infrastructure.Memory;
using Missum.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace Missum.Tests;

public sealed class ProjectContextServiceTests
{
    [Fact]
    public async Task StoreUsesCompareAndSwapAndDeduplicatesReportsWithAnIndexedProjectBoundary()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        var initial = Snapshot("project-a", 1, "Initial knowledge");
        Assert.True(await store.PublishAsync(initial, 0));
        var writes = await Task.WhenAll(store.PublishAsync(Snapshot("project-a", 2, "First"), 1),
            store.PublishAsync(Snapshot("project-a", 2, "Second"), 1));
        Assert.Single(writes, static written => written);
        Assert.False(await store.PublishAsync(Snapshot("project-a", 2, "Stale"), 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PublishAsync(Snapshot("project-a", 3, "Invalid") with { ContentHash = "wrong" }, 2));

        var contribution = new ProjectContextContribution("project-a", Guid.NewGuid(), Guid.NewGuid(), 1,
            "Quantitative Prüfung bleibt offen; eindeutiger Originalbeleg.", DateTimeOffset.UtcNow);
        await store.CaptureAsync(contribution);
        await store.CaptureAsync(contribution with { Text = "Duplicate must not replace the report" });
        await store.CaptureAsync(contribution with { MessageRevision = 0, Text = "Stale report" });
        Assert.Equal(contribution, await store.ReadAsync("project-a", contribution.MessageId));
        Assert.Null(await store.ReadAsync("project-b", contribution.MessageId));
        Assert.Single(await store.SearchAsync("project-a", "Quantitative"));
        Assert.Empty(await store.SearchAsync("project-b", "Quantitative"));
        await store.CaptureAsync(contribution with { MessageRevision = 2, Text = "Ergebnis korrigiert; Unsicherheit bleibt." });
        Assert.Empty(await store.SearchAsync("project-a", "Quantitative"));
        Assert.Single(await store.SearchAsync("project-a", "Unsicherheit"));
        Assert.Empty(await store.SearchAsync("project-a", "\" OR *"));
        Assert.Single(await store.ListAsync("project-a"));
    }

    [Fact]
    public async Task HistoryReadSearchAndCaptureVerifyCurrentSessionOwnershipAndRetainOriginalMath()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var chats = environment.Get<IChatRepository>();
        var first = Workspace(environment, "first");
        var second = Workspace(environment, "second");
        var session = await chats.CreateSessionAsync("Research", ChatMode.ClaudeScience);
        await chats.SetCodingWorkspacePathAsync(session.Id, first, false);
        await chats.AddMessageAsync(session.Id, ChatRole.User, "Verify the equation", MessageStatus.Completed);
        const string original = "Eindeutige Hypothese: $F=ma$ bleibt ohne Simulationsbeleg ausdrücklich ungeprüft.";
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, original, MessageStatus.Completed);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = environment.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Valid SQLite JSON, deliberately not a tool-step array. Text-only
            // history access must never deserialize this legacy detail column.
            command.CommandText = "UPDATE chat_messages SET tool_steps_json='{}' WHERE id=$id;";
            command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        await using var service = CreateService(store, chats, memory);
        await service.GetAsync(first, 1_048_576, "selected-model", "coding");
        await service.CaptureAsync(first, message with { Content = "Invented claim", Revision = 99 });
        var read = await service.ReadAsync(first, message.Id);
        Assert.NotNull(read);
        Assert.Contains(original, read.Text, StringComparison.Ordinal);
        Assert.Contains("Status: Completed", read.Text, StringComparison.Ordinal);
        Assert.Contains(message.Id.ToString("D"), read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Invented claim", read.Text, StringComparison.Ordinal);
        Assert.Single(await service.SearchAsync(first, "Eindeutige"));
        Assert.Null(await service.ReadAsync(second, message.Id));
        Assert.Empty(await service.SearchAsync(second, "Eindeutige"));

        // A stale or forged index entry cannot bypass live session ownership.
        await store.CaptureAsync(new(ProjectMemoryScope.FromWorkspace(second).ProjectKey, session.Id, message.Id,
            message.Revision, original, DateTimeOffset.UtcNow));
        Assert.Empty(await service.SearchAsync(second, "Eindeutige"));
        await chats.SetCodingWorkspacePathAsync(session.Id, second, false);
        Assert.Null(await service.ReadAsync(first, message.Id));
        Assert.Empty(await service.SearchAsync(first, "Eindeutige"));
        Assert.NotNull(await service.ReadAsync(second, message.Id));
        await chats.DeleteSessionAsync(session.Id);
        Assert.Null(await service.ReadAsync(second, message.Id));
    }

    [Fact]
    public async Task RepeatedFinalReportsDoNotCreateDuplicateSnapshotsButNewRevisionsDo()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var chats = environment.Get<IChatRepository>();
        var workspace = Workspace(environment, "project");
        var session = await chats.CreateSessionAsync("Project", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, false);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Original uncertainty remains.", MessageStatus.Completed);
        await using var service = CreateService(store, chats, memory);
        await service.GetAsync(workspace, 1_048_576, "selected-model", "coding");
        await service.CaptureAsync(workspace, message);
        var key = ProjectMemoryScope.FromWorkspace(workspace).ProjectKey;
        var first = await store.GetAsync(key);
        await service.CaptureAsync(workspace, message);
        Assert.Equal(first, await store.GetAsync(key));
        await chats.UpdateMessageAsync(message.Id, "Corrected report; no experimental confirmation.", MessageStatus.Completed);
        var revised = (await chats.GetMessageTextAsync(message.Id))!;
        await service.CaptureAsync(workspace, revised);
        var latest = await store.GetAsync(key);
        Assert.NotNull(latest);
        Assert.Equal(first!.Generation + 1, latest.Generation);
        Assert.Contains("Corrected report", latest.Text, StringComparison.Ordinal);
        Assert.Equal(revised.Revision, Assert.Single(await store.ListAsync(key)).MessageRevision);
    }

    [Fact]
    public async Task InitialBootstrapIsBoundedAndRemainingHistoryResumesWithoutPublishingOldReports()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var chats = environment.Get<IChatRepository>();
        var workspace = Workspace(environment, "large-project");
        for (var sessionIndex = 0; sessionIndex < 16; sessionIndex++)
        {
            var session = await chats.CreateSessionAsync("Session " + sessionIndex.ToString(CultureInfo.InvariantCulture), ChatMode.Coding);
            await chats.SetCodingWorkspacePathAsync(session.Id, workspace, false);
            for (var messageIndex = 0; messageIndex < 34; messageIndex++)
                await chats.AddMessageAsync(session.Id, messageIndex % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                    $"Visible original record {sessionIndex.ToString(CultureInfo.InvariantCulture)}/{messageIndex.ToString(CultureInfo.InvariantCulture)}.", MessageStatus.Completed);
        }
        var key = ProjectMemoryScope.FromWorkspace(workspace).ProjectKey;
        await using (var service = CreateService(store, chats, memory))
        {
            Assert.NotNull(await service.GetAsync(workspace, 1_048_576, "selected-model", "coding"));
            Assert.Equal(12, (await store.ListIndexProgressAsync(key)).Count);
            Assert.Equal(12 * 32, await ContributionCountAsync(environment, key));
            var before = await store.GetAsync(key);
            Assert.False(await service.IndexProjectBatchAsync(workspace));
            Assert.Equal(14, (await store.ListIndexProgressAsync(key)).Count);
            Assert.Equal(before, await store.GetAsync(key));
        }
        await using (var restarted = CreateService(store, chats, memory))
        {
            var before = await store.GetAsync(key);
            for (var batch = 0; batch < 24 && !await restarted.IndexProjectBatchAsync(workspace); batch++) { }
            Assert.Equal(16 * 34, await ContributionCountAsync(environment, key));
            Assert.All(await store.ListIndexProgressAsync(key), progress => Assert.True(progress.Completed));
            Assert.Equal(before, await store.GetAsync(key));
        }
    }

    [Fact]
    public async Task MoreThanOnePageOfNewMessagesIsCaughtUpAcrossRestartWithoutLosingTheMiddle()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var chats = environment.Get<IChatRepository>();
        var workspace = Workspace(environment, "catch-up");
        var session = await chats.CreateSessionAsync("Many new records", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, false);
        for (var index = 0; index < 70; index++)
            await chats.AddMessageAsync(session.Id, ChatRole.User, "Initial " + index.ToString(CultureInfo.InvariantCulture), MessageStatus.Completed);
        var key = ProjectMemoryScope.FromWorkspace(workspace).ProjectKey;
        await using (var service = CreateService(store, chats, memory))
        {
            await service.GetAsync(workspace, 1_048_576, "selected-model", "coding");
            for (var batch = 0; batch < 8 && !await service.IndexProjectBatchAsync(workspace); batch++) { }
            Assert.Equal(70, await ContributionCountAsync(environment, key));
            for (var index = 0; index < 75; index++)
                await chats.AddMessageAsync(session.Id, ChatRole.User, "New " + index.ToString(CultureInfo.InvariantCulture), MessageStatus.Completed);
            Assert.False(await service.IndexProjectBatchAsync(workspace));
            Assert.NotNull(Assert.Single(await store.ListIndexProgressAsync(key)).CatchUpBefore);
        }
        await using (var restarted = CreateService(store, chats, memory))
        {
            // Even a second growth burst during catch-up is revisited afterwards.
            for (var index = 0; index < 10; index++)
                await chats.AddMessageAsync(session.Id, ChatRole.User, "Latest " + index.ToString(CultureInfo.InvariantCulture), MessageStatus.Completed);
            for (var batch = 0; batch < 12 && !await restarted.IndexProjectBatchAsync(workspace); batch++) { }
            Assert.Equal(155, await ContributionCountAsync(environment, key));
            var final = Assert.Single(await store.ListIndexProgressAsync(key));
            Assert.True(final.Completed);
            Assert.Null(final.CatchUpBefore);
        }
    }

    [Fact]
    public async Task FullOriginalHistoryIsSearchableAndReadableBeyondTheSummaryExcerpt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var chats = environment.Get<IChatRepository>();
        var workspace = Workspace(environment, "long-report");
        var session = await chats.CreateSessionAsync("Long report", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, false);
        var original = new string('a', 80_000) + " latequantitativefinding $F=ma$ remains unverified. " + new string('b', 80_000);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, original, MessageStatus.Completed);
        await using var service = CreateService(store, chats, memory);
        await service.GetAsync(workspace, 1_048_576, "selected-model", "coding");
        var key = ProjectMemoryScope.FromWorkspace(workspace).ProjectKey;
        var summary = Assert.Single(await store.ListAsync(key));
        Assert.DoesNotContain("latequantitativefinding", summary.Text, StringComparison.Ordinal);
        Assert.True(summary.Text.Length < 16_000);
        var result = Assert.Single(await service.SearchAsync(workspace, "latequantitativefinding"));
        Assert.Contains("latequantitativefinding", result.Text, StringComparison.Ordinal);
        Assert.Contains("$F=ma$", (await service.ReadAsync(workspace, message.Id))!.Text, StringComparison.Ordinal);
        Assert.Contains(original, (await service.ReadAsync(workspace, message.Id))!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmallModelSummaryBudgetsInputsAndOnlyAdvancesThroughReportsActuallySent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var chats = environment.Get<IChatRepository>();
        var workspace = Workspace(environment, "small-model");
        var session = await chats.CreateSessionAsync("Small model", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, false);
        var messages = new List<ChatMessage>();
        for (var index = 0; index < 3; index++)
            messages.Add(await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
                "Offene Hypothese, unbestätigt. " + new string('x', 10000) + " Abschließendes Ergebnis bleibt offen.", MessageStatus.Completed));
        var requests = new List<ProjectContextSummarizeRequest>();
        await using var service = new ProjectContextService(store, chats, memory, (request, _) =>
        {
            requests.Add(request);
            Assert.InRange(request.MaximumTokens, 256, 4096);
            var sourceBytes = Encoding.UTF8.GetByteCount(request.PreviousText)
                + request.Contributions.Sum(Encoding.UTF8.GetByteCount);
            Assert.True(sourceBytes + request.MaximumTokens + 2048 <= 4096);
            Assert.Equal(2, request.Contributions.Count); // Metadata and one bounded report.
            Assert.Contains("AI-berichteter", request.Contributions[1], StringComparison.Ordinal);
            Assert.Contains("Abschließendes Ergebnis bleibt offen", request.Contributions[1], StringComparison.Ordinal);
            return Task.FromResult(new ProjectContextSummarizeResult(request.ProjectKey, "Die Hypothese bleibt ausdrücklich offen."));
        }, NullLogger<ProjectContextService>.Instance);
        await service.GetAsync(workspace, 4096, "small-selected-model", "coding");
        for (var batch = 0; batch < 5 && !await service.RefreshProjectAsync(workspace, 4096, "small-selected-model", "coding"); batch++) { }
        Assert.Equal(3, requests.Count);
        Assert.All(messages, message => Assert.Single(requests, request => request.Contributions[1]
            .Contains(message.Id.ToString("D"), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task IdleSemanticMergeUsesSelectedModelAndPreservesOriginsInsteadOfJustAppendingReportFragments()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var chats = environment.Get<IChatRepository>();
        var workspace = Workspace(environment, "project");
        var session = await chats.CreateSessionAsync("Research", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, false);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Offene Hypothese ohne experimentellen Beleg.", MessageStatus.Completed);
        ProjectContextSummarizeRequest? received = null;
        const string semantic = "Die Hypothese bleibt offen. Ein experimenteller Beleg fehlt; die Originalnachricht dokumentiert diese Grenze.";
        await using var service = new ProjectContextService(store, chats, memory, (request, _) =>
        {
            received = request;
            return Task.FromResult(new ProjectContextSummarizeResult(request.ProjectKey, semantic));
        }, NullLogger<ProjectContextService>.Instance);
        await service.GetAsync(workspace, 1_048_576, "selected-model", "coding");
        Assert.Null(received);
        Assert.True(await service.RefreshProjectAsync(workspace, 1_048_576, "selected-model", "coding"));
        Assert.NotNull(received);
        Assert.Equal("selected-model", received.ModelId);
        Assert.Equal("coding", received.Role);
        Assert.Contains(received.Contributions, text => text.Contains(message.Id.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(received.Contributions, text => text.Contains("AI-berichteter", StringComparison.Ordinal));
        var stored = await store.GetAsync(ProjectMemoryScope.FromWorkspace(workspace).ProjectKey);
        Assert.Equal(semantic, stored!.Text);
        Assert.NotNull(stored.SourceWatermark);
    }

    [Fact]
    public void RequestBudgetHandlesSmallModelsAndUnicodeWithoutBreakingCharacters()
    {
        Assert.InRange(ProjectContextService.TokenBudget(1_048_576), 1, 8192);
        Assert.True(ProjectContextService.TokenBudget(32768) < ProjectContextService.TokenBudget(1_048_576));
        for (var budget = 0; budget < 64; budget++)
        {
            var bounded = ProjectContextService.Bound(string.Concat(Enumerable.Repeat("α🙂 ", 100)), budget);
            Assert.InRange(Encoding.UTF8.GetByteCount(bounded), 0, budget);
            Assert.False(bounded.Length > 0 && char.IsHighSurrogate(bounded[^1]));
        }
    }

    [Fact]
    public async Task CurrentPublicationMetadataIsVisibleBeforeAnIdleModelPassAndSmallerModelsStayBounded()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var workspace = Workspace(environment, "publication");
        var path = Path.Combine(workspace, "Publikation.md");
        await File.WriteAllTextAsync(path, "# Originaler Stand");
        await using var service = CreateService(store, environment.Get<IChatRepository>(), memory);
        var first = await service.GetAsync(workspace, 1_048_576, "selected-model", "coding");
        await File.WriteAllTextAsync(path, "# Neuer Publikationsstand mit verworfener Hypothese");
        var changed = await service.GetAsync(workspace, 1_048_576, "selected-model", "coding");
        Assert.Contains("Neuer Publikationsstand", changed!.Text, StringComparison.Ordinal);
        Assert.NotEqual(first!.ContentHash, changed.ContentHash);
        var smaller = await service.GetAsync(workspace, 32768, "smaller-model", "coding");
        Assert.True(Encoding.UTF8.GetByteCount(smaller!.Text) <= ProjectContextService.TokenBudget(32768));
        Assert.Equal(first.Generation, (await store.GetAsync(first.ProjectKey))!.Generation);
    }

    [Fact]
    public async Task SemanticMergeCannotOverwriteAReportThatArrivesWhileTheModelIsSummarizing()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new SqliteProjectContextStore(environment.Get<SqliteDatabase>());
        await using var memory = CreateMemory(environment);
        var workspace = Workspace(environment, "concurrent");
        await using var service = new ProjectContextService(store, environment.Get<IChatRepository>(), memory, async (request, token) =>
        {
            var current = (await store.GetAsync(request.ProjectKey, token))!;
            await store.PublishAsync(Snapshot(request.ProjectKey, current.Generation + 1, "Newer original report"), current.Generation, token);
            return new ProjectContextSummarizeResult(request.ProjectKey, "Obsolete model summary");
        }, NullLogger<ProjectContextService>.Instance);
        var first = await service.GetAsync(workspace, 1_048_576, "selected-model", "coding");
        Assert.False(await service.RefreshProjectAsync(workspace, 1_048_576, "selected-model", "coding"));
        Assert.Equal("Newer original report", (await store.GetAsync(first!.ProjectKey))!.Text);
    }

    private static ProjectContextService CreateService(IProjectContextStore store, IChatRepository chats, IProjectMemoryStore memory) =>
        new(store, chats, memory, static (_, _) => throw new InvalidOperationException("No foreground summary call is allowed."), NullLogger<ProjectContextService>.Instance);

    private static SqliteProjectMemoryStore CreateMemory(TestEnvironment environment)
    {
        var memory = new SqliteProjectMemoryStore(new ProjectMemoryOptions { SharedDataDirectory = Path.Combine(environment.Directory, "project-memory") });
        environment.TrackDatabase(memory.DatabasePath);
        return memory;
    }

    private static string Workspace(TestEnvironment environment, string name)
    {
        var path = Path.Combine(environment.Directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProjectContextSnapshot Snapshot(string key, long generation, string text) =>
        new(key, generation, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), text, null, DateTimeOffset.UtcNow);

    private static async Task<long> ContributionCountAsync(TestEnvironment environment, string key)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = environment.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM project_context_contributions WHERE project_key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
