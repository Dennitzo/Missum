using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class AssistantClientStateTests
{
    [Fact]
    public async Task DamagedClientRecordsDoNotDiscardValidPeersOrPreventOpeningChats()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = Guid.NewGuid();
        var path = Path.Combine(environment.Directory, "ClientState", "views.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var records = new Dictionary<string, object?>
        {
            ["valid"] = new { mode = 1, model = "preserved-model", activeSession = session,
                drafts = new Dictionary<string, string> { [session.ToString("D")] = "Preserved peer draft" } },
            ["null-record"] = null,
            ["nullable-record"] = new { mode = 999, model = "\0invalid", reasoning = (object?)null, drafts = (object?)null },
            ["malformed-record"] = new { activeSession = "not-a-guid" },
            ["filtered"] = new
            {
                reasoning = new Dictionary<string, string?> { ["model"] = null, ["good-model"] = "high", ["invalid\0model"] = "low" },
                drafts = new Dictionary<string, string?> { ["not-a-guid"] = "Invalid", [session.ToString("B")] = "Canonical draft",
                    [Guid.NewGuid().ToString("D")] = null, [Guid.NewGuid().ToString("D")] = new string('x', 100_001) },
            },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(records));
        using var views = new AssistantClientStateStore(environment.Directory);
        var defaults = new AppSettings { SelectedModel = "default-model" };

        Assert.Equal("Preserved peer draft", views.Draft("valid", session, string.Empty, defaults));
        Assert.Equal(session, views.Resolve("valid", defaults).ActiveSessionId);
        Assert.Equal("preserved-model", views.Resolve("valid", defaults).SelectedModel);
        foreach (var id in new[] { "null-record", "nullable-record", "malformed-record" })
        {
            var resolved = views.Resolve(id, defaults);
            Assert.Equal(ChatMode.General, resolved.SelectedChatMode);
            Assert.Equal("default-model", resolved.SelectedModel);
            Assert.Empty(views.Draft(id, session, "Native legacy draft", defaults));
        }
        Assert.Equal("Canonical draft", views.Draft("filtered", session, string.Empty, defaults));
        Assert.Equal("high", Assert.Single(views.Get("filtered", defaults).Reasoning).Value);
        Assert.Empty(views.Resolve("filtered", defaults).ReasoningEffortsByModel);
        await views.SaveDraftAsync("nullable-record", session, "Recovery draft", defaults, CancellationToken.None);
        using var restarted = new AssistantClientStateStore(environment.Directory);
        Assert.Equal("Recovery draft", restarted.Draft("nullable-record", session, string.Empty, defaults));
        Assert.Equal("Preserved peer draft", restarted.Draft("valid", session, string.Empty, defaults));
    }

    [Fact]
    public async Task FailedDraftSaveKeepsTheLastPersistedInMemoryDraftAndCleansTemporaryFile()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        var session = Guid.NewGuid();
        var defaults = new AppSettings();
        await views.SaveDraftAsync("mac", session, "Saved draft", defaults, CancellationToken.None);
        var path = Path.Combine(environment.Directory, "ClientState", "views.json");
        File.Delete(path);
        Directory.CreateDirectory(path);

        var failure = await Record.ExceptionAsync(() => views.SaveDraftAsync("mac", session, "Failed edit", defaults, CancellationToken.None));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("Saved draft", views.Draft("mac", session, string.Empty, defaults));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task BrowserNavigationAndModelChoicesStayIndependentFromOtherDevicesAfterRestart()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var desktopSession = Guid.NewGuid();
        await settings.UpdateAsync(current => current with { ActiveSessionId = desktopSession,
            ActiveGeneralSessionId = desktopSession, SelectedModel = "desktop-model" });
        using var views = new AssistantClientStateStore(environment.Directory);
        var macSession = Guid.NewGuid();
        var peerSession = Guid.NewGuid();
        using (AssistantClientExecutionScope.Enter(views, "mac-tab-1"))
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with
            {
                SelectedChatMode = ChatMode.Coding, ActiveSessionId = macSession,
                ActiveCodingSessionId = macSession, SelectedModel = "mac-model", IsAssistantSessionPaneOpen = false,
            }, CancellationToken.None);
        using (AssistantClientExecutionScope.Enter(views, "other-tab-2"))
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with
            {
                SelectedChatMode = ChatMode.ClaudeScience, ActiveSessionId = peerSession,
                ActiveClaudeScienceSessionId = peerSession, SelectedModel = "peer-model",
            }, CancellationToken.None);

        using var restarted = new AssistantClientStateStore(environment.Directory);
        using (AssistantClientExecutionScope.Enter(restarted, "mac-tab-1"))
        {
            var current = AssistantClientExecutionScope.Resolve(settings.Current);
            Assert.Equal(macSession, current.ActiveSessionId);
            Assert.Equal(ChatMode.Coding, current.SelectedChatMode);
            Assert.Equal("mac-model", current.SelectedModel);
            Assert.False(current.IsAssistantSessionPaneOpen);
        }
        using (AssistantClientExecutionScope.Enter(restarted, "other-tab-2"))
        {
            var current = AssistantClientExecutionScope.Resolve(settings.Current);
            Assert.Equal(peerSession, current.ActiveSessionId);
            Assert.Equal(ChatMode.ClaudeScience, current.SelectedChatMode);
            Assert.Equal("peer-model", current.SelectedModel);
            Assert.True(current.IsAssistantSessionPaneOpen);
        }
        Assert.Equal(desktopSession, settings.Current.ActiveSessionId);
        Assert.Equal("desktop-model", settings.Current.SelectedModel);
        Assert.Equal(ChatMode.General, settings.Current.SelectedChatMode);
    }

    [Fact]
    public async Task DraftsRemainSeparatePerClientAndConversationIncludingExplicitEmptyDrafts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        var defaults = new AppSettings();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (AssistantClientExecutionScope.Enter(views, "mac"))
        {
            Assert.Empty(AssistantClientExecutionScope.Draft(first, "legacy desktop text", defaults));
            await AssistantClientExecutionScope.SaveDraftAsync(first, "Mac first chat draft", defaults, CancellationToken.None);
            await AssistantClientExecutionScope.SaveDraftAsync(second, "Mac second chat draft", defaults, CancellationToken.None);
        }
        using (AssistantClientExecutionScope.Enter(views, "peer"))
            await AssistantClientExecutionScope.SaveDraftAsync(first, "Peer draft", defaults, CancellationToken.None);
        using (AssistantClientExecutionScope.Enter(views, "desktop"))
        {
            Assert.Equal("legacy desktop text", AssistantClientExecutionScope.Draft(first, "legacy desktop text", defaults));
            await AssistantClientExecutionScope.SaveDraftAsync(first, string.Empty, defaults, CancellationToken.None);
        }

        using var restarted = new AssistantClientStateStore(environment.Directory);
        using (AssistantClientExecutionScope.Enter(restarted, "mac"))
        {
            Assert.Equal("Mac first chat draft", AssistantClientExecutionScope.Draft(first, "legacy", defaults));
            Assert.Equal("Mac second chat draft", AssistantClientExecutionScope.Draft(second, "legacy", defaults));
        }
        using (AssistantClientExecutionScope.Enter(restarted, "peer"))
            Assert.Equal("Peer draft", AssistantClientExecutionScope.Draft(first, "legacy", defaults));
        using (AssistantClientExecutionScope.Enter(restarted, "desktop"))
            Assert.Empty(AssistantClientExecutionScope.Draft(first, "old legacy text must not reappear", defaults));
        Assert.Equal("legacy", AssistantClientExecutionScope.Draft(first, "legacy", defaults));
    }

    [Fact]
    public async Task BrowserKeepsItsViewWhileReceivingSavedGlobalPreferences()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        using var browser = AssistantClientExecutionScope.Enter(views, "mac");
        await AssistantClientExecutionScope.UpdateAsync(settings, current => current with { SelectedModel = "mac-model" }, CancellationToken.None);
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = "http://127.0.0.1:65001", Theme = AppTheme.Light,
            IsAutomaticSpeechEnabled = true, SelectedModel = "new-desktop-model" });

        var current = AssistantClientExecutionScope.Resolve(settings.Current);
        Assert.Equal("mac-model", current.SelectedModel);
        Assert.Equal("http://127.0.0.1:65001", current.MissumAiServerUrl);
        Assert.Equal(AppTheme.Light, current.Theme);
        Assert.True(current.IsAutomaticSpeechEnabled);
        Assert.Equal("new-desktop-model", settings.Current.SelectedModel);
    }

    [Fact]
    public async Task NativeAndLegacyCallsContinueUpdatingTheExistingSettingsStore()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        var session = Guid.NewGuid();
        using (AssistantClientExecutionScope.Enter(views, "desktop"))
        {
            Assert.False(AssistantClientExecutionScope.IsBrowser);
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with
            { ActiveSessionId = session, SelectedModel = "native-model" }, CancellationToken.None);
        }
        Assert.Equal("desktop", AssistantClientExecutionScope.ClientId);
        Assert.False(AssistantClientExecutionScope.IsFrozen);
        await AssistantClientExecutionScope.UpdateAsync(settings, current => current with { IsNavigationPaneOpen = false }, CancellationToken.None);
        using var reopenedStore = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        using var reopened = new SettingsCoordinator(reopenedStore);
        await reopened.InitializeAsync();
        Assert.Equal(session, reopened.Current.ActiveSessionId);
        Assert.Equal("native-model", reopened.Current.SelectedModel);
        Assert.False(reopened.Current.IsNavigationPaneOpen);
        Assert.Same(reopened.Current, AssistantClientExecutionScope.Resolve(reopened.Current));
        Assert.Equal("legacy draft", AssistantClientExecutionScope.Draft(session, "legacy draft", reopened.Current));
    }

    [Theory]
    [InlineData("mac")]
    [InlineData("desktop")]
    public async Task QueuedExecutionRetainsSubmittedGatewayModelAndGenerationPreferences(string clientId)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            MissumAiServerUrl = "http://127.0.0.1:65001", SelectedModel = "submitted-model",
            CodingToolStepsExpanded = true, IsAutomaticSpeechEnabled = true,
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:submitted-model"] = "high" },
        });
        using var views = new AssistantClientStateStore(environment.Directory);
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            static () => new RecordingGatewayProbe());
        await using var scheduler = new ProfileAssistantRunScheduler(NullLogger<ProfileAssistantRunScheduler>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.Enqueue(Guid.NewGuid(), "occupy-model", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        Func<IDisposable> execution;
        using (AssistantClientExecutionScope.Enter(views, clientId)) execution = AssistantClientExecutionScope.Capture(settings.Current);
        var queued = scheduler.Enqueue(Guid.NewGuid(), "queued-client", async token =>
        {
            using var scope = execution();
            Assert.True(AssistantClientExecutionScope.IsFrozen);
            Assert.Equal(clientId, AssistantClientExecutionScope.ClientId);
            var snapshot = AssistantClientExecutionScope.Resolve(settings.Current);
            using var gateway = await connection.CreateClientAsync(token);
            var health = await gateway.GetLiveHealthAsync(token);
            return (snapshot, gatewayAddress: health.Reason);
        }, timeout.Token);

        using (AssistantClientExecutionScope.Enter(views, clientId))
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with
            {
                SelectedModel = "edited-model", CodingToolStepsExpanded = false,
                ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:submitted-model"] = "low" },
            }, timeout.Token);
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = "http://127.0.0.1:65002", IsAutomaticSpeechEnabled = false });
        Assert.Equal(1, scheduler.Snapshot.QueueDepth);
        release.SetResult();
        Assert.True(await active.Completion.WaitAsync(timeout.Token));
        var result = await queued.Completion.WaitAsync(timeout.Token);

        Assert.Equal("submitted-model", result.snapshot.SelectedModel);
        Assert.Equal("high", MissumAiAssistantService.StoredReasoning(result.snapshot, "submitted-model", "general"));
        Assert.True(result.snapshot.CodingToolStepsExpanded);
        Assert.True(result.snapshot.IsAutomaticSpeechEnabled);
        Assert.Equal("http://127.0.0.1:65001", result.snapshot.MissumAiServerUrl);
        // Verify the actual HTTP client destination, not only the frozen record.
        Assert.Equal("127.0.0.1:65001", result.gatewayAddress);
        using (AssistantClientExecutionScope.Enter(views, clientId))
            Assert.Equal("edited-model", AssistantClientExecutionScope.Resolve(settings.Current).SelectedModel);
        Assert.False(AssistantClientExecutionScope.IsFrozen);
    }

    [Fact]
    public async Task FrozenRunNavigationUpdatesDoNotMoveTheUsersCurrentViewOrOverwriteDrafts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        var submittedSession = Guid.NewGuid();
        var currentlyViewedSession = Guid.NewGuid();
        Func<IDisposable> execution;
        using (AssistantClientExecutionScope.Enter(views, "mac"))
        {
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with { ActiveSessionId = submittedSession }, CancellationToken.None);
            execution = AssistantClientExecutionScope.Capture(settings.Current);
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with { ActiveSessionId = currentlyViewedSession }, CancellationToken.None);
            await AssistantClientExecutionScope.SaveDraftAsync(submittedSession, "new follow-up draft", settings.Current, CancellationToken.None);
        }
        using (execution())
        {
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with { ActiveSessionId = submittedSession }, CancellationToken.None);
            await AssistantClientExecutionScope.SaveDraftAsync(submittedSession, string.Empty, settings.Current, CancellationToken.None);
            Assert.Equal(submittedSession, AssistantClientExecutionScope.Resolve(settings.Current).ActiveSessionId);
        }
        using (AssistantClientExecutionScope.Enter(views, "mac"))
        {
            Assert.Equal(currentlyViewedSession, AssistantClientExecutionScope.Resolve(settings.Current).ActiveSessionId);
            Assert.Equal("new follow-up draft", AssistantClientExecutionScope.Draft(submittedSession, "legacy", settings.Current));
        }
    }

    [Fact]
    public async Task ConcurrentAsyncClientOperationsAndNestedScopesDoNotLeakClientIdentity()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        var session = Guid.NewGuid();
        async Task<string> EditAsync(string id)
        {
            using var scope = AssistantClientExecutionScope.Enter(views, id);
            await Task.Yield();
            await AssistantClientExecutionScope.UpdateAsync(settings, current => current with { SelectedModel = id + "-model" }, CancellationToken.None);
            await AssistantClientExecutionScope.SaveDraftAsync(session, id + "-draft", settings.Current, CancellationToken.None);
            using (AssistantClientExecutionScope.Enter(views, "temporary"))
            {
                await Task.Yield();
                Assert.Equal("temporary", AssistantClientExecutionScope.ClientId);
            }
            Assert.Equal(id, AssistantClientExecutionScope.ClientId);
            Assert.Equal(id + "-draft", AssistantClientExecutionScope.Draft(session, "legacy", settings.Current));
            return AssistantClientExecutionScope.Resolve(settings.Current).SelectedModel!;
        }

        Assert.Equal(["mac-model", "peer-model"], await Task.WhenAll(EditAsync("mac"), EditAsync("peer")));
        Assert.Equal("desktop", AssistantClientExecutionScope.ClientId);
        Assert.Equal(AppSettings.DefaultSelectedModel, settings.Current.SelectedModel);
    }

    [Fact]
    public async Task OversizedDraftDoesNotReplaceAnExistingDraft()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        var defaults = new AppSettings();
        var session = Guid.NewGuid();
        using var scope = AssistantClientExecutionScope.Enter(views, "mac");
        await AssistantClientExecutionScope.SaveDraftAsync(session, "keep this draft", defaults, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AssistantClientExecutionScope.SaveDraftAsync(
            session, new string('x', 100_001), defaults, CancellationToken.None));
        Assert.Equal("keep this draft", AssistantClientExecutionScope.Draft(session, "legacy", defaults));
    }

    [Fact]
    public async Task QueuedPromptKeepsItsSubmittedToolAndToolTextAfterTriggerEditing()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Frozen tool");
        var trigger = await environment.Get<IPromptTriggerRepository>().CreateAsync(CreateTrigger(
            PromptTriggerAction.TextToSpeech, "frozen-read"));
        using var views = new AssistantClientStateStore(environment.Directory);
        await using var scheduler = new ProfileAssistantRunScheduler(NullLogger<ProfileAssistantRunScheduler>.Instance);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.Enqueue(Guid.NewGuid(), "blocker", async token =>
        { blockerStarted.SetResult(); await release.Task.WaitAsync(token); return true; }, timeout.Token);
        await blockerStarted.Task.WaitAsync(timeout.Token);
        var coordinator = CreateCoordinator(environment, settings, scheduler);
        var speech = new TaskCompletionSource<(string ClientId, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.BrowserSpeechHandler = (id, envelope, _, _) =>
        {
            speech.TrySetResult((id, envelope.Payload.GetProperty("text").GetString()!));
            return Task.FromResult(true);
        };
        using (AssistantClientExecutionScope.Enter(views, "mac"))
            await coordinator.HandleAsync(ChatEnvelope(session.Id, "frozen-read original words"),
                (_, _, _) => Task.CompletedTask, timeout.Token);
        await environment.Get<IPromptTriggerRepository>().UpdateAsync(trigger with
        { Action = PromptTriggerAction.Translation, ExtensionActionId = BuiltInActionIds.Translate, Phrase = "changed-read" }, trigger.Revision);
        release.SetResult();
        Assert.True(await blocker.Completion.WaitAsync(timeout.Token));

        var result = await speech.Task.WaitAsync(timeout.Token);
        Assert.Equal("mac", result.ClientId);
        Assert.Equal("original words", result.Text);
    }

    [Fact]
    public async Task NewlyAddedTriggerCannotConvertAnAlreadyQueuedPlainPromptIntoSpeech()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Plain submitted prompt");
        using var views = new AssistantClientStateStore(environment.Directory);
        await using var scheduler = new ProfileAssistantRunScheduler(NullLogger<ProfileAssistantRunScheduler>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.Enqueue(Guid.NewGuid(), "blocker", async token =>
        { started.SetResult(); await release.Task.WaitAsync(token); return true; }, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        var coordinator = CreateCoordinator(environment, settings, scheduler);
        var speechInvoked = false;
        coordinator.BrowserSpeechHandler = (_, _, _, _) => { speechInvoked = true; return Task.FromResult(true); };
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (AssistantClientExecutionScope.Enter(views, "mac"))
            await coordinator.HandleAsync(ChatEnvelope(session.Id, "later-only-trigger plain request"),
                (type, payload, _) =>
                {
                    if (type == "host.error") completion.TrySetResult(JsonSerializer.SerializeToElement(payload).GetProperty("message").GetString()!);
                    return Task.CompletedTask;
                }, timeout.Token);
        await environment.Get<IPromptTriggerRepository>().CreateAsync(CreateTrigger(PromptTriggerAction.TextToSpeech, "later-only-trigger"));
        release.SetResult();
        Assert.True(await blocker.Completion.WaitAsync(timeout.Token));

        // This fixture has no model executor. Reaching its guard proves that the
        // submitted plain prompt remained a model request rather than new speech.
        Assert.Contains("AI-Clientdienst", await completion.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
        Assert.False(speechInvoked);
    }

    [Fact]
    public async Task StartingAnOlderQueuedPromptDoesNotOverwriteTheUsersLaterPersistentToolChoice()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Persistent tool");
        await environment.Get<IPromptTriggerRepository>().CreateAsync(CreateTrigger(PromptTriggerAction.Audiobook, "queued-story"));
        using var views = new AssistantClientStateStore(environment.Directory);
        await using var scheduler = new ProfileAssistantRunScheduler(NullLogger<ProfileAssistantRunScheduler>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.Enqueue(Guid.NewGuid(), "blocker", async token =>
        { started.SetResult(); await release.Task.WaitAsync(token); return true; }, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        var coordinator = CreateCoordinator(environment, settings, scheduler);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (AssistantClientExecutionScope.Enter(views, "mac"))
            await coordinator.HandleAsync(ChatEnvelope(session.Id, "queued-story create a new story"),
                (type, _, _) => { if (type == "host.error") completion.TrySetResult(); return Task.CompletedTask; }, timeout.Token);
        Assert.Equal(BuiltInActionIds.CreateAudiobook, (await chats.GetSessionAsync(session.Id))?.PersistentExtensionActionId);
        await chats.SetPersistentExtensionActionIdAsync(session.Id, BuiltInActionIds.PlanMode);
        release.SetResult();
        Assert.True(await blocker.Completion.WaitAsync(timeout.Token));
        await completion.Task.WaitAsync(timeout.Token);

        Assert.Equal(BuiltInActionIds.PlanMode, (await chats.GetSessionAsync(session.Id))?.PersistentExtensionActionId);
    }

    private static AssistantCoordinator CreateCoordinator(TestEnvironment environment, SettingsCoordinator settings,
        ProfileAssistantRunScheduler scheduler)
    {
        var recentActivity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        return new(environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), null,
            settings, recentActivity, runScheduler: scheduler);
    }
    private static WebBridgeEnvelope ChatEnvelope(Guid sessionId, string prompt) =>
        new(2, "chat.send", Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(new { sessionId, prompt }));
    private static PromptTrigger CreateTrigger(PromptTriggerAction action, string phrase) =>
        new(Guid.NewGuid(), action, phrase, "Queue regression", PromptTriggerMatchMode.Prefix, true, 100, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class RecordingGatewayProbe : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = request.RequestUri!;
            var body = "{\"status\":\"live\",\"protocolVersion\":\"1.0\",\"timestamp\":\"2026-10-05T00:00:00Z\",\"reason\":\""
                + address.Authority + "\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
