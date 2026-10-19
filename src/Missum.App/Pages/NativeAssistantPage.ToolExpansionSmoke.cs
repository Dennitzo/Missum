using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyToolExpansionDefaultsSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke) return;
        var originalDefault = _settings.Current.CodingToolStepsExpanded;
        var originalChildren = _subagents.ToArray();
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        var cases = 0;
        var phase = "fixture-start";
        var expectedOwner = Guid.Empty;
        try
        {
            // The full smoke precedes this case with real pointer selections.
            // Begin a separate checkbox interaction through normal navigation;
            // an intentional text selection correctly defers production resets.
            ShowParentConversation(); ShowChatView(); _conversationSelection?.Clear(); UpdateLayout();
            foreach (var expanded in new[] { false, true })
            foreach (var mode in new[] { "general", "coding", "claudescience" })
            {
                await _settings.UpdateAsync(settings => settings with { CodingToolStepsExpanded = expanded });
                var owner = Guid.NewGuid();
                expectedOwner = owner;
                var id = Guid.NewGuid().ToString();
                var at = DateTimeOffset.UtcNow;
                var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
                snapshot["chatMode"] = JsonSerializer.SerializeToElement(mode);
                snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
                snapshot["runMessageId"] = JsonSerializer.SerializeToElement(id);
                snapshot["generationState"] = JsonSerializer.SerializeToElement("tokenProgress");
                snapshot["generatedTokens"] = JsonSerializer.SerializeToElement(7);
                snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(at);
                snapshot["subagents"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
                snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
                {
                    id, sessionId = owner, role = "assistant", content = "", status = "streaming", createdAt = at, updatedAt = at,
                    toolSteps = new object[]
                    {
                        new { id = "old-reasoning", tool = "assistant.reasoning", status = "completed", detail = "Frühere Prüfung.", contentOffset = 0 },
                        new { id = "read", tool = "coding.read", status = "completed", inputJson = "{\"path\":\"example.py\"}",
                            outputJson = "{\"path\":\"example.py\",\"content\":\"x = 2\"}", contentOffset = 0 },
                        new { id = "live-reasoning", tool = "assistant.reasoning", status = "running", detail = "Aktuelle Prüfung.", contentOffset = 0, updatedAt = at },
                    },
                } });
                ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
                ShowChatView(); _conversationSelection?.Clear(); RenderMessagesNow(); RefreshThinkingIndicators();
                phase = "initial-default";
                AssertDefault(id, expanded);
                var initialThinking = _thinkingIndicators[id];
                var initialRead = (ToolStepView)_messageBlocks[id]["tool:read"];

                // An explicit checkbox change applies to retained controls once;
                // subsequent streaming must preserve the new manual choice.
                await _settings.UpdateAsync(settings => settings with { CodingToolStepsExpanded = !expanded });
                phase = "checkbox-toggled";
                RefreshThinkingIndicators(); AssertDefault(id, !expanded);
                if (!ReferenceEquals(initialThinking, _thinkingIndicators[id]) || !ReferenceEquals(initialRead, _messageBlocks[id]["tool:read"]))
                    throw new InvalidOperationException("Die Einstellungsänderung hat vorhandene Werkzeug-/Denkprozess-Controls ersetzt.");
                await _settings.UpdateAsync(settings => settings with { CodingToolStepsExpanded = expanded });
                phase = "checkbox-restored";
                RefreshThinkingIndicators(); AssertDefault(id, expanded);
                await ToggleDisclosureAsync(initialThinking);
                await ToggleDisclosureAsync(initialRead);
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentAppend = "", toolStep = new { id = "live-reasoning", tool = "assistant.reasoning", status = "running",
                        detail = "Aktuelle Prüfung mit weiterem Text.", contentOffset = 0, updatedAt = DateTimeOffset.UtcNow } }));
                RenderMessagesNow(); RefreshThinkingIndicators();
                if (initialThinking.IsExpanded != !expanded || initialRead.IsExpanded != !expanded)
                    throw new InvalidOperationException("Ein Delta desselben Bereichs verliert dessen manuellen Aufklappzustand.");
                ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    generationState = "promptProcessing", runStatus = "Kontext wird verarbeitet", processedPromptTokens = 8,
                    totalPromptTokens = 10, generationUpdatedAt = DateTimeOffset.UtcNow }));
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentAppend = "", toolStep = new { id = "next-read", tool = "coding.read", status = "completed",
                        inputJson = "{\"path\":\"next.py\"}", outputJson = "{\"content\":\"y = 3\"}", contentOffset = 0 } }));
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentAppend = "", toolStep = new { id = "next-reasoning", tool = "assistant.reasoning", status = "running",
                        detail = "Neue Prüfung mit eigenem Aufklappzustand.", contentOffset = 0, updatedAt = DateTimeOffset.UtcNow } }));
                RenderMessagesNow(); RefreshThinkingIndicators();
                phase = "next-step-isolation";
                if (initialThinking.IsExpanded != expanded || ((ToolStepView)_messageBlocks[id]["tool:next-reasoning"]).IsExpanded != expanded
                    || ((ToolStepView)_messageBlocks[id]["tool:next-read"]).IsExpanded != expanded
                    || ((ToolStepView)_messageBlocks[id]["tool:live-reasoning"]).IsExpanded != !expanded || initialRead.IsExpanded != !expanded
                    || _settings.Current.CodingToolStepsExpanded != expanded)
                    throw new InvalidOperationException("Der manuelle Aufklappzustand greift auf nachfolgende Bereiche über oder verändert frühere Bereiche.");
                await ToggleDisclosureAsync(initialThinking);

                _scrollNewSessionToLatest = true;
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentReplace = string.Join("\n\n", Enumerable.Range(0, 300).Select(index =>
                        $"Abschnitt {index}: Dieser vollständige Quellabschnitt prüft die native Blockvirtualisierung und stabile Werkzeugdarstellung.")) }));
                RenderMessagesNow();
                await WaitForPagingSmokeAsync(() => _virtualMessages.TryGetValue(id, out var state) && !state.Preparing
                    && state.Version == MessageVersion(_messages[id]), "Der Disclosure-Smoke hat das logische Blockfenster nicht erreicht.");
                await ThinkingAtTailAsync(id);
                if (_thinkingIndicators[id].IsExpanded != !expanded)
                    throw new InvalidOperationException("Der Wechsel zur Virtualisierung verliert den manuellen Denkprozesszustand.");
                _scrollNewSessionToLatest = false;
                ConversationScroll.ChangeView(null, 0, null, true); RefreshVirtualConversationViewport();
                await WaitForPagingSmokeAsync(() => !_thinkingIndicators.ContainsKey(id)
                    && _messageBlocks[id].TryGetValue("tool:read", out var read) && read.IsLoaded,
                    "Der Disclosure-Smoke hat die Livezeile nicht recycelt und den früheren Werkzeugblock nicht geladen.");
                if (((ToolStepView)_messageBlocks[id]["tool:read"]).IsExpanded != !expanded)
                    throw new InvalidOperationException("Recycling verliert den manuellen Zustand des normalen Werkzeugschritts.");
                await ThinkingAtTailAsync(id);
                if (_thinkingIndicators[id].IsExpanded != !expanded)
                    throw new InvalidOperationException("Die wieder geladene Livezeile ignoriert ihren manuellen Zustand.");
                var virtualContentLength = MessageContent(_messages[id]).Length;
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentAppend = "", toolStep = new { id = "virtual-read", tool = "coding.read", status = "completed",
                        outputJson = "{\"content\":\"z = 4\"}", contentOffset = virtualContentLength } }));
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = id,
                    contentAppend = "", toolStep = new { id = "virtual-reasoning", tool = "assistant.reasoning", status = "running",
                        detail = "Neue Prüfung nach dem Recycling.", contentOffset = virtualContentLength, updatedAt = DateTimeOffset.UtcNow } }));
                RenderMessagesNow();
                await WaitForPagingSmokeAsync(() => _virtualMessages.TryGetValue(id, out var state) && !state.Preparing
                    && state.Version == MessageVersion(_messages[id]), "Neue virtuelle Werkzeugschritte wurden nicht projiziert.");
                await ThinkingAtTailAsync(id);
                if (_thinkingIndicators[id].IsExpanded != expanded
                    || ((ToolStepView)_messageBlocks[id]["tool:virtual-read"]).IsExpanded != expanded
                    || _virtualMessages[id].ExpandedSteps["tool:next-reasoning"] != !expanded
                    || _virtualMessages[id].ExpandedSteps["tool:read"] != !expanded)
                    throw new InvalidOperationException("Die virtuelle Livezeile überträgt eine manuelle Auswahl auf neue Bereiche.");

                if (mode == "general") await VerifyChildDefaultsAsync(owner, id, expanded);
                RefreshConversationTab();
                await ThinkingAtTailAsync(id);
                if (_thinkingIndicators[id].IsExpanded != expanded)
                    throw new InvalidOperationException("Navigation setzt die Livezeile nicht auf den Einstellungsdefault zurück.");
                ApplyEvent("state.snapshot", original); RenderMessagesNow();
                ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); RenderMessagesNow(); RefreshThinkingIndicators();
                phase = "session-navigation";
                AssertDefault(id, expanded);
                cases++;
            }
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-tool-expansion-validation.json"),
                JsonSerializer.Serialize(new { passed = true, cases, modes = 3, defaults = 2,
                    historicalAndLiveReasoningFollowSetting = true, explicitSettingUpdatesRetainedControls = true,
                    manualChoiceSurvivesStreaming = true, manualChoiceSurvivesVirtualizationAndRecycling = true,
                    manualChoiceScopedToReceipt = true, newStepsUseDefaultAfterManualExpansion = true,
                    virtualNewStepsUseDefault = true, subagentManualChoiceScopedToReceipt = true,
                    parentChildNavigationRestoresDefault = true, sessionNavigationRestoresDefault = true,
                    modelRequests = 0, userDataModified = false }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            ShowParentConversation();
            foreach (var child in _subagents.Values) SessionTabsPanel.Children.Remove(child.Container);
            _subagents.Clear();
            foreach (var entry in originalChildren) _subagents[entry.Key] = entry.Value;
            await _settings.UpdateAsync(settings => settings with { CodingToolStepsExpanded = originalDefault });
            ApplyEvent("state.snapshot", original); ShowChatView(); RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }

        void AssertDefault(string messageId, bool expanded)
        {
            if (!_thinkingIndicators.TryGetValue(messageId, out var thinking) || thinking.IsExpanded != expanded
                || _messageBlocks[messageId].Values.OfType<ToolStepView>().Any(tool => tool.IsExpanded != expanded))
                throw new InvalidOperationException("Werkzeug-/Denkprozessdefault ist inkonsistent: " + JsonSerializer.Serialize(new
                {
                    phase, expected = expanded, mode = _mode, messageId, sessionId = _session, activeSubagent = _activeSubagentId,
                    expectedOwner, storedSession = _settings.Current.ActiveSessionId, snapshotSession = S(_snapshot, "activeSessionId"),
                    storedDefault = _settings.Current.CodingToolStepsExpanded, observedDefault = _observedToolExpansionDefault,
                    IsLoaded, bodyVisibility = BodyGrid.Visibility.ToString(), selecting = _conversationSelection?.IsSelecting,
                    dragging = _conversationSelection?.IsDragging, hasSelection = _conversationSelection?.HasSelection,
                    thinkingPresent = _thinkingIndicators.ContainsKey(messageId), thinkingExpanded = thinking?.IsExpanded,
                    messagePresent = _messages.ContainsKey(messageId), messages = _messages.Count,
                    displayMessagePresent = DisplayMessages.ContainsKey(messageId), displayMessages = DisplayMessages.Count,
                    messageViewPresent = _messageViews.ContainsKey(messageId), messageViews = _messageViews.Count,
                    virtualStatePresent = _virtualMessages.ContainsKey(messageId), virtualStates = _virtualMessages.Count,
                    lastCoordinatorEvent = _lastUiEventType, lastCoordinatorRevision = _lastUiEventRevision,
                    tools = _messageBlocks.GetValueOrDefault(messageId)?.Where(pair => pair.Value is ToolStepView)
                        .Select(pair => new { key = pair.Key, expanded = ((ToolStepView)pair.Value).IsExpanded, visibility = pair.Value.Visibility.ToString() }).ToArray(),
                }, JsonOptions));
        }

        async Task ThinkingAtTailAsync(string messageId)
        {
            _scrollNewSessionToLatest = true;
            OnScrollToBottom(this, new RoutedEventArgs()); RefreshVirtualConversationViewport();
            await WaitForPagingSmokeAsync(() => _thinkingIndicators.TryGetValue(messageId, out var thinking) && thinking.IsLoaded,
                "Die Livezeile konnte nach dem Scrollen nicht im nativen Fenster realisiert werden.");
        }

        async Task ToggleDisclosureAsync(FrameworkElement view)
        {
            var button = view is ThinkingIndicatorView thinking ? thinking.ToggleButton : Descendants(view).OfType<Button>().First();
            if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der native Werkzeugbereich besitzt keinen bedienbaren Aufklapp-Button.");
            var wasExpanded = view is ThinkingIndicatorView live ? live.IsExpanded : ((ToolStepView)view).IsExpanded;
            invoke.Invoke();
            await WaitForPagingSmokeAsync(() => (view is ThinkingIndicatorView refreshed ? refreshed.IsExpanded : ((ToolStepView)view).IsExpanded) != wasExpanded,
                "Der native Aufklapp-Button hat den ausgewählten Bereich nicht geändert.");
        }

        async Task VerifyChildDefaultsAsync(Guid owner, string parentId, bool expanded)
        {
            var agent = "tool-expansion-" + Guid.NewGuid().ToString("N");
            var childId = Guid.NewGuid().ToString();
            var at = DateTimeOffset.UtcNow;
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(new { sessionId = owner, agentId = agent,
                runId = Guid.NewGuid(), parentMessageId = parentId, title = "Isolierte Aufklappprüfung", status = "running", isRunning = true,
                projectionRevision = 1, messageId = childId, generationState = "tokenProgress", generatedTokens = 7, generationUpdatedAt = at,
                messages = new object[]
                {
                    new { id = Guid.NewGuid(), sessionId = owner, role = "user", content = "Prüfe die Darstellung.", status = "completed", createdAt = at },
                    new { id = childId, sessionId = owner, role = "assistant", content = "", status = "streaming", createdAt = at, updatedAt = at,
                        toolSteps = new object[] {
                            new { id = "child-read", tool = "coding.read", status = "completed", outputJson = "{\"content\":\"x = 2\"}" },
                            new { id = "child-reasoning", tool = "assistant.reasoning", status = "running", detail = "Prüfung.", updatedAt = at } } },
                } }));
            var child = _subagents[agent];
            phase = "child-first-navigation";
            await ActivateSubagentTabAsync(child); RenderMessagesNow(); RefreshThinkingIndicators(); AssertDefault(childId, expanded);
            await ToggleDisclosureAsync(_thinkingIndicators[childId]);
            await ToggleDisclosureAsync(_messageBlocks[childId]["tool:child-read"]);
            var childMessage = child.Messages[childId].Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            childMessage["updatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
            childMessage["toolSteps"] = JsonSerializer.SerializeToElement(new object[] {
                new { id = "child-read", tool = "coding.read", status = "completed", outputJson = "{\"content\":\"x = 2\"}" },
                new { id = "child-reasoning", tool = "assistant.reasoning", status = "completed", detail = "Prüfung.", updatedAt = at },
                new { id = "child-next-read", tool = "coding.read", status = "completed", outputJson = "{\"content\":\"y = 3\"}" },
                new { id = "child-next-reasoning", tool = "assistant.reasoning", status = "running", detail = "Nächste Prüfung.", updatedAt = DateTimeOffset.UtcNow },
            });
            var childSnapshot = child.Snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            childSnapshot["projectionRevision"] = JsonSerializer.SerializeToElement(2);
            childSnapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
            childSnapshot["messages"] = JsonSerializer.SerializeToElement(new[] { JsonSerializer.SerializeToElement(childMessage) });
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(childSnapshot));
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (_thinkingIndicators[childId].IsExpanded != expanded
                || ((ToolStepView)_messageBlocks[childId]["tool:child-next-read"]).IsExpanded != expanded
                || ((ToolStepView)_messageBlocks[childId]["tool:child-reasoning"]).IsExpanded != !expanded
                || ((ToolStepView)_messageBlocks[childId]["tool:child-read"]).IsExpanded != !expanded)
                throw new InvalidOperationException("Manuelles Ausklappen im Subagenten verändert nachfolgende Werkzeug-/Denkprozessbereiche.");
            ShowParentConversation(); await ThinkingAtTailAsync(parentId);
            if (_thinkingIndicators[parentId].IsExpanded != expanded)
                throw new InvalidOperationException("Rückkehr vom Subagenten ignoriert den Parent-Default.");
            phase = "child-return-navigation";
            await ActivateSubagentTabAsync(child); RenderMessagesNow(); RefreshThinkingIndicators(); AssertDefault(childId, expanded);
            ShowParentConversation();
        }
    }
}
