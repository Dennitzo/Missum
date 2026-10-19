using System.Text.Json;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Exercises the actual footer controls and production event projections.
    // Only the OS clipboard and audio device actions are captured in this
    // isolated profile. No speech synthesis or chat/model request is started.
    private async Task VerifyStreamingMessageFooterSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke)
            throw new InvalidOperationException("Der Nachrichten-Footer-Smoke ist ausschließlich im isolierten Portable-Smoke erlaubt.");
        var previousCopy = _messageCopySmokeAdapter;
        var previousSpeech = _messageSpeechSmokeAdapter;
        var previousMicrophone = _messageSpeechMicrophoneSmokeSnapshot;
        var previousSession = _messageSpeechSessionId;
        var previousMessage = _messageSpeechMessageId;
        var previousPlayback = _messageSpeechPlaybackId;
        var previousAutomatic = _messageSpeechAutomatic;
        var previousSpeaking = _speaking;
        var previousSpeechStatus = _messageSpeechStatus;
        var previousStopRequested = _messageSpeechStopRequested;
        var previousStopped = _stoppedMessageSpeechPlaybacks.ToArray();
        var previousStoppedOrder = _stoppedMessageSpeechPlaybackOrder.ToArray();
        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var id = messageId.ToString();
        var other = otherId.ToString();
        var at = DateTimeOffset.UtcNow;
        var copies = new List<string>();
        var commands = new List<(string Action, string? Message)>();
        var currentPlayback = Guid.NewGuid();
        StackPanel? monitoredFooter = null;
        var footerLoads = 0;
        var footerUnloads = 0;
        var footerLifecycleTrace = new List<string>();
        void ObserveFooterLifecycle(string kind, object sender, RoutedEventArgs args)
        {
            var ownSender = ReferenceEquals(sender, monitoredFooter);
            var ownSource = args.OriginalSource is null || ReferenceEquals(args.OriginalSource, monitoredFooter);
            if (footerLifecycleTrace.Count < 16)
                footerLifecycleTrace.Add($"{kind}:sender={ownSender};source={args.OriginalSource?.GetType().Name ?? "null"};ownSource={ownSource}");
            if (!ownSender || !ownSource) return;
            if (kind == "loaded") footerLoads++;
            else footerUnloads++;
        }
        RoutedEventHandler onFooterLoaded = (sender, args) => ObserveFooterLifecycle("loaded", sender, args);
        RoutedEventHandler onFooterUnloaded = (sender, args) => ObserveFooterLifecycle("unloaded", sender, args);
        void Device(bool active, bool paused) => _messageSpeechMicrophoneSmokeSnapshot = new(
            false, false, active, active, paused, paused ? "Vorlesen pausiert" : active ? "Vorlesen läuft" : "Bereit",
            active ? at : null, null, "", "Supertonic-3 F5", null);
        void SpeechStatus(Guid source, Guid playback, bool active, bool automatic)
        {
            Device(active, false);
            ApplyEvent("speech.status", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, sourceMessageId = source, playbackId = playback, ownerClientId = "desktop",
                automatic, active, status = active ? "Antwort wird fortlaufend vorgelesen" : "Vorlesen beendet",
                isPromptRequest = false,
            }));
        }
        void Progress(Guid source, Guid playback, string state, bool automatic)
        {
            ApplyEvent("speech.progress", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, sourceMessageId = automatic ? (Guid?)null : source,
                controlMessageId = automatic ? source : (Guid?)null,
                controlPlaybackId = automatic ? playback : (Guid?)null,
                playbackId = automatic ? Guid.NewGuid() : playback,
                ownerClientId = "desktop", state,
            }));
        }
        try
        {
            _messageCopySmokeAdapter = copies.Add;
            _messageSpeechSmokeAdapter = (action, source) =>
            {
                commands.Add((action, source));
                if (action == "start")
                {
                    currentPlayback = Guid.NewGuid();
                    SpeechStatus(Guid.Parse(source!), currentPlayback, true, false);
                    Progress(Guid.Parse(source!), currentPlayback, "playing", false);
                }
                else if (action == "pause")
                {
                    var paused = !MessageFooterMicrophone.IsSpeechPaused;
                    Device(true, paused);
                    Progress(Guid.Parse(source!), currentPlayback, paused ? "paused" : "playing", _messageSpeechAutomatic);
                }
                else if (action == "stop") Device(false, false);
                return Task.CompletedTask;
            };
            Device(false, false);
            _speaking = false;
            var state = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            state["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
            state["chatMode"] = JsonSerializer.SerializeToElement("general");
            state["isRunning"] = JsonSerializer.SerializeToElement(true);
            state["activeRunSessionId"] = JsonSerializer.SerializeToElement(owner);
            state["activeRunId"] = JsonSerializer.SerializeToElement("run-native-footer-smoke");
            state["runMessageId"] = JsonSerializer.SerializeToElement(messageId);
            state["messages"] = JsonSerializer.SerializeToElement(new[]
            {
                new { id = otherId, sessionId = owner, role = "assistant", content = "Vorherige Antwort", status = "completed", createdAt = at.AddSeconds(-1), updatedAt = at.AddSeconds(-1) },
                new { id = messageId, sessionId = owner, role = "assistant", content = "", status = "streaming", createdAt = at, updatedAt = at },
            });
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(state)); RenderMessagesNow(); UpdateLayout();
            var footer = _messageActionViews[id];
            var panel = footer.Panel;
            var copy = footer.Copy;
            var read = footer.Read;
            var pause = footer.Pause;
            var footerBody = MessageBody(_messageViews[id].View);
            var footerRoot = footerBody.XamlRoot;
            bool FooterHasVisualBinding() => footerRoot is not null && ReferenceEquals(panel.XamlRoot, footerRoot)
                && panel.ActualHeight >= 28 && panel.ActualWidth > 0 && ReferenceEquals(panel.Parent, footerBody);
            // An initial IsLoaded flag can toggle during attachment even while
            // the measured footer and its parent remain in the same XamlRoot.
            // Use the actual visual binding, after the initial layout/UI turn,
            // as the baseline. The first unpaired Loaded may arrive later;
            // any Unloaded or subsequent Loaded still indicates a reattach.
            LayoutRoot.UpdateLayout();
            var loadedBaseline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => loadedBaseline.TrySetResult(true)))
                throw new InvalidOperationException("Die Footer-Ladebasis konnte nicht im UI-Dispatcher vorbereitet werden.");
            await loadedBaseline.Task.WaitAsync(_lifetime.Token);
            var stableVisualPasses = 0;
            await WaitForPagingSmokeAsync(() =>
            {
                LayoutRoot.UpdateLayout();
                stableVisualPasses = FooterHasVisualBinding() ? stableVisualPasses + 1 : 0;
                return stableVisualPasses >= 3;
            }, "Der Nachrichten-Footer wurde nicht stabil im nativen Fenster angeordnet.");
            footerLoads = footerUnloads = 0;
            monitoredFooter = panel;
            panel.Loaded += onFooterLoaded;
            panel.Unloaded += onFooterUnloaded;
            void AssertStableFooter(string phase)
            {
                if (!ReferenceEquals(panel, _messageActionViews[id].Panel) || !ReferenceEquals(copy, footer.Copy)
                    || !ReferenceEquals(read, footer.Read) || !ReferenceEquals(pause, footer.Pause)
                    || !ReferenceEquals(footerBody, MessageBody(_messageViews[id].View))
                    || !ReferenceEquals(panel.Parent, footerBody) || !ReferenceEquals(footerBody.Children.LastOrDefault(), panel)
                    || !FooterHasVisualBinding() || footerLoads > 1 || footerUnloads != 0)
                    throw new InvalidOperationException("Der Nachrichten-Footer hat beim Streaming oder Recycling seine Controls, seinen Parent oder seine Ladebindung verloren: "
                        + JsonSerializer.Serialize(new
                        {
                            phase, owner, currentSession = _session, messageId = id,
                            panelRetained = ReferenceEquals(panel, _messageActionViews[id].Panel),
                            copyRetained = ReferenceEquals(copy, footer.Copy), readRetained = ReferenceEquals(read, footer.Read),
                            pauseRetained = ReferenceEquals(pause, footer.Pause),
                            bodyRetained = ReferenceEquals(footerBody, MessageBody(_messageViews[id].View)),
                            parentMatches = ReferenceEquals(panel.Parent, footerBody), parentType = panel.Parent?.GetType().Name,
                            lastChildMatches = ReferenceEquals(footerBody.Children.LastOrDefault(), panel),
                            footerIndex = footerBody.Children.IndexOf(panel), children = footerBody.Children.Count,
                            loaded = panel.IsLoaded, footerLoads, footerUnloads, footerLifecycleTrace,
                            actualHeight = panel.ActualHeight, actualWidth = panel.ActualWidth,
                            visualRootMatches = ReferenceEquals(panel.XamlRoot, footerRoot),
                            visibility = panel.Visibility.ToString(), copyEnabled = copy.IsEnabled, readEnabled = read.IsEnabled,
                            pauseEnabled = pause.IsEnabled, textLength = footer.Text.Length,
                            bodyLoaded = footerBody.IsLoaded, bubbleLoaded = _messageViews[id].View.IsLoaded,
                            virtualized = _virtualMessages.ContainsKey(id),
                        }, JsonOptions));
            }
            AssertStableFooter("initial-loaded-footer");
            if (panel.Visibility != Visibility.Visible || copy.IsEnabled || read.IsEnabled)
                throw new InvalidOperationException("Ein leerer Streaming-Footer muss sichtbar sein, ohne noch fehlenden Text vorlesen oder kopieren zu können.");
            const string firstText = "Die Antwort erscheint bereits während des AI-Laufs.";
            const string latestText = firstText + " Weitere Sätze werden fortlaufend ergänzt.";
            foreach (var text in new[] { firstText, latestText })
            {
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = text }));
                await Task.Delay(180, _lifetime.Token);
                if (!ReferenceEquals(panel, _messageActionViews[id].Panel) || !ReferenceEquals(copy, footer.Copy)
                    || !ReferenceEquals(read, footer.Read) || !ReferenceEquals(pause, footer.Pause)
                    || panel.Visibility != Visibility.Visible || !copy.IsEnabled || !read.IsEnabled
                    || footer.Text != text || panel.Parent != MessageBody(_messageViews[id].View))
                    throw new InvalidOperationException("Ein Textdelta hat den sichtbaren Nachrichten-Footer ersetzt oder seine Aktionen deaktiviert.");
                AssertStableFooter("normal-text-delta");
                ((IInvokeProvider)new ButtonAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke();
                if (copies.LastOrDefault() != text)
                    throw new InvalidOperationException("Kopieren im Streaming-Footer verwendet nicht den aktuell angezeigten Antwortstand.");
            }
            async Task Invoke(Button button, int count)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(40, _lifetime.Token);
                if (commands.Count != count || !_running)
                    throw new InvalidOperationException("Eine Footer-Audioaktion hat den AI-Lauf angehalten oder nicht die erwartete Audioaktion ausgelöst.");
            }
            await Invoke(read, 1);
            if (commands[0] != ("start", id) || AutomationProperties.GetName(read) != "Vorlesen beenden"
                || pause.Visibility != Visibility.Visible || !pause.IsEnabled)
                throw new InvalidOperationException("Manuelles Vorlesen ist während des Textstreamings nicht am Nachrichten-Footer steuerbar.");
            await Invoke(pause, 2);
            if (AutomationProperties.GetName(pause) != "Vorlesen fortsetzen")
                throw new InvalidOperationException("Der Footer bietet nach Pause keine Fortsetzen-Aktion.");
            await Invoke(pause, 3);
            if (AutomationProperties.GetName(pause) != "Vorlesen pausieren")
                throw new InvalidOperationException("Fortsetzen hat die Pause-Aktion im Footer nicht wiederhergestellt.");
            await Invoke(read, 4);
            if (_speaking || pause.Visibility != Visibility.Collapsed || !read.IsEnabled)
                throw new InvalidOperationException("Vorlesen beenden hat den Streaming-Footer nicht unabhängig von der AI-Generierung zurückgesetzt.");

            currentPlayback = Guid.NewGuid();
            SpeechStatus(messageId, currentPlayback, true, true);
            Progress(messageId, currentPlayback, "buffering", true);
            if (!IsMessageSpeechSource(footer) || _messageSpeechMessageId != messageId
                || _messageSpeechPlaybackId != currentPlayback || AutomationProperties.GetName(read) != "Automatisches Vorlesen beenden"
                || pause.Visibility != Visibility.Visible || !pause.IsEnabled
                || _messageActionViews[other].Pause.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Automatisches Vorlesen ohne Chunk-Highlightquelle wurde nicht dem richtigen Nachrichten-Footer zugeordnet.");
            await Invoke(pause, 5);
            if (AutomationProperties.GetName(pause) != "Vorlesen fortsetzen")
                throw new InvalidOperationException("Die automatische Vorlesequeue lässt sich zwischen Textabschnitten nicht pausieren.");
            await Invoke(pause, 6);
            await SaveMathPreviewAsync(MessageBody(_messageViews[id].View), "native-message-footer-streaming-preview.png");
            var stoppedAutomatic = currentPlayback;
            await Invoke(read, 7);
            SpeechStatus(messageId, stoppedAutomatic, true, true);
            Progress(messageId, stoppedAutomatic, "playing", true);
            if (_speaking || pause.Visibility != Visibility.Collapsed || _messageSpeechPlaybackId != stoppedAutomatic)
                throw new InvalidOperationException("Ein verspätetes automatisches Audioereignis hat einen im Footer gestoppten Playback erneut aktiviert.");
            Device(false, false);
            ApplyEvent("speech.status", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, sourceMessageId = otherId, playbackId = Guid.NewGuid(), ownerClientId = "mac-browser",
                automatic = true, active = true, status = "Browseraudio",
            }));
            ApplyEvent("speech.progress", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, controlMessageId = otherId, controlPlaybackId = Guid.NewGuid(), playbackId = Guid.NewGuid(),
                ownerClientId = "mac-browser", state = "playing",
            }));
            if (_speaking || _messageSpeechMessageId != messageId)
                throw new InvalidOperationException("Audio auf einem anderen Client hat den nativen Footer übernommen.");
            currentPlayback = Guid.NewGuid();
            SpeechStatus(otherId, currentPlayback, true, true);
            Progress(otherId, currentPlayback, "playing", true);
            var otherFooter = _messageActionViews[other];
            if (pause.Visibility != Visibility.Collapsed || otherFooter.Pause.Visibility != Visibility.Visible
                || AutomationProperties.GetName(otherFooter.Read) != "Automatisches Vorlesen beenden")
                throw new InvalidOperationException("Eine andere Vorlesequellnachricht hat die Steuerelemente im falschen Footer aktiviert.");
            await Invoke(otherFooter.Read, 8);
            if (commands[^1] != ("stop", other) || !_running || S(DisplayMessages[id], "status") != "streaming")
                throw new InvalidOperationException("Automatisches Vorlesen stoppen hat die aktive AI-Antwort verändert.");

            // Insert a new content block before the same footer, then cross the
            // real virtualization threshold and recycle both ends of the content.
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId,
                toolStep = new { id = "footer-read", tool = "coding.read", status = "completed", contentOffset = latestText.Length,
                    outputJson = "{\"path\":\"example.py\",\"content\":\"x = 2\"}" } }));
            RenderMessagesNow(); UpdateLayout(); AssertStableFooter("normal-tool-inserted");
            var largeText = latestText + "\n\n" + @"Die Gleichung lautet \(F = m \cdot a\)." + "\n\n"
                + string.Join("\n\n", Enumerable.Range(0, 240).Select(index =>
                    $"Abschnitt {index}: Dieser Absatz prüft den stabilen Nachrichtenfooter außerhalb der recycelten Inhaltsblöcke."));
            _scrollNewSessionToLatest = true;
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId,
                contentReplace = largeText, updatedAt = DateTimeOffset.UtcNow }));
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => _virtualMessages.TryGetValue(id, out var virtualized)
                && !virtualized.Preparing && virtualized.Version == MessageVersion(DisplayMessages[id]) && virtualized.Panel.IsLoaded,
                "Die Streaming-Nachricht wurde nicht als virtuelles Inhaltsfenster geladen.");
            OnScrollToBottom(this, new RoutedEventArgs()); RefreshVirtualConversationViewport();
            await WaitForPagingSmokeAsync(() => _messageBlocks[id].ContainsKey("thinkingIndicator"),
                "Das virtuelle Nachrichtenende wurde im Footer-Smoke nicht realisiert.");
            AssertStableFooter("virtualization-transition");
            _scrollNewSessionToLatest = false;
            ConversationScroll.ChangeView(null, 0, null, true); RefreshVirtualConversationViewport();
            await WaitForPagingSmokeAsync(() => _messageBlocks[id].ContainsKey("header")
                && !_messageBlocks[id].ContainsKey("thinkingIndicator"),
                "Das virtuelle Nachrichtenende wurde beim Hochscrollen nicht recycelt.");
            AssertStableFooter("virtual-content-head");
            _scrollNewSessionToLatest = true;
            OnScrollToBottom(this, new RoutedEventArgs()); RefreshVirtualConversationViewport();
            await WaitForPagingSmokeAsync(() => _messageBlocks[id].ContainsKey("thinkingIndicator"),
                "Das virtuelle Nachrichtenende wurde nach dem Recycling nicht wieder geladen.");
            var streamedTail = " Ein weiterer Satz erscheint nach dem Recycling.";
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId,
                contentAppend = streamedTail, updatedAt = DateTimeOffset.UtcNow }));
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => !_virtualMessages[id].Preparing
                && _virtualMessages[id].Version == MessageVersion(DisplayMessages[id]),
                "Der nächste Streaming-Abschnitt wurde nach dem Recycling nicht übernommen.");
            AssertStableFooter("virtual-content-tail-streaming");
            var virtualState = _virtualMessages[id];
            if (virtualState.Blocks.Any(block => block.Kind == "footer") || virtualState.Panel.RealizedCount > 256
                || footerBody.Children.Count != 2 || !ReferenceEquals(footerBody.Children[0], virtualState.Panel)
                || footer.Text != largeText + streamedTail)
                throw new InvalidOperationException("Der Footer gehört weiterhin zum Recyclingfenster oder sein Textstand ist veraltet.");
            ((IInvokeProvider)new ButtonAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke();
            if (copies.LastOrDefault() != largeText + streamedTail)
                throw new InvalidOperationException("Der stabile virtuelle Nachrichten-Footer kopiert den Text einschließlich LaTeX nicht vollständig.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-message-footer-streaming-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, processId = Environment.ProcessId, renderer = "WinUI3", streamingFooterVisible = true,
                    buttonsRetainedAcrossDeltas = true, copyUsesLatestVisibleText = true, copiedVersions = copies.Count,
                    manualReadDuringStreaming = true, pauseResumeStop = true, automaticPlaybackControlled = true,
                    queueControlIdsWithoutHighlights = true, stoppedPlaybackEventsIgnored = true,
                    otherClientIgnored = true, controlsBelongToCorrectMessage = true, aiRunRemainsActive = true,
                    stableParentAcrossVirtualization = true, footerOutsideRecyclingWindow = true, footerLoads, footerUnloads,
                    delayedInitialLoadedAllowed = true, footerLifecycleTrace,
                    footerSurvivesContentRecycling = true, virtualCopyPreservesLatex = true,
                    realizedContentBlocks = virtualState.Panel.RealizedCount,
                    clipboardModified = false, audioDeviceStarted = false, aiRequests = 0,
                }), _lifetime.Token);
        }
        finally
        {
            if (monitoredFooter is not null)
            {
                monitoredFooter.Loaded -= onFooterLoaded;
                monitoredFooter.Unloaded -= onFooterUnloaded;
            }
            _messageCopySmokeAdapter = previousCopy; _messageSpeechSmokeAdapter = previousSpeech;
            _messageSpeechMicrophoneSmokeSnapshot = previousMicrophone;
            _messageSpeechSessionId = previousSession; _messageSpeechMessageId = previousMessage;
            _messageSpeechPlaybackId = previousPlayback; _messageSpeechAutomatic = previousAutomatic;
            _speaking = previousSpeaking; _messageSpeechStatus = previousSpeechStatus; _messageSpeechStopRequested = previousStopRequested;
            _stoppedMessageSpeechPlaybacks.Clear();
            foreach (var playback in previousStopped) _stoppedMessageSpeechPlaybacks.Add(playback);
            _stoppedMessageSpeechPlaybackOrder.Clear();
            foreach (var playback in previousStoppedOrder) _stoppedMessageSpeechPlaybackOrder.Enqueue(playback);
            ApplyEvent("state.snapshot", original); RenderMessagesNow(); RefreshAllMessageActions();
        }
    }
}
