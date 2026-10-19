using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // SQLite -> compact snapshot -> native dispatcher -> both message renderers.
    // This path only writes to the disposable profile used by the portable smoke.
    private async Task VerifySteeringPromptSmokeAsync(JsonElement original)
    {
        var directory = Path.GetFullPath(App.Current.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Directory.GetParent(directory);
        if (parent is null || !Guid.TryParseExact(parent.Name, "N", out _)
            || !string.Equals(parent.Parent?.FullName, Path.Combine(Path.GetTempPath(), "Missum-Smoke").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(directory), "Data", StringComparison.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Steering smoke requires an isolated disposable profile.");
        var chats = App.Current.GetService<IChatRepository>();
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        try
        {
            foreach (var large in new[] { false, true })
            {
                var session = await chats.CreateSessionAsync("Umlenkung " + large, cancellationToken: _lifetime.Token);
                var content = large ? string.Concat(Enumerable.Repeat("Ein früherer unveränderter Absatz.\n\n", 270)) : "Bisherige Antwort.\n\n";
                var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, content, MessageStatus.Streaming, cancellationToken: _lifetime.Token);
                if (!await CommandAsync("session.open", new { sessionId = session.Id }))
                    throw new InvalidOperationException("Steering fixture did not open.");
                ShowChatView();
                RenderMessagesNow();
                if (SubagentsSection.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("An empty subagent summary remained visible.");
                var inputId = Guid.NewGuid().ToString("N");
                var prompt = "Prüfe den alternativen Ansatz.\n\nErhalte alle mathematischen Ausdrücke.";
                var pending = AddPendingUserMessage(session.Id, prompt, inputId);
                // Simulate an acknowledgement racing ahead of its payload.
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
                {
                    messageId = assistant.Id, sessionId = session.Id, revision = 1,
                    toolStep = new { id = "steering-" + inputId, tool = "assistant.steering", status = "running", detailsDeferred = true, contentOffset = content.Length },
                    toolStepOrdinal = 0, toolStepCount = 1,
                }));
                RenderMessagesNow();
                if (!_pendingUserMessages.ContainsKey(pending) || !_messageViews.ContainsKey(pending))
                    throw new InvalidOperationException("A textless acknowledgement removed the pending prompt.");

                await chats.SaveToolStepDeltaAsync(assistant.Id, new("steering-" + inputId, "assistant.steering", "completed", prompt,
                    ContentOffset: content.Length, UpdatedAt: DateTimeOffset.UtcNow), _lifetime.Token);
                await chats.UpdateMessageAsync(assistant.Id, content + "Antwort auf die neue Anweisung.", MessageStatus.Streaming, cancellationToken: _lifetime.Token);
                if (!await CommandAsync("conversation.refresh", new { sessionId = session.Id }))
                    throw new InvalidOperationException("Stored steering could not be refreshed.");
                RenderMessagesNow();
                await WaitForSteeringBubbleAsync(assistant.Id.ToString(), "steering-" + inputId, prompt);
                await VerifySteeringFooterSmokeAsync(assistant.Id, "steering-" + inputId, prompt);
                if (_pendingUserMessages.ContainsKey(pending) || _messages.ContainsKey(pending))
                    throw new InvalidOperationException("Stored steering did not replace its pending prompt exactly once.");
                if (_virtualMessages.ContainsKey(assistant.Id.ToString()) != large)
                    throw new InvalidOperationException("Steering smoke did not exercise both message renderers.");
                await CommandAsync("session.open", new { sessionId = Guid.Parse(S(original, "activeSessionId")) });
                await CommandAsync("session.open", new { sessionId = session.Id });
                RenderMessagesNow();
                await WaitForSteeringBubbleAsync(assistant.Id.ToString(), "steering-" + inputId, prompt);
                await VerifySteeringFooterSmokeAsync(assistant.Id, "steering-" + inputId, prompt);
                if (large)
                {
                    _scrollNewSessionToLatest = false;
                    ConversationScroll.ChangeView(null, 0, null, true);
                    await Task.Delay(80, _lifetime.Token);
                    _virtualMessages[assistant.Id.ToString()].Panel.RefreshViewport();
                    var footerId = MissumAiAssistantService.SteeringPromptMessageId(assistant.Id, "steering-" + inputId).ToString("D");
                    if (_messageActionViews.ContainsKey(footerId))
                        throw new InvalidOperationException("An offscreen steering footer retained its recycled visual controls.");
                    await WaitForSteeringBubbleAsync(assistant.Id.ToString(), "steering-" + inputId, prompt);
                    await VerifySteeringFooterSmokeAsync(assistant.Id, "steering-" + inputId, prompt);
                }
                await SaveMathPreviewAsync(LayoutRoot, large ? "native-steering-virtual.png" : "native-steering-short.png");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "native-steering-prompt-validation.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, pendingRetainedUntilText = true, compactDatabaseReload = true,
                shortMessageBubble = true, virtualMessageBubble = true, sessionReload = true, emptySubagentsHidden = true,
                copyExactPrompt = true, readExactPrompt = true, independentSpeechIdentity = true, pauseResumeStop = true,
                recycledFooterRestored = true,
            }), _lifetime.Token);
        }
        finally
        {
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }
    }

    private async Task WaitForSteeringBubbleAsync(string messageId, string stepId, string prompt)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            RenderMessagesNow();
            OnScrollToBottom(this, new RoutedEventArgs());
            if (_messageBlocks.TryGetValue(messageId, out var blocks)
                && blocks.TryGetValue("steering:" + stepId, out var view)
                && view is Border { Child: StackPanel body } && body.Children[0] is TextBlock text && text.Text == prompt)
                return;
            await Task.Delay(50, _lifetime.Token);
        }
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-steering-failure-state.json"), JsonSerializer.Serialize(new
        {
            sessionId = _session, messageId, expectedStepId = stepId,
            messages = _messages.Values.Select(message => new { id = S(message, "id"), contentLength = MessageContent(message).Length,
                version = MessageVersion(message), steps = MessageSteps(message).Select(step => new { id = S(step, "id"), tool = S(step, "tool"), detailLength = S(step, "detail").Length }) }),
            virtualMessages = _virtualMessages.Select(pair => new { id = pair.Key, pair.Value.Version, pair.Value.Preparing,
                blocks = pair.Value.Blocks.Select(block => block.Key), pair.Value.Panel.RealizedCount }),
        }), _lifetime.Token);
        throw new InvalidOperationException("The stored steering prompt was not rendered as a user bubble.");
    }

    private async Task VerifySteeringFooterSmokeAsync(Guid ownerId, string stepId, string prompt)
    {
        var id = MissumAiAssistantService.SteeringPromptMessageId(ownerId, stepId).ToString("D");
        if (!_messageActionViews.TryGetValue(id, out var footer) || footer.OwnerMessageId != ownerId.ToString("D")
            || footer.SteeringStepId != stepId || footer.IsAssistant || footer.IsStreaming || footer.Text != prompt
            || footer.Panel.Parent is not StackPanel body || body.Children[0] is not TextBlock text || text.Text != prompt
            || !footer.Copy.IsEnabled || !footer.Read.IsEnabled || footer.Panel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("A steering prompt lacks its own enabled user-message footer.");
        RefreshAllMessageActions();
        if (!_messageActionViews.ContainsKey(id))
            throw new InvalidOperationException("The inline footer was evicted because its synthetic identity is not a database message.");
        var previousCopy = _messageCopySmokeAdapter;
        var previousSpeech = _messageSpeechSmokeAdapter;
        var previousDevice = _messageSpeechMicrophoneSmokeSnapshot;
        var previousRunning = _running;
        var previousSpeaking = _speaking;
        var previousSession = _messageSpeechSessionId;
        var previousMessage = _messageSpeechMessageId;
        var previousPlayback = _messageSpeechPlaybackId;
        var previousAutomatic = _messageSpeechAutomatic;
        var previousStop = _messageSpeechStopRequested;
        var previousStatus = _messageSpeechStatus;
        var playback = Guid.NewGuid();
        string? copied = null;
        var commands = new List<string>();
        void Device(bool active, bool paused) => _messageSpeechMicrophoneSmokeSnapshot = new(
            false, false, active, active, paused, "Isolierte Vorlesesteuerung", null, null, "", "Supertonic-3 F5", null);
        try
        {
            _running = true;
            _messageCopySmokeAdapter = value => copied = value;
            _messageSpeechSmokeAdapter = (action, source) =>
            {
                if (source != id) throw new InvalidOperationException("A steering footer addressed a different message.");
                commands.Add(action);
                var paused = action == "pause" && !MessageFooterMicrophone.IsSpeechPaused;
                Device(action != "stop", paused);
                ApplyEvent("speech.status", JsonSerializer.SerializeToElement(new
                { sessionId = _session, sourceMessageId = id, playbackId = playback, ownerClientId = "desktop", active = action != "stop", automatic = false }));
                return Task.CompletedTask;
            };
            Device(false, false);
            ((IInvokeProvider)new ButtonAutomationPeer(footer.Copy).GetPattern(PatternInterface.Invoke)).Invoke();
            if (copied != prompt) throw new InvalidOperationException("The steering footer copied assistant text instead of the prompt.");
            async Task Invoke(Button button)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(30, _lifetime.Token);
                if (!_running || !_messageActionViews.ContainsKey(id))
                    throw new InvalidOperationException("A steering speech action stopped generation or removed the footer.");
            }
            await Invoke(footer.Read);
            if (!IsMessageSpeechSource(footer) || footer.Pause.Visibility != Visibility.Visible || !footer.Pause.IsEnabled
                || AutomationProperties.GetName(footer.Read) != "Vorlesen beenden"
                || IsMessageSpeechSource(_messageActionViews[ownerId.ToString("D")]))
                throw new InvalidOperationException("Steering speech controls were assigned to the containing assistant answer.");
            await Invoke(footer.Pause);
            if (AutomationProperties.GetName(footer.Pause) != "Vorlesen fortsetzen")
                throw new InvalidOperationException("Steering speech cannot be resumed after pausing.");
            await Invoke(footer.Pause);
            await Invoke(footer.Read);
            if (_speaking || footer.Pause.Visibility != Visibility.Collapsed || string.Join(',', commands) != "start,pause,pause,stop")
                throw new InvalidOperationException("The steering footer did not complete its independent speech controls.");
        }
        finally
        {
            _messageCopySmokeAdapter = previousCopy;
            _messageSpeechSmokeAdapter = previousSpeech;
            _messageSpeechMicrophoneSmokeSnapshot = previousDevice;
            _running = previousRunning; _speaking = previousSpeaking;
            _messageSpeechSessionId = previousSession; _messageSpeechMessageId = previousMessage;
            _messageSpeechPlaybackId = previousPlayback; _messageSpeechAutomatic = previousAutomatic;
            _messageSpeechStopRequested = previousStop; _messageSpeechStatus = previousStatus;
            RefreshAllMessageActions();
        }
    }
}
