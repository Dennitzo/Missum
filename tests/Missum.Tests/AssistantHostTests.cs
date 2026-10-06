using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Chat;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

[Collection("Assistant LAN host")]
public sealed class AssistantHostTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BrowserReasoningChoicePersistsOnPcRefreshesMatchingNativeAndPreservesFrozenRuns()
    {
        await using var fixture = await Fixture.CreateAsync(reasoning: true);
        await fixture.Settings.UpdateAsync(value => value with { SelectedModel = "Model-A", SelectedChatMode = ChatMode.Coding,
            ReasoningEffortsByModel = new() { ["text:model-a"] = "high" } });
        using (AssistantClientExecutionScope.Enter(fixture.Views, "mac"))
            await AssistantClientExecutionScope.UpdateAsync(fixture.Settings, value => value with { SelectedChatMode = ChatMode.General }, CancellationToken.None);
        using (AssistantClientExecutionScope.Enter(fixture.Views, "different-model"))
            await AssistantClientExecutionScope.UpdateAsync(fixture.Settings, value => value with { SelectedModel = "Model-B" }, CancellationToken.None);
        Func<IDisposable> frozen;
        using (AssistantClientExecutionScope.Enter(fixture.Views, "mac")) frozen = AssistantClientExecutionScope.Capture(fixture.Settings.Current);
        var owner = new Events(); var desktop = new Events(); var different = new Events();
        using var nativeSubscription = fixture.Host.Subscribe("desktop", desktop.Emit);
        using var peerSubscription = fixture.Host.Subscribe("different-model", different.Emit);

        await fixture.Host.HandleAsync("mac", Envelope("reasoning.set", new { modelId = "Model-A", role = "general", effort = "low" }), owner.Emit);

        Assert.DoesNotContain(owner.Items, item => item.Type == "host.error");
        Assert.Equal("low", Assert.Single(owner.Items, item => item.Type == "reasoning.snapshot").Payload.GetProperty("selected").GetString());
        var native = Assert.Single(desktop.Items, item => item.Type == "reasoning.snapshot").Payload;
        Assert.Equal("Model-A", native.GetProperty("modelId").GetString());
        Assert.Equal("coding", native.GetProperty("role").GetString());
        Assert.Equal("low", native.GetProperty("selected").GetString());
        Assert.Empty(different.Items);
        Assert.Equal("low", MissumAiAssistantService.StoredReasoning(fixture.Settings.Current, "Model-A", "coding"));
        Assert.Equal("Model-A", fixture.Settings.Current.SelectedModel);
        using (frozen()) Assert.Equal("high", MissumAiAssistantService.StoredReasoning(AssistantClientExecutionScope.Resolve(fixture.Settings.Current), "Model-A", "general"));
        using var reopenedStore = new Missum.Infrastructure.Settings.JsonSettingsStore(new Missum.Infrastructure.MissumInfrastructureOptions { DataDirectory = fixture.Environment.Directory });
        using var reopened = new SettingsCoordinator(reopenedStore); await reopened.InitializeAsync();
        Assert.Equal("low", MissumAiAssistantService.StoredReasoning(reopened.Current, "Model-A", "general"));
        Assert.All(fixture.Gateway.Requests, request => Assert.Equal((HttpMethod.Get, "/v1/models/status"), request));
    }

    [Theory]
    [InlineData("Model-B", "high")]
    [InlineData("Model-A", "unsupported")]
    public async Task InvalidOrStaleBrowserReasoningChoiceDoesNotPersistOrNotifyPeers(string modelId, string effort)
    {
        await using var fixture = await Fixture.CreateAsync(reasoning: true);
        await fixture.Settings.UpdateAsync(value => value with { SelectedModel = "Model-A", ReasoningEffortsByModel = new() { ["text:model-a"] = "high" } });
        var owner = new Events(); var desktop = new Events(); using var subscription = fixture.Host.Subscribe("desktop", desktop.Emit);
        await fixture.Host.HandleAsync("mac", Envelope("reasoning.set", new { modelId, role = "general", effort }), owner.Emit);
        Assert.Contains(owner.Items, item => item.Type == "host.error");
        Assert.Empty(desktop.Items);
        Assert.Equal("high", MissumAiAssistantService.StoredReasoning(fixture.Settings.Current, "Model-A", "general"));
    }

    [Fact]
    public async Task ScienceProjectListWithNullDetailDoesNotProduceAHostError()
    {
        await using var fixture = await Fixture.CreateAsync(scientificResearch: true);
        var session = await fixture.Environment.Get<IChatRepository>().CreateSessionAsync("Neue Forschungssitzung");
        var owner = new Events();
        var peer = new Events();
        using var otherClient = fixture.Host.Subscribe("other-mac", peer.Emit);
        await fixture.Host.HandleAsync("mac", Envelope("research.list", new { sessionId = session.Id }), owner.Emit);
        Assert.DoesNotContain(owner.Items, item => item.Type == "host.error");
        var snapshot = Assert.Single(owner.Items, item => item.Type == "research.snapshot").Payload;
        Assert.True(snapshot.GetProperty("available").GetBoolean());
        Assert.Equal(session.Id, snapshot.GetProperty("sessionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("detail").ValueKind);
        Assert.Empty(snapshot.GetProperty("projects").EnumerateArray());
        Assert.Empty(peer.Items);
    }

    [Fact]
    public async Task BrowserFolderSelectionListsServerDirectoriesAndKeepsNavigationPrivate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var selected = Directory.CreateDirectory(Path.Combine(fixture.Environment.Directory, "Projekt mit Umlauten ä")).FullName;
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("workspace.browse"));
        Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed("workspace.list"));
        var owner = new Events();
        var peer = new Events();
        using var otherClient = fixture.Host.Subscribe("other-mac", peer.Emit);
        await fixture.Host.HandleAsync("mac", Envelope("workspace.browse", new { path = fixture.Environment.Directory }), owner.Emit);
        var listing = Assert.Single(owner.Items, item => item.Type == "workspace.list").Payload;
        Assert.Equal(fixture.Environment.Directory, listing.GetProperty("path").GetString());
        Assert.Contains(listing.GetProperty("directories").EnumerateArray(), item => item.GetProperty("path").GetString() == selected);
        Assert.Empty(peer.Items);
        await fixture.Host.HandleAsync("mac", Envelope("workspace.browse", new { path = "relative-path" }), owner.Emit);
        Assert.Contains(owner.Items, item => item.Type == "host.error" && item.Payload.GetProperty("type").GetString() == "workspace.browse");
        Assert.Empty(peer.Items);
    }

    [Fact]
    public async Task SessionCommandsAreDeduplicatedAcrossReconnectAndHostRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = new Events();
        var create = Envelope("session.create", new { chatMode = "general" }, "stable-request");
        await fixture.Host.HandleAsync("mac", create, first.Emit);
        Assert.DoesNotContain(first.Items, item => item.Type == "host.error");
        var initial = await fixture.Environment.Get<IChatRepository>().ListSessionsAsync();
        Assert.Single(initial);
        await fixture.Host.HandleAsync("mac", create, first.Emit);
        Assert.Single(await fixture.Environment.Get<IChatRepository>().ListSessionsAsync());
        Assert.Contains(first.Items, item => item.Type == "action.completed"
            && item.Payload.GetProperty("duplicate").GetBoolean()
            && item.Payload.GetProperty("receiptStatus").GetString() == "complete");

        await fixture.Host.DisposeAsync();
        await using var restarted = new AssistantHost(fixture.Services, fixture.Settings,
            fixture.Coordinator, new AssistantClientStateStore(fixture.Environment.Directory));
        var afterRestart = new Events();
        await restarted.HandleAsync("mac", create, afterRestart.Emit);
        Assert.Single(await fixture.Environment.Get<IChatRepository>().ListSessionsAsync());
        Assert.Contains(afterRestart.Items, item => item.Type == "state.snapshot"
            && item.Payload.GetProperty("activeSessionId").GetGuid() == initial[0].Id);
        await restarted.HandleAsync("other-mac", create, afterRestart.Emit);
        Assert.Equal(2, (await fixture.Environment.Get<IChatRepository>().ListSessionsAsync()).Count);
    }

    [Fact]
    public async Task NavigationDraftsAndPrivateRepliesRemainIndependentWhileSidebarsShareDurableChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var chats = fixture.Environment.Get<IChatRepository>();
        var left = await chats.CreateSessionAsync("Linker Chat");
        var right = await chats.CreateSessionAsync("Rechter Chat");
        await fixture.Settings.UpdateAsync(settings => settings with { ActiveSessionId = left.Id, ActiveGeneralSessionId = left.Id });
        var desktop = new Events();
        var mac = new Events();
        using var desktopSubscription = fixture.Host.Subscribe("desktop", desktop.Emit);
        using var macSubscription = fixture.Host.Subscribe("mac", mac.Emit);
        await fixture.Host.HandleAsync("desktop", Envelope("app.ready", new { }));
        await fixture.Host.HandleAsync("mac", Envelope("session.open", new { sessionId = right.Id }));
        await fixture.Host.SaveDraftAsync("desktop", left.Id, "Entwurf auf Windows");
        await fixture.Host.SaveDraftAsync("mac", right.Id, "Entwurf auf Mac");
        await fixture.Host.HandleAsync("desktop", Envelope("app.ready", new { }));
        await fixture.Host.HandleAsync("mac", Envelope("app.ready", new { }));
        var desktopState = desktop.Items.Last(item => item.Type == "state.snapshot").Payload;
        var macState = mac.Items.Last(item => item.Type == "state.snapshot").Payload;
        Assert.Equal(left.Id, desktopState.GetProperty("activeSessionId").GetGuid());
        Assert.Equal("Entwurf auf Windows", desktopState.GetProperty("draft").GetString());
        Assert.Equal(right.Id, macState.GetProperty("activeSessionId").GetGuid());
        Assert.Equal("Entwurf auf Mac", macState.GetProperty("draft").GetString());
        Assert.Equal(left.Id, fixture.Settings.Current.ActiveSessionId);
        Assert.DoesNotContain(desktop.Items, item => item.Type == "session.changed"
            && item.Payload.GetProperty("activeSessionId").GetGuid() == right.Id);

        await fixture.Host.HandleAsync("mac", Envelope("session.rename", new { sessionId = right.Id, title = "Gemeinsamer Titel" }));
        Assert.Contains(desktop.Items, item => item.Type == "session.grouped"
            && item.Payload.GetProperty("sessions").EnumerateArray().Any(session =>
                session.GetProperty("id").GetGuid() == right.Id && session.GetProperty("title").GetString() == "Gemeinsamer Titel"));
        Assert.Equal(left.Id, fixture.Settings.Current.ActiveSessionId);
    }

    [Fact]
    public async Task DisposingOldSubscriptionDoesNotDisconnectItsReplacement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var oldEvents = new Events();
        var replacementEvents = new Events();
        using var old = fixture.Host.Subscribe("mac", oldEvents.Emit);
        using var replacement = fixture.Host.Subscribe("mac", replacementEvents.Emit);
        old.Dispose();
        await fixture.Host.HandleAsync("mac", Envelope("app.ready", new { }));
        Assert.Contains(replacementEvents.Items, item => item.Type == "state.snapshot");
        Assert.Empty(oldEvents.Items);
    }

    [Theory]
    [InlineData("session.delete")]
    [InlineData("session.clear")]
    public async Task RemovingSessionsRepairsAffectedPeerViewsWithoutMovingOtherChats(string command)
    {
        await using var fixture = await Fixture.CreateAsync();
        var chats = fixture.Environment.Get<IChatRepository>();
        var removed = await chats.CreateSessionAsync("Gelöschter Chat");
        await chats.CreateSessionAsync("Verbleibender Chat");
        var coding = await chats.CreateSessionAsync("Anderer Modus", ChatMode.Coding);
        var owner = new Events();
        var peer = new Events();
        var other = new Events();
        using var ownerSubscription = fixture.Host.Subscribe("desktop", owner.Emit);
        using var peerSubscription = fixture.Host.Subscribe("mac", peer.Emit);
        using var otherSubscription = fixture.Host.Subscribe("other-mac", other.Emit);
        await fixture.Host.HandleAsync("desktop", Envelope("session.open", new { sessionId = removed.Id }));
        await fixture.Host.HandleAsync("mac", Envelope("session.open", new { sessionId = removed.Id }));
        await fixture.Host.HandleAsync("other-mac", Envelope("session.open", new { sessionId = coding.Id }));
        await fixture.Host.SaveDraftAsync("mac", removed.Id, "Entwurf des gelöschten Chats");
        var previousPeerEvents = peer.Items.Count;
        var previousOtherEvents = other.Items.Count;

        await fixture.Host.HandleAsync("desktop", Envelope(command, new { sessionId = removed.Id, chatMode = "general" }));

        Assert.DoesNotContain(owner.Items, item => item.Type == "host.error");
        var repaired = Assert.Single(peer.Items.Skip(previousPeerEvents), item => item.Type == "session.changed").Payload;
        var replacement = repaired.GetProperty("activeSessionId").GetGuid();
        Assert.NotEqual(removed.Id, replacement);
        Assert.NotNull(await chats.GetSessionAsync(replacement));
        Assert.Equal("general", repaired.GetProperty("chatMode").GetString());
        Assert.Equal(string.Empty, repaired.GetProperty("draft").GetString());
        Assert.DoesNotContain(other.Items.Skip(previousOtherEvents), item => item.Type == "session.changed");
        Assert.Equal(coding.Id, (await chats.GetSessionAsync(coding.Id))!.Id);
        await fixture.Host.HandleAsync("mac", Envelope("app.ready", new { }));
        Assert.Equal(replacement, peer.Items.Last(item => item.Type == "state.snapshot").Payload.GetProperty("activeSessionId").GetGuid());
    }

    [Theory]
    [InlineData("document.remove", "documents", "documentId")]
    [InlineData("attachment.remove", "attachments", "attachmentId")]
    public async Task RemovingSharedContextRefreshesPeersWithTheirOwnDrafts(string command, string collection, string idProperty)
    {
        await using var fixture = await Fixture.CreateAsync();
        var chats = fixture.Environment.Get<IChatRepository>();
        var shared = await chats.CreateSessionAsync("Geteilter Chat");
        var separate = await chats.CreateSessionAsync("Unabhängiger Chat");
        Guid contextId;
        await using (var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Lokaler Testinhalt.")))
        {
            contextId = command == "document.remove"
                ? (await fixture.Environment.Get<IDocumentIngestor>().ImportAsync(shared.Id, "kontext.txt", content)).Document!.Id
                : (await fixture.Environment.Get<IAssistantAttachmentRepository>().ImportAsync(shared.Id, "anhang.bin", "application/octet-stream", content)).Id;
        }
        var owner = new Events();
        var peer = new Events();
        var other = new Events();
        using var ownerSubscription = fixture.Host.Subscribe("desktop", owner.Emit);
        using var peerSubscription = fixture.Host.Subscribe("mac", peer.Emit);
        using var otherSubscription = fixture.Host.Subscribe("other-mac", other.Emit);
        await fixture.Host.HandleAsync("desktop", Envelope("session.open", new { sessionId = shared.Id }));
        await fixture.Host.HandleAsync("mac", Envelope("session.open", new { sessionId = shared.Id }));
        await fixture.Host.HandleAsync("other-mac", Envelope("session.open", new { sessionId = separate.Id }));
        await fixture.Host.SaveDraftAsync("mac", shared.Id, "Mein Mac-Entwurf");
        var previousPeerEvents = peer.Items.Count;
        var previousOtherEvents = other.Items.Count;
        Assert.Single(peer.Items.Last(item => item.Type == "session.changed").Payload.GetProperty(collection).EnumerateArray());

        await fixture.Host.HandleAsync("desktop", Envelope(command, new Dictionary<string, Guid> { [idProperty] = contextId }));

        Assert.DoesNotContain(owner.Items, item => item.Type == "host.error");
        var updated = Assert.Single(peer.Items.Skip(previousPeerEvents), item => item.Type == "document.changed").Payload;
        Assert.Empty(updated.GetProperty(collection).EnumerateArray());
        Assert.Equal(shared.Id, updated.GetProperty("activeSessionId").GetGuid());
        Assert.Equal("Mein Mac-Entwurf", updated.GetProperty("draft").GetString());
        Assert.DoesNotContain(other.Items.Skip(previousOtherEvents), item => item.Type == "document.changed");
    }

    [Fact]
    public async Task BrowserReconnectCancelsUnacknowledgedAudioInsteadOfResumingOldPlayback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var events = new Events();
        await fixture.Host.HandleAsync("mac", Envelope("microphone.speak", new
            { text = "Erster Satz. Zweiter Satz. Dritter Satz." }), events.Emit);
        await events.Next("speech.audio");
        await events.Next("speech.audio");
        await fixture.Host.HandleAsync("mac", Envelope("app.ready", new { }), events.Emit);
        Assert.Contains(events.Items, item => item.Type == "speech.reset"
            && item.Payload.GetProperty("playbackId").ValueKind == JsonValueKind.Null);
        await fixture.Gateway.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReplayedReadAloudCommandDoesNotRestartAlreadyRequestedPlayback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var events = new Events();
        var request = Envelope("microphone.speak", new { text = "Ein Satz." }, "one-read-aloud-request");
        await fixture.Host.HandleAsync("mac", request, events.Emit);
        await events.Next("speech.audio");
        await fixture.Host.HandleAsync("mac", request, events.Emit);
        Assert.Single(events.Items, item => item.Type == "speech.reset"
            && item.Payload.GetProperty("playbackId").ValueKind == JsonValueKind.String);
        Assert.Contains(events.Items, item => item.Type == "action.completed"
            && item.Payload.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task FailedMutationReplayReportsFailureWithoutExecutingAgain()
    {
        await using var fixture = await Fixture.CreateAsync();
        var events = new Events();
        var request = Envelope("document.upload", new { sessionId = Guid.NewGuid(), files = Array.Empty<object>() }, "failed-upload");
        await fixture.Host.HandleAsync("mac", request, events.Emit);
        Assert.Contains(events.Items, item => item.Type == "host.error");
        var replay = new Events();
        await fixture.Host.HandleAsync("mac", request, replay.Emit);
        Assert.Equal("failed", Assert.Single(replay.Items, item => item.Type == "action.completed").Payload.GetProperty("receiptStatus").GetString());
        Assert.Contains(replay.Items, item => item.Type == "state.snapshot");
        Assert.DoesNotContain(replay.Items, item => item.Type == "host.error");
    }

    [Fact]
    public async Task HttpAudioResourcesUseRegisteredGatewayArtifactsAndSupportRangeAndCache()
    {
        var previousPort = Environment.GetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT");
        var port = FreePort();
        Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            await fixture.Host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            using var missing = await client.GetAsync("gateway-artifacts/not-registered");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(0, fixture.Gateway.Downloads);
            var owner = new Events();
            var observer = new Events();
            using var observerSubscription = fixture.Host.Subscribe("desktop", observer.Emit);
            await fixture.Host.HandleAsync("mac", Envelope("microphone.speak", new { text = "Vorlesen auf dem Mac." }), owner.Emit);
            var audio = await owner.Next("speech.audio");
            Assert.DoesNotContain(observer.Items, item => item.Type.StartsWith("speech.", StringComparison.Ordinal));
            var url = audio.Payload.GetProperty("url").GetString()!;
            // An already announced WAV belongs to the synthesizing gateway,
            // even when preferences change before the browser downloads it.
            await fixture.Settings.UpdateAsync(value => value with { MissumAiServerUrl = "http://changed-fixture/" });
            using var resource = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
            Assert.Equal("audio/wav", resource.Content.Headers.ContentType!.MediaType);
            Assert.Equal(fixture.Gateway.Audio, await resource.Content.ReadAsByteArrayAsync());
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 7);
            using var range = await client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(fixture.Gateway.Audio.Take(8), await range.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, fixture.Gateway.Downloads);
            Assert.Equal("fixture", Assert.Single(fixture.Gateway.DownloadHosts));
            Assert.False(resource.Headers.Contains("Set-Cookie"));
        }
        finally { Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", previousPort); }
    }

    [Fact]
    public async Task HttpCaptureCommandsReturnClientErrorWithoutInvokingServerCapture()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var type in new[] { "microphone.start", "microphone.audio", "screen.capture", "screenClip.start", "audioCapture.start", "liveCaption.start" })
        {
            var events = new Events();
            await fixture.Host.HandleAsync("mac", Envelope(type, new { }), events.Emit);
            Assert.Contains(events.Items, item => item.Type == "host.error"
                && item.Payload.GetProperty("message").GetString()!.Contains("HTTP", StringComparison.Ordinal));
            Assert.DoesNotContain(events.Items, item => item.Type == "capture.required");
        }
    }

    [Fact]
    public async Task BackupRestoreWaitsForScheduledWorkBlocksNewRunsAndAllowsReadAndCancel()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var chats = fixture.Environment.Get<IChatRepository>();
        var saved = await chats.CreateSessionAsync("Zustand im Backup");
        var backupPath = Path.Combine(fixture.Environment.Directory, "restore-queue.missumbackup");
        await fixture.Environment.Get<IBackupService>().CreateAsync(backupPath, timeout.Token);
        var later = await chats.CreateSessionAsync("Änderung nach dem Backup");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedInvoked = false;
        var first = fixture.Scheduler.Enqueue(saved.Id, "gated-run", async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        var queued = fixture.Scheduler.Enqueue(later.Id, "waiting-run", _ =>
        {
            queuedInvoked = true;
            return Task.FromResult(true);
        }, timeout.Token);
        var desktop = new Events();
        using var subscription = fixture.Host.Subscribe("desktop", desktop.Emit);
        var restoring = fixture.Host.RestoreBackupFileAsync(backupPath, timeout.Token);
        try
        {
            await desktop.Next("backup.restoreDeferred");
            Assert.False(restoring.IsCompleted);
            Assert.Equal(2, (await chats.ListSessionsAsync()).Count);
            var rejected = new Events();
            await fixture.Host.HandleAsync("mac", Envelope("chat.send", new { sessionId = saved.Id, text = "Neuer Auftrag" }), rejected.Emit);
            Assert.Contains(rejected.Items, item => item.Type == "host.error"
                && item.Payload.GetProperty("message").GetString()!.Contains("Wiederherstellung", StringComparison.Ordinal));
            var reads = new Events();
            await fixture.Host.HandleAsync("mac", Envelope("conversation.refresh", new { sessionId = saved.Id }), reads.Emit);
            Assert.Contains(reads.Items, item => item.Type == "conversation.snapshot");
            Assert.DoesNotContain(reads.Items, item => item.Type == "host.error");
            var cancellation = new Events();
            await fixture.Host.HandleAsync("mac", Envelope("chat.cancel", new { sessionId = later.Id }), cancellation.Emit);
            Assert.Contains(cancellation.Items, item => item.Type == "queue.changed");
            Assert.DoesNotContain(cancellation.Items, item => item.Type == "host.error");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Completion);
            Assert.False(queuedInvoked);
            var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.RestoreBackupFileAsync(backupPath, timeout.Token));
            Assert.Contains("bereits", duplicate.Message);
            release.TrySetResult();
            Assert.True(await first.Completion.WaitAsync(timeout.Token));
            await restoring.WaitAsync(timeout.Token);
            var restored = Assert.Single(await chats.ListSessionsAsync());
            Assert.Equal(saved.Id, restored.Id);
            Assert.Equal("Zustand im Backup", restored.Title);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.SaveDraftAsync("desktop", saved.Id, "Verspäteter Composer-Entwurf"));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task BackupRestoreWaitsForAlreadyAdmittedSendBeforeCheckingSchedulerIdle()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var chats = fixture.Environment.Get<IChatRepository>();
        var saved = await chats.CreateSessionAsync("Backup-Zustand");
        var backupPath = Path.Combine(fixture.Environment.Directory, "restore-admission.missumbackup");
        await fixture.Environment.Get<IBackupService>().CreateAsync(backupPath, timeout.Token);
        var later = await chats.CreateSessionAsync("Noch nicht eingeplanter Auftrag");
        fixture.Triggers.BlockingPrompt = "Zugelassener Auftrag";
        var owner = new Events();
        var desktop = new Events();
        using var subscription = fixture.Host.Subscribe("desktop", desktop.Emit);
        var request = Envelope("chat.send", new { sessionId = later.Id, prompt = fixture.Triggers.BlockingPrompt });
        var send = fixture.Host.HandleAsync("mac", request, owner.Emit, timeout.Token);
        await fixture.Triggers.Entered.Task.WaitAsync(timeout.Token);
        Assert.True(fixture.Scheduler.Snapshot.IsIdle);
        var replay = new Events();
        await fixture.Host.HandleAsync("mac", request, replay.Emit, timeout.Token);
        Assert.Equal("accepted", Assert.Single(replay.Items, item => item.Type == "action.completed").Payload.GetProperty("receiptStatus").GetString());
        Assert.DoesNotContain(replay.Items, item => item.Type == "state.snapshot");
        var restoring = fixture.Host.RestoreBackupFileAsync(backupPath, timeout.Token);
        try
        {
            await desktop.Next("backup.restoreDeferred");
            Assert.False(restoring.IsCompleted);
            Assert.NotNull(await chats.GetSessionAsync(later.Id));
            var deferredReplay = new Events();
            await fixture.Host.HandleAsync("mac", request, deferredReplay.Emit, timeout.Token);
            Assert.Contains(deferredReplay.Items, item => item.Type == "host.error");
            using (var receipts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Environment.Directory,
                "ClientState", "requests.json"), timeout.Token)))
            {
                // A rejected replay does not own the original command's receipt.
                Assert.Equal("accepted", receipts.RootElement.GetProperty("mac:" + request.RequestId).GetString());
            }
            var reads = new Events();
            await fixture.Host.HandleAsync("other-mac", Envelope("conversation.refresh", new { sessionId = saved.Id }), reads.Emit, timeout.Token);
            Assert.Contains(reads.Items, item => item.Type == "conversation.snapshot");
            var rejected = new Events();
            await fixture.Host.HandleAsync("other-mac", Envelope("chat.send", new { sessionId = saved.Id, prompt = "Zu spät" }), rejected.Emit, timeout.Token);
            Assert.Contains(rejected.Items, item => item.Type == "host.error");
            fixture.Triggers.Release.TrySetResult();
            await send.WaitAsync(timeout.Token);
            Assert.Contains(owner.Items, item => item.Type == "chat.queued");
            await restoring.WaitAsync(timeout.Token);
            Assert.Equal(saved.Id, Assert.Single(await chats.ListSessionsAsync()).Id);
        }
        finally
        {
            fixture.Triggers.Release.TrySetResult();
            await send.WaitAsync(timeout.Token);
            await restoring.WaitAsync(timeout.Token);
        }
    }

    private static WebBridgeEnvelope Envelope(string type, object payload, string? id = null) =>
        new(AssistantWebBridge.ProtocolVersion, type, id ?? Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload, Json));

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record HostEvent(string Type, JsonElement Payload);
    private sealed class Events
    {
        private readonly Channel<HostEvent> _channel = Channel.CreateUnbounded<HostEvent>();
        public ConcurrentQueue<HostEvent> Items { get; } = new();
        public Task Emit(string type, object payload, string? id)
        {
            var item = new HostEvent(type, JsonSerializer.SerializeToElement(payload, Json));
            Items.Enqueue(item);
            _channel.Writer.TryWrite(item);
            return Task.CompletedTask;
        }
        public async Task<HostEvent> Next(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await _channel.Reader.WaitToReadAsync(timeout.Token))
                while (_channel.Reader.TryRead(out var item)) if (item.Type == type) return item;
            throw new InvalidOperationException("Expected host event was not emitted.");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MissumAiConnectionService _connection;
        private readonly BrowserSpeechService _speech;
        private readonly AssistantSettingsService _preferences;
        private readonly ScientificPresentationCoordinator _science;
        private readonly AssistantArtifactPreviewService _previews;
        private readonly MissumAiAssistantService? _ai;
        private Fixture(TestEnvironment environment, SettingsCoordinator settings, bool scientificResearch, bool reasoning)
        {
            Environment = environment;
            Settings = settings;
            Gateway = new GatewayState();
            Scheduler = new(NullLogger<ProfileAssistantRunScheduler>.Instance);
            Triggers = new(environment.Get<IPromptTriggerRepository>());
            _connection = new(settings, NullLogger<MissumAiConnectionService>.Instance, () => new GatewayHandler(Gateway));
            if (reasoning) _ai = new(_connection, null!, null!, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, settings, null!, NullLogger<MissumAiAssistantService>.Instance);
            _speech = new(_connection.CreateClientAsync, (request, _) => Task.FromResult(BrowserSpeechPlan.Create(request.SessionId,
                request.MessageId, "Vorgegebener Text", request.Text ?? "Testtext.")));
            _preferences = new(settings, _connection, environment.Get<IPromptTriggerRepository>(), environment.Get<IBackupService>());
            _science = new(null!, null!);
            _previews = new(environment.Get<IChatArtifactRepository>(), environment.Get<IBinaryObjectStore>(), Path.Combine(environment.Directory, "Previews"));
            var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
            Coordinator = new(environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
                Triggers, environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
                environment.Get<IConversationSnapshotRepository>(), _ai, settings, recent, runScheduler: Scheduler,
                scientificResearch: scientificResearch ? environment.Get<IScientificResearchRepository>() : null);
            var services = new ServiceCollection().AddLogging()
                .AddSingleton(_connection).AddSingleton(_speech).AddSingleton(_preferences).AddSingleton(_science).AddSingleton(_previews)
                .AddSingleton(environment.Get<IChatRepository>()).AddSingleton(environment.Get<IChatArtifactRepository>())
                .AddSingleton(environment.Get<IBinaryObjectStore>()).AddSingleton<IAssistantRunScheduler>(Scheduler);
            if (_ai is not null) services.AddSingleton(_ai);
            Services = services.BuildServiceProvider();
            Views = new(environment.Directory);
            Host = new(Services, settings, Coordinator, Views);
        }
        public TestEnvironment Environment { get; }
        public SettingsCoordinator Settings { get; }
        public AssistantCoordinator Coordinator { get; }
        public ProfileAssistantRunScheduler Scheduler { get; }
        public BlockingPromptTriggers Triggers { get; }
        public ServiceProvider Services { get; }
        public AssistantHost Host { get; }
        public GatewayState Gateway { get; }
        public AssistantClientStateStore Views { get; }
        public static async Task<Fixture> CreateAsync(bool scientificResearch = false, bool reasoning = false)
        {
            var environment = await TestEnvironment.CreateAsync();
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            await settings.UpdateAsync(value => value with { MissumAiServerUrl = "http://fixture/", IsAutomaticSpeechEnabled = false });
            return new(environment, settings, scientificResearch, reasoning);
        }
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await _speech.DisposeAsync();
            await Scheduler.DisposeAsync();
            _preferences.Dispose();
            _science.Dispose();
            _previews.Dispose();
            _connection.Dispose();
            await Services.DisposeAsync();
            Views.Dispose();
            Settings.Dispose();
            await Environment.DisposeAsync();
        }
    }

    private sealed class BlockingPromptTriggers(IPromptTriggerRepository inner) : IPromptTriggerRepository
    {
        public string? BlockingPrompt { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<PromptTrigger>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task<PromptTrigger?> GetAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
        public Task<PromptTrigger> CreateAsync(PromptTrigger trigger, CancellationToken cancellationToken = default) => inner.CreateAsync(trigger, cancellationToken);
        public Task<PromptTrigger> UpdateAsync(PromptTrigger trigger, long expectedRevision, CancellationToken cancellationToken = default) => inner.UpdateAsync(trigger, expectedRevision, cancellationToken);
        public Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, expectedRevision, cancellationToken);
        public async Task<PromptTriggerMatch?> MatchAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (prompt == BlockingPrompt)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.MatchAsync(prompt, cancellationToken);
        }
    }

    private sealed class GatewayState
    {
        public byte[] Audio { get; } = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        public ConcurrentDictionary<string, byte[]> Artifacts { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> ArtifactHosts { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> DownloadHosts { get; } = new();
        public int Downloads;
        public ConcurrentQueue<(HttpMethod Method, string Path)> Requests { get; } = new();
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class GatewayHandler(GatewayState state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            state.Requests.Enqueue((request.Method, path));
            var now = DateTimeOffset.UtcNow;
            if (path == "/v1/models/status")
                return Response(new ModelStatusSnapshot(true, "fixture", [
                    new ModelRuntimeStatus("Model-A", "general", true, true, "loaded", 4096, ReasoningEfforts: ["low", "high"], DefaultReasoningEffort: "high"),
                    new ModelRuntimeStatus("Model-A", "coding", true, true, "loaded", 4096, ReasoningEfforts: ["low", "high"], DefaultReasoningEffort: "high"),
                ], now));
            if (path.EndsWith("/cancel", StringComparison.Ordinal)) state.Cancelled.TrySetResult();
            if (path.StartsWith("/v1/artifacts/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/v1/artifacts/".Length..]);
                Interlocked.Increment(ref state.Downloads);
                state.DownloadHosts.Enqueue(request.RequestUri.Host);
                return state.Artifacts.TryGetValue(id, out var bytes)
                    && state.ArtifactHosts.TryGetValue(id, out var origin) && origin == request.RequestUri.Host
                    ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new(HttpStatusCode.NotFound);
            }
            if (path.EndsWith("/paragraphs", StringComparison.Ordinal))
            {
                var paragraph = (await request.Content!.ReadFromJsonAsync<SpeechParagraphRequest>(MissumAiProtocol.CreateJsonOptions(), token))!;
                var id = "speech-" + Guid.NewGuid().ToString("N");
                state.Artifacts[id] = state.Audio;
                state.ArtifactHosts[id] = request.RequestUri.Host;
                var artifact = new ArtifactDescriptor(id, "speech.wav", "audio/wav", state.Audio.Length,
                    Convert.ToHexString(SHA256.HashData(state.Audio)), now, now.AddHours(1));
                return Response(new SpeechParagraphResponse(artifact, SpeechProviderIds.SupertonicF5Cuda,
                    paragraph.ParagraphIndex, 1, 44_100,
                    paragraph.Parts!.Select(part => new SpeechParagraphTiming(part.SegmentIndex, 0, 1)).ToArray()));
            }
            return Response(new SpeechSessionSnapshot("fixture-session", "active", SpeechContentProfile.Prepared,
                SpeechProviderIds.SupertonicF5Cuda, false, now, now));
        }
        private static HttpResponseMessage Response<T>(T value) => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(value, options: MissumAiProtocol.CreateJsonOptions()) };
    }
}
