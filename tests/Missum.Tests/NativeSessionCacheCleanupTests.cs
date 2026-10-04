using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class NativeSessionCacheCleanupTests
{
    [Fact]
    public async Task OfflineDeletionRemovesOnlyOwnedSnapshotsIncludingDelegatedSessionsAndKeepsDurableQueue()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var profile = Profile(environment.Directory);
        var target = Session(ChatMode.ClaudeScience);
        var other = Session(ChatMode.Coding);
        var root = Path.Combine(profile.NativeStateDirectory, "session-cache");
        Directory.CreateDirectory(root);
        var main = new string('a', 64);
        var child = new string('b', 64);
        var retained = new string('c', 64);
        var childId = Guid.NewGuid().ToString("D");
        var entries = new Dictionary<string, object>
        {
            [main] = new { sessionKey = NativeSessionCacheCleanupService.BuildSessionKey(target.Id.ToString("D"), "coding", target.CodingWorkspacePath), sessionId = target.Id.ToString("D") },
            [child] = new { sessionKey = "child-key", sessionId = childId, parentSessionId = target.Id.ToString("D") },
            [retained] = new { sessionKey = "retained-key", sessionId = other.Id.ToString("D") },
        };
        await File.WriteAllTextAsync(Path.Combine(root, "ownership.json"), JsonSerializer.Serialize(new { version = 1, entries }));
        foreach (var (identity, owner) in entries)
        {
            await File.WriteAllTextAsync(Path.Combine(root, identity + ".bin"), "KV fixture");
            await File.WriteAllTextAsync(Path.Combine(root, identity + ".json"), JsonSerializer.Serialize(owner));
        }
        var unrelated = Path.Combine(root, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep this file");
        var sends = 0;
        var service = new NativeSessionCacheCleanupService(profile, (_, _) => { sends++; return Task.FromResult(true); }, () => true);
        await service.DeleteSessionsAsync([target]);
        Assert.Equal(0, sends);
        Assert.False(File.Exists(Path.Combine(root, main + ".bin")));
        Assert.False(File.Exists(Path.Combine(root, child + ".bin")));
        Assert.True(File.Exists(Path.Combine(root, retained + ".bin")));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(unrelated));
        using var queue = JsonDocument.Parse(await File.ReadAllTextAsync(service.QueuePath));
        Assert.Equal(target.Id.ToString("D"), Assert.Single(queue.RootElement.EnumerateObject()).Name);
    }

    [Fact]
    public async Task OfflineDeletionAlsoRemovesVerifiedLegacyMetadataWithoutAReconstructableSessionKey()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var profile = Profile(environment.Directory);
        var target = Session(ChatMode.General);
        var retained = Session(ChatMode.General);
        var root = Path.Combine(profile.NativeStateDirectory, "session-cache");
        Directory.CreateDirectory(root);
        var legacy = Path.Combine(root, new string('d', 64) + ".bin");
        var other = Path.Combine(root, new string('e', 64) + ".bin");
        await File.WriteAllTextAsync(legacy, "legacy snapshot");
        await File.WriteAllTextAsync(other, "retained snapshot");
        await File.WriteAllTextAsync(Path.ChangeExtension(legacy, ".json"), JsonSerializer.Serialize(new { sessionId = target.Id.ToString("D"), tokens = 100 }));
        await File.WriteAllTextAsync(Path.ChangeExtension(other, ".json"), JsonSerializer.Serialize(new { sessionId = retained.Id.ToString("D"), tokens = 100 }));
        var service = new NativeSessionCacheCleanupService(profile, (_, _) => throw new InvalidOperationException("Offline deletion must not send HTTP."), () => true);
        await service.DeleteSessionsAsync([target]);
        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(Path.ChangeExtension(legacy, ".json")));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(Path.ChangeExtension(other, ".json")));
    }

    [Fact]
    public async Task FailedRuntimeDeleteRemainsQueuedAcrossReopenAndSuccessAcknowledgesIt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var profile = Profile(environment.Directory);
        var target = Session(ChatMode.General);
        var attempts = 0;
        var offline = new NativeSessionCacheCleanupService(profile, (_, _) => { attempts++; return Task.FromResult(false); }, () => false);
        await offline.DeleteSessionsAsync([target]);
        await offline.DeleteSessionsAsync([Session(ChatMode.General)]);
        Assert.Equal(1, attempts); // Project deletion cannot repeat a network timeout for each session.
        var freshDirectory = Path.Combine(environment.Directory, "reopened-profile");
        Directory.CreateDirectory(freshDirectory);
        var restartedProfile = profile with { DataDirectory = freshDirectory };
        File.Copy(offline.QueuePath, Path.Combine(freshDirectory, "NativeCacheDeletions.json"));
        NativeSessionCacheCleanupService.CacheDeletion? received = null;
        var restarted = new NativeSessionCacheCleanupService(restartedProfile, (request, _) =>
        {
            received = request;
            return Task.FromResult(true);
        }, () => false);
        Assert.True(await restarted.FlushAsync());
        Assert.NotNull(received);
        Assert.Contains(target.Id.ToString("D"), received.SessionIds);
        Assert.Equal(2, received.SessionIds.Length);
        using var queue = JsonDocument.Parse(await File.ReadAllTextAsync(restarted.QueuePath));
        Assert.Empty(queue.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task PendingDeletionIsSentAfterStartupAndBeforeTheRuntimeIsReturnedToItsCaller()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var sequence = new List<string>();
        var cleanup = new NativeSessionCacheCleanupService(Profile(environment.Directory), (_, _) =>
        {
            sequence.Add("delete");
            return Task.FromResult(true);
        }, () => true);
        await cleanup.DeleteSessionsAsync([Session(ChatMode.General)]);
        var running = false;
        using var runtime = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(running), _ =>
        {
            sequence.Add("start");
            running = true;
            return Task.CompletedTask;
        }, cacheCleanup: cleanup);
        await runtime.EnsureStartedAsync(new("http://localhost:8080"), CancellationToken.None);
        sequence.Add("model request may begin");
        Assert.Equal("start", sequence[0]);
        Assert.Equal("delete", sequence[1]);
        Assert.Equal("model request may begin", sequence[2]);
        Assert.Equal(3, sequence.Count);
    }

    [Fact]
    public async Task ProbeCooldownDoesNotSkipADeletionQueuedAfterThePreviousStart()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var sends = 0;
        var cleanup = new NativeSessionCacheCleanupService(Profile(environment.Directory), (_, _) =>
        {
            sends++;
            return Task.FromResult(true);
        }, () => true);
        using var runtime = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(true), _ => Task.CompletedTask,
            cacheCleanup: cleanup);
        var gateway = new Uri("http://localhost:8080");
        await runtime.EnsureStartedAsync(gateway, CancellationToken.None);
        await cleanup.DeleteSessionsAsync([Session(ChatMode.General)]);
        await runtime.EnsureStartedAsync(gateway, CancellationToken.None);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task QueueWriteFailureRetainsTheDeletionForALaterRetry()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var blocked = Path.Combine(environment.Directory, "blocked-profile");
        await File.WriteAllTextAsync(blocked, "file occupies directory");
        var profile = Profile(environment.Directory) with { DataDirectory = blocked };
        NativeSessionCacheCleanupService.CacheDeletion? received = null;
        var service = new NativeSessionCacheCleanupService(profile, (request, _) =>
        {
            received = request;
            return Task.FromResult(true);
        }, () => true);
        var target = Session(ChatMode.General);
        await Assert.ThrowsAnyAsync<IOException>(() => service.DeleteSessionsAsync([target]));
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);
        Assert.True(await service.FlushAsync());
        Assert.NotNull(received);
        Assert.Contains(target.Id.ToString("D"), received.SessionIds);
    }

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    public async Task SidebarProjectDeletionCleansEveryCommittedMemberAndPreservesTheOtherProject(ChatMode mode)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var workspace = Path.Combine(environment.Directory, "project");
        var retainedWorkspace = Path.Combine(environment.Directory, "retained-project");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(retainedWorkspace);
        var first = await chats.CreateSessionAsync("First", mode);
        var second = await chats.CreateSessionAsync("Second", mode);
        var retained = await chats.CreateSessionAsync("Retained", mode);
        await chats.SetCodingWorkspacePathAsync(first.Id, workspace, activateCoding: false);
        await chats.SetCodingWorkspacePathAsync(second.Id, workspace, activateCoding: false);
        await chats.SetCodingWorkspacePathAsync(retained.Id, retainedWorkspace, activateCoding: false);
        var group = (await chats.GetSessionAsync(first.Id))!.SessionGroupId!.Value;
        var deletedOwners = new List<string>();
        var cleanup = new NativeSessionCacheCleanupService(Profile(environment.Directory), async (request, token) =>
        {
            foreach (var id in request.SessionIds) Assert.Null(await chats.GetSessionAsync(Guid.Parse(id), token));
            deletedOwners.AddRange(request.SessionIds);
            return true;
        }, () => false);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { SelectedChatMode = mode });
        var coordinator = Coordinator(environment, settings, cleanup);
        // Exactly the native ProjectMenu path: delete its session members, then
        // remove the empty group. Workspace files are retained.
        foreach (var session in new[] { first, second })
            await HandleAsync(coordinator, "session.delete", new { sessionId = session.Id });
        await HandleAsync(coordinator, "session.groupDeleteEmpty", new { groupId = group });
        Assert.Contains(first.Id.ToString("D"), deletedOwners);
        Assert.Contains(second.Id.ToString("D"), deletedOwners);
        Assert.DoesNotContain(retained.Id.ToString("D"), deletedOwners);
        Assert.NotNull(await chats.GetSessionAsync(retained.Id));
        Assert.DoesNotContain(await chats.ListSessionGroupsAsync(), candidate => candidate.Id == group);
        Assert.True(Directory.Exists(workspace));
        Assert.True(Directory.Exists(retainedWorkspace));
    }

    [Fact]
    public async Task BulkDeletionAlsoCleansOnlyTheDeletedMode()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var target = await chats.CreateSessionAsync("Target", ChatMode.General);
        var retained = await chats.CreateSessionAsync("Retained", ChatMode.Coding);
        var owners = new List<string>();
        var cleanup = new NativeSessionCacheCleanupService(Profile(environment.Directory), (request, _) =>
        {
            owners.AddRange(request.SessionIds);
            return Task.FromResult(true);
        }, () => false);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await HandleAsync(Coordinator(environment, settings, cleanup), "session.clear", new { });
        Assert.Contains(target.Id.ToString("D"), owners);
        Assert.DoesNotContain(retained.Id.ToString("D"), owners);
        Assert.NotNull(await chats.GetSessionAsync(retained.Id));
    }

    [Fact]
    public async Task CommittedDatabaseDeletionRemainsSuccessfulWhenCacheQueueCannotBeWritten()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var target = await chats.CreateSessionAsync("Target", ChatMode.General);
        var blocked = Path.Combine(environment.Directory, "blocked");
        await File.WriteAllTextAsync(blocked, "fixture");
        var cleanup = new NativeSessionCacheCleanupService(Profile(environment.Directory) with { DataDirectory = blocked },
            (_, _) => Task.FromResult(true), () => true);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await HandleAsync(Coordinator(environment, settings, cleanup), "session.delete", new { sessionId = target.Id });
        Assert.Null(await chats.GetSessionAsync(target.Id));
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);
        Assert.True(await cleanup.FlushAsync());
    }

    private static Task HandleAsync(AssistantCoordinator coordinator, string command, object payload) =>
        coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, command, "kv-lifecycle",
            JsonSerializer.SerializeToElement(payload)), (_, _, _) => Task.CompletedTask);

    private static AssistantCoordinator Coordinator(TestEnvironment environment, SettingsCoordinator settings,
        NativeSessionCacheCleanupService cleanup) => new(environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), null, settings,
            new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance), cacheCleanup: cleanup);

    private static ChatSession Session(ChatMode mode) => new(Guid.NewGuid(), "Fixture", DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, mode, CodingWorkspacePath: "C:/fixture/project");

    private static AssistantRuntimeProfile Profile(string directory) => new("tests", "Missum", directory,
        new("http://localhost:8080"), 8081, 8082, Path.Combine(directory, "native"), Path.Combine(directory, "llama-server.exe"),
        Path.Combine(directory, "stack"), "Missum.tests");
}
