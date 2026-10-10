using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static readonly string[] SubagentSmokeReasoningLevels = ["none", "medium", "high"];

    private async Task VerifySubagentSmokeAsync(JsonElement original)
    {
        var live = default(JsonElement);
        var liveChildSourcesVisibleInParent = false;
        if (Environment.GetEnvironmentVariable("MISSUM_SMOKE_SUBAGENT_INPUT") is { Length: > 0 } livePath)
        {
            using var liveDocument = JsonDocument.Parse(await File.ReadAllTextAsync(livePath));
            live = liveDocument.RootElement.Clone();
            if (live.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The real-model subagent fixture must be an object with its owner chat mode and transcript.");
        }
        var ownerChatMode = S(live, "chatMode", "coding").Trim().ToLowerInvariant();
        if (ownerChatMode is not ("coding" or "general" or "claudescience"))
            throw new InvalidOperationException("The subagent fixture owner chat mode must be coding, general or claudescience.");
        var originalChildren = _subagents.ToArray();
        var originalInspector = Inspector.Visibility;
        var originalReasoning = _reasoning;
        var originalReasoningModel = _reasoningModel;
        var originalSelectedModel = _settings.Current.SelectedModel;
        const string parentModel = "openai/gpt-oss-120b~native-subagent-smoke";
        const string childModel = "qwen/qwen3.8-27b";
        const string childSourceUrl = "https://docs.scipy.org/doc/scipy/reference/generated/scipy.integrate.solve_ivp.html";
        var session = Guid.NewGuid();
        var parentMessage = Guid.NewGuid().ToString();
        var parentRun = Guid.NewGuid();
        var agentId = "native-subagent-smoke:" + Guid.NewGuid().ToString("N");
        var childMessage = Guid.NewGuid().ToString();
        var childRun = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var child = JsonSerializer.SerializeToElement(new
        {
            sessionId = session, agentId, runId = childRun, parentRunId = parentRun,
            model = childModel,
            title = "Datei analysieren", status = "running", isRunning = true,
            resultDelivered = false,
            contextUsed = 4096, contextLimit = 32768, runMessageId = childMessage,
            generationState = "generating", generatedTokens = 12, generationUpdatedAt = now,
            messages = new object[]
            {
                new { id = Guid.NewGuid(), sessionId = session, role = "user", status = "completed", content = "Analysiere die Datei und berechne $x^2$.", createdAt = now.AddSeconds(-3), updatedAt = now.AddSeconds(-3) },
                new { id = childMessage, sessionId = session, role = "assistant", status = "streaming", content = "Das Ergebnis ist $x^2=4$.\n\n```python\nresult = 2 ** 2\n```", createdAt = now.AddSeconds(-2), updatedAt = now,
                    toolSteps = new object[]
                    {
                        new { id = "child-read", tool = "coding.read", agentId, status = "completed", detail = "Datei: example.py", inputJson = "{\"path\":\"example.py\"}", outputJson = "{\"path\":\"example.py\",\"content\":\"result = 2 ** 2\"}" },
                        new { id = "child-source", tool = "web.fetch", agentId, status = "completed", updatedAt = now,
                            inputJson = JsonSerializer.Serialize(new { url = childSourceUrl }),
                            outputJson = JsonSerializer.Serialize(new { url = childSourceUrl, title = "SciPy solve_ivp", found = true, isUntrusted = true, retrievedAt = now }) },
                    } },
            },
        }, JsonOptions);
        try
        {
            // This profile has isolated settings. Use a deliberately different
            // parent selection and restore it before leaving the smoke fixture.
            await _settings.UpdateAsync(settings => settings with { SelectedModel = parentModel });
            _reasoningModel = (ownerChatMode == "coding" ? "coding:" : "general:") + parentModel;
            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(session);
            snapshot["chatMode"] = JsonSerializer.SerializeToElement(ownerChatMode);
            snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
            snapshot["runId"] = JsonSerializer.SerializeToElement(parentRun);
            snapshot["runMessageId"] = JsonSerializer.SerializeToElement(parentMessage);
            snapshot["generationState"] = JsonSerializer.SerializeToElement("generating");
            snapshot["generatedTokens"] = JsonSerializer.SerializeToElement(24);
            snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(now);
            snapshot["reasoningModelId"] = JsonSerializer.SerializeToElement(parentModel);
            snapshot["draft"] = JsonSerializer.SerializeToElement("Mein Entwurf bleibt erhalten");
            snapshot["subagents"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            snapshot["messages"] = JsonSerializer.SerializeToElement(new object[]
            {
                new { id = Guid.NewGuid(), sessionId = session, role = "user", status = "completed", content = "Delegiere die Analyse.", createdAt = now.AddSeconds(-4), updatedAt = now.AddSeconds(-4) },
                new { id = parentMessage, sessionId = session, role = "assistant", status = "streaming", content = "Ich verarbeite parallel den Hauptauftrag.", createdAt = now.AddSeconds(-3), updatedAt = now,
                    toolSteps = new object[]
                    {
                        new { id = "parent-spawn", tool = "subagent.spawn", status = "completed", outputJson = JsonSerializer.Serialize(new { agentId, runId = childRun }), inputJson = "{\"task\":\"Datei analysieren\"}" },
                        new { id = "parent-delegate", tool = "subagent", agentId, status = "running", outputJson = child.GetRawText(), inputJson = "{\"task\":\"Datei analysieren\"}" },
                        new { id = "parent-wait", tool = "subagent.wait", status = "running", inputJson = JsonSerializer.Serialize(new { runId = childRun }) },
                    } },
            }, JsonOptions);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); RenderMessagesNow();
            _reasoningModel = ModelRole + ":" + parentModel;
            var parentReasoning = JsonSerializer.SerializeToElement(new
            {
                modelId = parentModel, role = ModelRole, selected = "high", levels = SubagentSmokeReasoningLevels,
                defaultLevel = "high", available = true,
            }, JsonOptions);
            ApplyEvent("reasoning.snapshot", parentReasoning);
            var expectedParentEffort = "Hoch";
            var parentBubble = _messageViews[parentMessage].View;
            void VerifyOwnerTabInfrastructure(NativeSubagentState? visibleChild = null)
            {
                if (visibleChild is not null)
                foreach (var message in visibleChild.Messages.Values.Where(message => S(message, "role") == "assistant"))
                {
                    if (!_messageViews.TryGetValue(S(message, "id"), out var view) || view.View.Child is not Grid messageGrid
                        || messageGrid.Children.OfType<Border>().Single().Child is not NativeSubagentAvatar avatar
                        || avatar.Variant != visibleChild.PlanetIndex)
                        throw new InvalidOperationException("Subagent chat messages must use their own persisted planet avatar.");
                }
                if (_mode != ownerChatMode || _sessionTabs.All(tab => tab.Id != session || tab.Mode != ownerChatMode))
                    throw new InvalidOperationException("Subagent navigation changed the owner's chat mode or session tab.");
                if (ownerChatMode == "claudescience"
                    && (_researchTabButton is not { } publicationTab || _simulationTabButton is not { } simulationTab
                        || publicationTab.Parent != SessionTabsPanel || simulationTab.Parent != SessionTabsPanel
                        || SessionTabsPanel.Children.IndexOf(publicationTab) != 1
                        || SessionTabsPanel.Children.IndexOf(simulationTab) != 2
                        || (visibleChild is not null && SessionTabsPanel.Children.IndexOf(visibleChild.Container) < 3)))
                    throw new InvalidOperationException("Claude Science publication and simulation tabs must remain alongside the child's native chat tab.");
            }
            void VerifyParentStillStreaming()
            {
                VerifyOwnerTabInfrastructure();
                if (!_running || !DisplayRunning || ActiveSubagent is not null || !IsConversationMessageRunning(parentMessage)
                    || ModelLabel.Text != "gpt-oss-120b" || ReasoningLabel.Text != expectedParentEffort || !ModelButton.IsEnabled
                    || !MessageBody(parentBubble).Children.OfType<NativeStreamingMarkdown>().Any(markdown => markdown.Children.Count > 0))
                    throw new InvalidOperationException("The active parent run or its native streamed text was lost during child navigation or completion.");
                AssertChatCursorAbsent(MessageBody(parentBubble));
            }
            VerifyParentStillStreaming();
            ApplyEvent("subagent.snapshot", child); UpdateLayout();
            var childState = _subagents[agentId];
            var lifecycle = _messageBlocks[parentMessage]["tool:parent-delegate"] as SubagentLifecycleView
                ?? throw new InvalidOperationException("The parent delegation receipt is not a compact lifecycle button.");
            void VerifyLifecycle(SubagentLifecycleView view, NativeSubagentState state, string suffix)
            {
                if (view.TaskLabel.Text != state.Title || view.StateLabel.Text != suffix || view.Height != 30
                    || view.HorizontalAlignment != HorizontalAlignment.Left
                    || !view.BorderThickness.Equals(new Thickness(0)) || !view.IsTabStop || !view.IsEnabled
                    || view.TaskLabel.TextTrimming != TextTrimming.CharacterEllipsis
                    || ToolTipService.GetToolTip(view) is not string tooltip || !tooltip.Contains(state.Title, StringComparison.Ordinal)
                    || AutomationProperties.GetName(view) != state.Title + " " + suffix
                    || view.Icon.Variant != state.PlanetIndex
                    || view.Icon.IconColor != SubagentIcon(state.AgentId, 14, state.PlanetIndex).IconColor)
                    throw new InvalidOperationException("The compact lifecycle row lost its task, state, stable icon, tooltip or keyboard button semantics.");
            }
            async Task InvokeLifecycleAsync(SubagentLifecycleView view, string expectedAgent)
            {
                var previous = view.InvocationCount;
                if (new ButtonAutomationPeer(view).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke
                    || !view.Focus(FocusState.Keyboard))
                    throw new InvalidOperationException("The lifecycle row cannot be focused or invoked as a native button.");
                invoke.Invoke();
                for (var attempt = 0; attempt < 20 && previous == view.InvocationCount; attempt++)
                    await Task.Delay(10);
                if (view.NavigationTask is not { } navigation || previous == view.InvocationCount)
                    throw new InvalidOperationException("The native lifecycle button did not raise its click action.");
                await navigation;
                if (ActiveSubagent?.AgentId != expectedAgent)
                    throw new InvalidOperationException("Clicking the lifecycle row did not open its own subagent transcript.");
            }
            VerifyLifecycle(lifecycle, childState, "hat die Arbeit begonnen");
            if (WebSourceActions().Count(action => action.Id == "child-source" && action.Urls.Contains(childSourceUrl, StringComparer.Ordinal)) != 1
                || SourcesPanel.Children.Count == 0 || ActiveSubagent is not null)
                throw new InvalidOperationException("A child-only fetched source must appear in the owner's overlay while the parent chat stays active.");
            var foreign = child.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            foreign["sessionId"] = JsonSerializer.SerializeToElement(Guid.NewGuid());
            foreign["agentId"] = JsonSerializer.SerializeToElement("foreign-source-agent");
            foreign["messages"] = JsonSerializer.SerializeToElement(new[] { new
            {
                id = Guid.NewGuid(), role = "assistant", toolSteps = new[] { new
                {
                    id = "foreign-source", tool = "web.fetch", status = "completed",
                    outputJson = "{\"url\":\"https://example.com/foreign-session-only\"}",
                } },
            } });
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(foreign));
            if (WebSourceActions().Any(action => action.Id == "foreign-source"))
                throw new InvalidOperationException("The owner's source overlay includes a child from another session.");
            if (_messageBlocks[parentMessage].ContainsKey("tool:parent-spawn") || _messageBlocks[parentMessage].ContainsKey("tool:parent-wait")
                || MessageBody(parentBubble).Children.OfType<SubagentLifecycleView>().Count() != 1)
                throw new InvalidOperationException("Accepted spawn/wait calls must share the child's single lifecycle row.");
            var failedManager = JsonSerializer.SerializeToElement(new { tool = "subagent.wait", status = "failed", inputJson = JsonSerializer.Serialize(new { runId = childRun }) });
            if (IsAcceptedSubagentManagementStep(failedManager, Items(_messages[parentMessage], "toolSteps").Where(step => S(step, "tool") == "subagent").ToArray()))
                throw new InvalidOperationException("Failed manager calls must retain their own tool feedback.");
            if (ActiveSubagent is not null || !ReferenceEquals(parentBubble, _messageViews[parentMessage].View)
                || childState.Container.Parent != SessionTabsPanel || SubagentsSection.Visibility != Visibility.Visible
                || SubagentsPanel.Children.Count != 1)
                throw new InvalidOperationException("Subagent discovery must create a tab and output entry while preserving the active parent.");
            var summaryButton = VerifySubagentSummarySmoke("1 arbeitet");
            var summaryIcons = SubagentSummarySmokeIcons(summaryButton);
            (string State, bool Running)[] overlayStates =
            [
                ("pending", true), ("queued", true), ("waiting", true),
                ("waitingForClient", true), ("running", true), ("completed", false),
                ("failed", false), ("cancelled", false),
                ("interrupted", false), ("unavailable", false),
            ];
            var originalChildStatus = childState.Status;
            var originalChildRunning = childState.IsRunning;
            foreach (var state in overlayStates)
            {
                childState.Status = state.State; childState.IsRunning = state.Running; RenderSubagentOverlay();
                if (!ReferenceEquals(summaryButton, VerifySubagentSummarySmoke(state.Running ? "1 arbeitet" : "1 fertig"))
                    || !summaryIcons.SequenceEqual(SubagentSummarySmokeIcons(summaryButton)))
                    throw new InvalidOperationException("Subagent status updates must preserve the compact summary button and icon controls.");
                if (!state.Running && state.State != "completed"
                    && ToolTipService.GetToolTip(summaryButton) is string stateTooltip
                    && !stateTooltip.Contains(SubagentStatusLabel(childState), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The aggregate finished caption must retain unsuccessful states in its compact tooltip.");
            }
            childState.Status = originalChildStatus; childState.IsRunning = originalChildRunning;
            RenderSubagentOverlay(); VerifySubagentSummarySmoke("1 arbeitet");
            VerifyParentStillStreaming();
            // Restore old terminal children while a new child is active: their
            // transcripts remain available without flooding the native tab strip.
            var historicalIds = new List<string>();
            try
            {
                foreach (var historicalStatus in new[] { "completed", "failed", "cancelled" })
                {
                    var historicalId = "historical-tab-smoke:" + historicalStatus + ":" + session;
                    historicalIds.Add(historicalId);
                    var historical = child.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                    historical["agentId"] = JsonSerializer.SerializeToElement(historicalId);
                    historical["status"] = JsonSerializer.SerializeToElement(historicalStatus);
                    // Deliberately stale flag: the terminal status is authoritative.
                    historical["isRunning"] = JsonSerializer.SerializeToElement(true);
                    historical["messages"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
                    ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(historical));
                    if (_subagents[historicalId].TabOpen || _subagents[historicalId].Container.Parent is not null)
                        throw new InvalidOperationException("Restoring a historical terminal child opened an unwanted tab.");
                }
            }
            finally
            {
                foreach (var historicalId in historicalIds)
                    if (_subagents.Remove(historicalId, out var historicalState))
                        SessionTabsPanel.Children.Remove(historicalState.Container);
                RenderSubagentOverlay(); RenderSessionTabs();
            }
            if (new ButtonAutomationPeer(childState.Close).GetPattern(PatternInterface.Invoke) is not IInvokeProvider closeChild)
                throw new InvalidOperationException("The live child tab lacks an accessible close button.");
            closeChild.Invoke();
            for (var attempt = 0; attempt < 20 && childState.TabOpen; attempt++) await Task.Delay(10);
            if (childState.TabOpen || childState.Container.Parent is not null
                || !SubagentTabStates.WasManuallyClosed(session, agentId))
                throw new InvalidOperationException("Closing a running child tab did not preserve the user's choice.");
            ApplyEvent("subagent.snapshot", child);
            if (childState.TabOpen)
                throw new InvalidOperationException("A background child update reopened its manually closed tab.");
            _subagents.Remove(agentId);
            _subagentTabStates = new SubagentTabStateStore(App.Current.DataDirectory);
            ApplyEvent("subagent.snapshot", child);
            childState = _subagents[agentId];
            if (childState.TabOpen || childState.Container.Parent is not null)
                throw new InvalidOperationException("Rehydrating a live child ignored its persisted closed-tab state.");
            VerifyParentStillStreaming();
            Inspector.Visibility = Visibility.Visible; UpdateLayout();
            if (SubagentsHeading.TransformToVisual(Inspector).TransformPoint(new(0, 0)).Y
                >= SourcesHeading.TransformToVisual(Inspector).TransformPoint(new(0, 0)).Y)
                throw new InvalidOperationException("The Subagenten heading must appear above Quellen in Ausgaben.");
            await SaveMathPreviewAsync(Inspector, "native-subagents-outputs-preview.png");
            await InvokeLifecycleAsync(lifecycle, agentId); RenderMessagesNow(); UpdateLayout();
            VerifyOwnerTabInfrastructure(childState);
            if (DisplayMessages.Count != 2 || MessagesPanel.Children.Count != 2 || _messages.Count != 2
                || !_running || !DisplayRunning
                || !Composer.IsReadOnly || _messageBlocks[childMessage]["header"] is not StackPanel
                || !_messageBlocks[childMessage].ContainsKey("tool:child-read")
                || PromptTimeline.Visibility != Visibility.Visible || _promptTimelineMarkers.Count != 1)
                throw new InvalidOperationException("The child tab did not reuse the native message, tool and timeline renderer.");
            AssertChatCursorAbsent(MessageBody(_messageViews[childMessage].View));
            void VerifyChildModel(string effort)
            {
                if (ModelLabel.Text != "qwen3.8-27b" || ReasoningLabel.Text != effort || ModelButton.IsEnabled)
                    throw new InvalidOperationException("The readonly child footer must show its actual Qwen model and only its own reasoning metadata.");
            }
            VerifyChildModel("");
            var delayedReasoning = parentReasoning.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            delayedReasoning["selected"] = JsonSerializer.SerializeToElement("medium");
            ApplyEvent("reasoning.snapshot", JsonSerializer.SerializeToElement(delayedReasoning));
            expectedParentEffort = "Mittel";
            VerifyChildModel("");
            var ownReasoning = child.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            ownReasoning["reasoningEffort"] = JsonSerializer.SerializeToElement("none");
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(ownReasoning));
            VerifyChildModel("Ohne");
            var childBubble = _messageViews[childMessage].View;
            var childBody = MessageBody(childBubble);
            if (!childBody.Children.OfType<NativeStreamingMarkdown>().SelectMany(markdown => markdown.Children)
                    .OfType<NativeMathParagraph>().Any()
                || _messageActionViews[childMessage].Panel.Visibility != Visibility.Visible
                || !_messageActionViews[childMessage].Copy.IsEnabled || !_messageActionViews[childMessage].Read.IsEnabled)
                throw new InvalidOperationException("Child mathematics or streaming footer behavior differs from the parent renderer.");
            Inspector.Visibility = Visibility.Collapsed;
            await SaveMathPreviewAsync(LayoutRoot, "native-subagent-chat-preview.png");
            ShowParentConversation();
            if (Composer.Text != "Mein Entwurf bleibt erhalten" || Composer.IsReadOnly
                || !ReferenceEquals(parentBubble, _messageViews[parentMessage].View)
                || MessagesPanel.Children.Contains(childBubble))
                throw new InvalidOperationException("Returning from a child discarded the parent draft or transcript.");
            VerifyParentStillStreaming();
            var completed = child.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            completed["status"] = JsonSerializer.SerializeToElement("completed");
            completed["isRunning"] = JsonSerializer.SerializeToElement(false);
            var finalMessages = Items(child, "messages");
            var finalAssistant = finalMessages[1].Deserialize<Dictionary<string, JsonElement>>()!;
            finalAssistant["status"] = JsonSerializer.SerializeToElement("completed");
            finalMessages[1] = JsonSerializer.SerializeToElement(finalAssistant);
            completed["messages"] = JsonSerializer.SerializeToElement(finalMessages);
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(completed));
            VerifySubagentSummarySmoke("1 fertig");
            if (!ReferenceEquals(lifecycle, _messageBlocks[parentMessage]["tool:parent-delegate"]))
                throw new InvalidOperationException("Child completion replaced the cached lifecycle row.");
            VerifyLifecycle(lifecycle, childState, "hat die Arbeit begonnen");
            if (MessageBody(parentBubble).Children.OfType<SubagentLifecycleView>().Count() != 1)
                throw new InvalidOperationException("A completed child must not post an end notification before result delivery.");
            completed["resultDelivered"] = JsonSerializer.SerializeToElement(true);
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(completed));
            if (!ReferenceEquals(lifecycle, _messageBlocks[parentMessage]["tool:parent-delegate"]))
                throw new InvalidOperationException("Result delivery replaced the cached lifecycle row.");
            VerifyLifecycle(lifecycle, childState, "hat die Arbeit begonnen");
            var parentWithCompletion = _messages[parentMessage].Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            var continuedParentText = S(_messages[parentMessage], "content") + "\n\nIch habe meinen eigenen Forschungsstrang weiterbearbeitet.";
            parentWithCompletion["content"] = JsonSerializer.SerializeToElement(continuedParentText);
            var completionStep = JsonSerializer.SerializeToElement(new
            {
                id = "parent-delegate-completed", tool = "subagent.completed", agentId, status = "completed",
                contentOffset = continuedParentText.Length,
                outputJson = JsonSerializer.Serialize(new { agentId, runId = childRun, title = "Datei analysieren", status = "completed", resultDelivered = true }),
            });
            parentWithCompletion["toolSteps"] = JsonSerializer.SerializeToElement(Items(_messages[parentMessage], "toolSteps").Append(completionStep));
            _messages[parentMessage] = JsonSerializer.SerializeToElement(parentWithCompletion, JsonOptions);
            RenderMessagesNow(); UpdateLayout();
            var completionLifecycle = _messageBlocks[parentMessage]["tool:parent-delegate-completed"] as SubagentLifecycleView
                ?? throw new InvalidOperationException("Successful delivery did not create its separate clickable end notification.");
            VerifyLifecycle(lifecycle, childState, "hat die Arbeit begonnen");
            VerifyLifecycle(completionLifecycle, childState, "hat die Arbeit beendet");
            RenderMessagesNow();
            if (!ReferenceEquals(lifecycle, _messageBlocks[parentMessage]["tool:parent-delegate"])
                || !ReferenceEquals(completionLifecycle, _messageBlocks[parentMessage]["tool:parent-delegate-completed"])
                || MessageBody(parentBubble).Children.OfType<SubagentLifecycleView>().Count() != 2
                || MessageBody(parentBubble).Children.IndexOf(lifecycle) >= MessageBody(parentBubble).Children.IndexOf(completionLifecycle))
                throw new InvalidOperationException("Start/end notifications must stay ordered, separate and idempotent across rendering replays.");
            Inspector.Visibility = Visibility.Collapsed; UpdateLayout();
            await SaveMathPreviewAsync(LayoutRoot, "native-subagent-lifecycle-preview.png");
            if (!ReferenceEquals(parentBubble, _messageViews[parentMessage].View) || ActiveSubagent is not null)
                throw new InvalidOperationException("A background child completion altered the visible parent chat.");
            VerifyParentStillStreaming();
            await InvokeLifecycleAsync(completionLifecycle, agentId); RenderMessagesNow();
            VerifyOwnerTabInfrastructure(childState);
            VerifyChildModel("");
            if (!ReferenceEquals(childBubble, _messageViews[childMessage].View)
                || !_running || DisplayRunning
                || _messageBlocks[childMessage].ContainsKey("streamCursor")
                || _messageActionViews[childMessage].Panel.Parent != childBody
                || _messageActionViews[childMessage].Panel.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Child completion lost its cached native bubble or footer ownership.");
            AssertChatCursorAbsent(childBody);
            childState.TabOpen = false; ShowParentConversation(); RenderSessionTabs();
            VerifyParentStillStreaming();
            if (childState.Container.Parent is not null || SubagentsPanel.Children.Count != 1)
                throw new InvalidOperationException("Closing a child tab must preserve its output entry for reopening.");
            await InvokeLifecycleAsync(lifecycle, agentId);
            VerifyOwnerTabInfrastructure(childState);
            if (childState.Container.Parent != SessionTabsPanel)
                throw new InvalidOperationException("The output entry could not reopen the closed child tab.");
            ShowParentConversation();
            VerifyParentStillStreaming();
            SyncSubagents(JsonSerializer.SerializeToElement(new { subagents = new[] { childState.Snapshot } }), sessionChanged: true);
            RenderSessionTabs();
            if (childState.TabOpen || childState.Container.Parent is not null)
                throw new InvalidOperationException("Returning to a warm session reopened its historical completed child tab.");
            await InvokeLifecycleAsync(lifecycle, agentId);
            ShowParentConversation(); VerifyParentStillStreaming();
            await VerifySubagentOverviewSmokeAsync(child, parentMessage);
            VerifyParentStillStreaming();
            if (live.ValueKind == JsonValueKind.Object)
            {
                var liveChild = Items(live, "subagents").LastOrDefault();
                if (liveChild.ValueKind != JsonValueKind.Object) liveChild = live;
                var scoped = liveChild.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                scoped["sessionId"] = JsonSerializer.SerializeToElement(session);
                var liveId = S(liveChild, "agentId");
                if (liveId.Length == 0 || Items(liveChild, "messages").Length < 2)
                    throw new InvalidOperationException("The real-model subagent fixture has no assigned task and result transcript.");
                ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(scoped));
                var owner = _messages[parentMessage].Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                var liveSteps = new List<JsonElement> { JsonSerializer.SerializeToElement(new
                {
                    id = "live-parent-delegate", tool = "subagent", agentId = liveId,
                    status = S(liveChild, "status"), outputJson = JsonSerializer.Serialize(scoped, JsonOptions),
                }, JsonOptions) };
                if (liveChild.TryGetProperty("resultDelivered", out var received) && received.ValueKind == JsonValueKind.True)
                    liveSteps.Add(JsonSerializer.SerializeToElement(new
                    {
                        id = "live-parent-completed", tool = "subagent.completed", agentId = liveId, status = "completed",
                        contentOffset = S(_messages[parentMessage], "content").Length, outputJson = JsonSerializer.Serialize(scoped, JsonOptions),
                    }, JsonOptions));
                owner["toolSteps"] = JsonSerializer.SerializeToElement(liveSteps);
                _messages[parentMessage] = JsonSerializer.SerializeToElement(owner, JsonOptions);
                RenderMessagesNow(); UpdateLayout();
                var liveLifecycle = _messageBlocks[parentMessage]["tool:live-parent-delegate"] as SubagentLifecycleView
                    ?? throw new InvalidOperationException("The real-model child has no parent lifecycle button.");
                var liveDelivered = liveChild.TryGetProperty("resultDelivered", out var delivered) && delivered.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? delivered.GetBoolean() : (bool?)null;
                VerifyLifecycle(liveLifecycle, _subagents[liveId], liveDelivered is null && S(liveChild, "status") == "completed"
                    ? "hat die Arbeit beendet" : "hat die Arbeit begonnen");
                var actualChildSources = WebSourceActions().Where(action => _subagents[liveId].Messages.Values
                    .SelectMany(message => Items(message, "toolSteps")).Any(step => S(step, "id") == action.Id)).ToArray();
                if (actualChildSources.Length > 0)
                {
                    if (ActiveSubagent is not null || !_sessionSources.GetValueOrDefault(session, []).Any(action => actualChildSources.Any(childAction => childAction.Id == action.Id)))
                        throw new InvalidOperationException("The real child sources are missing from the parent overlay before opening its tab.");
                    liveChildSourcesVisibleInParent = true;
                }
                Inspector.Visibility = Visibility.Collapsed; UpdateLayout();
                await SaveMathPreviewAsync(LayoutRoot, "native-subagent-live-lifecycle-preview.png");
                _subagents[liveId].TabOpen = false; RenderSessionTabs();
                await InvokeLifecycleAsync(liveLifecycle, liveId); UpdateLayout();
                if (liveDelivered is true)
                {
                    ShowParentConversation(); UpdateLayout();
                    var liveCompletion = _messageBlocks[parentMessage]["tool:live-parent-completed"] as SubagentLifecycleView
                        ?? throw new InvalidOperationException("The real delivered child has no separate end notification.");
                    VerifyLifecycle(liveCompletion, _subagents[liveId], "hat die Arbeit beendet");
                    await InvokeLifecycleAsync(liveCompletion, liveId); UpdateLayout();
                }
                VerifyOwnerTabInfrastructure(_subagents[liveId]);
                var liveModel = S(liveChild, "model", S(liveChild, "modelId"));
                var liveEffort = S(liveChild, "reasoningEffort");
                if (liveModel.Length == 0 || ModelLabel.Text != liveModel.Split('/').Last().Split('~')[0]
                    || ReasoningLabel.Text != (liveEffort.Length > 0 ? EffortLabel(liveEffort) : "") || ModelButton.IsEnabled)
                    throw new InvalidOperationException("The real-model child footer does not match its own recorded model identity.");
                await SaveMathPreviewAsync(LayoutRoot, "native-subagent-live-chat-preview.png");
                ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true); UpdateLayout();
                await SaveMathPreviewAsync(LayoutRoot, "native-subagent-live-response-preview.png");
                Inspector.Visibility = Visibility.Visible; UpdateLayout();
                await SaveMathPreviewAsync(Inspector, "native-subagent-live-outputs-preview.png");
                var liveParentDraft = _parentComposerDraft;
                OpenSubagentOverview(); UpdateLayout();
                if (_activeSubagentOverviewSession != session || ActiveSubagent is not null
                    || Composer.Text != liveParentDraft || !SubagentOverviewRows().Any(button => Equals(button.Tag, liveId))
                    || SubagentsPanel.Children.Count != 1)
                    throw new InvalidOperationException("The real child overview lost its transcript owner or parent draft.");
                await VerifySubagentOverviewTabVisibleAsync();
                await SaveMathPreviewAsync(LayoutRoot, "native-subagent-live-overview-preview.png");
                await InvokeOverviewButtonAsync(SubagentOverviewRows().Single(button => Equals(button.Tag, liveId)), liveId);
                VerifyOwnerTabInfrastructure(_subagents[liveId]);
            }
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-subagent-validation.json"),
                JsonSerializer.Serialize(new { renderer = "WinUI3", passed = true, ownerChatMode, parentStable = true, childUsesNativeRenderer = true,
                    overlayAboveSources = true, closableAndReopenable = true, parentDraftPreserved = true,
                    parentRunRemainedActive = true, chatCursorAbsent = true,
                    childModelIdentityPreserved = true,
                    compactLifecycleRow = true, lifecycleStartEntryPreserved = true, lifecycleCompletionPostedOnce = true, lifecycleClickOpensChild = true,
                    lifecycleKeyboardAccessible = true, acceptedManagerStepsCoalesced = true,
                    lifecycleCompletionWaitsForDelivery = true,
                    compactSubagentSummary = true, activeInactiveCountsCorrect = true, summaryControlsStable = true,
                    uniquePlanetIcons = true, planetIdentityConsistent = true,
                    historicalTabsStayClosed = true, closedRunningTabSurvivesRehydration = true,
                    summaryClickOpensOverview = true, noSubagentAllLink = true,
                    latestSourceOnly = true, completeSubagentOverview = true,
                    completeSourcesOverview = true, overviewSessionIsolated = true, overviewClosableAndReopenable = true,
                    subagentRecencyStable = true, overviewNativeKeyboardAccessible = true, overviewTabFullyVisible = true,
                    overviewBackgroundUpdatesPreserveTabScroll = true,
                    childSourcesVisibleInParent = true, parentSourceUpdatesWhileChildVisible = true,
                    sourceSessionIsolated = true, liveChildSourcesVisibleInParent,
                    liveLifecycleClickOpensChild = live.ValueKind == JsonValueKind.Object,
                    scienceTabsPreserved = ownerChatMode == "claudescience",
                    liveTranscriptRendered = live.ValueKind == JsonValueKind.Object }));
        }
        finally
        {
            await _settings.UpdateAsync(settings => settings with { SelectedModel = originalSelectedModel });
            _subagentOverviewTabs.Remove(session); HideSubagentOverview();
            ShowParentConversation();
            foreach (var state in _subagents.Values) SessionTabsPanel.Children.Remove(state.Container);
            _subagents.Clear(); foreach (var entry in originalChildren) _subagents[entry.Key] = entry.Value;
            _subagentOverlaySignature = null; Inspector.Visibility = originalInspector;
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            _reasoning = originalReasoning; _reasoningModel = originalReasoningModel; RefreshComposerModelDisplay();
        }
    }

    private Button[] SubagentOverviewRows() => _subagentOverviewHost.Content is ScrollViewer { Content: StackPanel body }
        ? body.Children.OfType<Button>().ToArray() : [];

    private Button VerifySubagentSummarySmoke(string expectedLabel, string? expectedFinished = null)
    {
        if (SubagentsPanel.Children.Count != 1 || SubagentsPanel.Children[0] is not Button button
            || button.Content is not Grid row || row.Children.Count != 3
            || row.Children[0] is not StackPanel { Orientation: Orientation.Horizontal } icons
            || row.Children[1] is not TextBlock label || label.Text != expectedLabel
            || row.Children[2] is not TextBlock finished
            || (expectedFinished is null ? finished.Visibility != Visibility.Collapsed
                : finished.Visibility != Visibility.Visible || finished.Text != expectedFinished)
            || AutomationProperties.GetName(button) != expectedLabel + (expectedFinished is null ? "" : " · " + expectedFinished)
            || !button.IsEnabled || !button.IsTabStop)
            throw new InvalidOperationException("Subagents must use a single compact accessible icon-and-count summary without a separate all-items link.");
        var children = SessionSubagents();
        foreach (var child in children)
        {
            var tabIcon = ((StackPanel)child.Select.Content).Children.OfType<NativeSubagentAvatar>().Single();
            if (tabIcon.Variant != child.PlanetIndex || PlanetIdentities.GetOrAssign(child.AgentId) != child.PlanetIndex)
                throw new InvalidOperationException("The same subagent must retain its stored planet in tabs and the overlay.");
        }
        if (ToolTipService.GetToolTip(button) is not string tooltip || !tooltip.StartsWith(expectedLabel, StringComparison.Ordinal)
            || !tooltip.Contains("Subagentenübersicht öffnen", StringComparison.Ordinal))
            throw new InvalidOperationException("The compact summary tooltip must state its aggregate status and complete-overview navigation.");
        var active = children.Where(child => child.IsRunning).ToArray();
        var representatives = (active.Length > 0 ? active : children).Take(4).ToArray();
        if (children.Select(child => child.PlanetIndex).Distinct().Count() != children.Length)
            throw new InvalidOperationException("Different subagents must never share the same planet identity.");
        if (icons.Children.Count != representatives.Length || icons.Children.Count > 4
            || icons.Children.OfType<NativeSubagentAvatar>().Count() != icons.Children.Count)
            throw new InvalidOperationException("The compact subagent summary must keep a bounded representative colored icon cluster.");
        for (var index = 0; index < icons.Children.Count; index++)
        {
            var actual = (NativeSubagentAvatar)icons.Children[index];
            var expected = SubagentIcon(representatives[index].AgentId, 14, representatives[index].PlanetIndex);
            if (actual.Variant != expected.Variant || actual.IconColor != expected.IconColor)
                throw new InvalidOperationException("The compact subagent summary must preserve each displayed agent's stable colored shape.");
        }
        return button;
    }

    private static NativeSubagentAvatar[] SubagentSummarySmokeIcons(Button summary) => ((StackPanel)((Grid)summary.Content).Children[0])
        .Children.OfType<NativeSubagentAvatar>().ToArray();

    private async Task VerifySubagentOverviewTabVisibleAsync()
    {
        var tab = _subagentOverviewTabContainer ?? throw new InvalidOperationException("The overview tab is missing.");
        var scroll = SubagentOverviewTabScroll() ?? throw new InvalidOperationException("The tab strip has no horizontal scroll owner.");
        var close = ((Grid)tab.Child).Children.OfType<Button>().Single(button => button != _subagentOverviewTabButton);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            UpdateLayout();
            var left = tab.TransformToVisual(scroll).TransformPoint(new(0, 0)).X;
            var closeLeft = close.TransformToVisual(scroll).TransformPoint(new(0, 0)).X;
            if (tab.ActualWidth > 0 && close.ActualWidth > 0 && left >= -1
                && left + tab.ActualWidth <= scroll.ViewportWidth + 1 && closeLeft >= -1
                && closeLeft + close.ActualWidth <= scroll.ViewportWidth + 1) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Explicit overview navigation must reveal its full title and close button in the horizontal tab viewport.");
    }

    private async Task InvokeOverviewButtonAsync(Button button, string? childId = null)
    {
        if (!button.Focus(FocusState.Keyboard)
            || new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("The subagent overview navigation is not keyboard-accessible.");
        invoke.Invoke();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (childId is null ? _activeSubagentOverviewSession == _session : ActiveSubagent?.AgentId == childId) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The subagent overview button did not open its intended session view.");
    }

    private async Task VerifySubagentOverviewSmokeAsync(JsonElement template, string parentMessage)
    {
        var owner = _session;
        var inspectorVisibility = Inspector.Visibility;
        var parent = _messages[parentMessage].Clone();
        var draft = Composer.Text;
        var addedIds = new List<string>();
        var now = DateTimeOffset.UtcNow;
        try
        {
            for (var index = 1; index <= 3; index++)
            {
                var id = "overview-smoke-" + index + ":" + owner;
                addedIds.Add(id);
                var child = template.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                child["agentId"] = JsonSerializer.SerializeToElement(id);
                child["title"] = JsonSerializer.SerializeToElement("Forschungsansatz " + index);
                child["status"] = JsonSerializer.SerializeToElement("running");
                child["isRunning"] = JsonSerializer.SerializeToElement(true);
                // Production DTO recency comes from the assigned user message.
                child["messages"] = JsonSerializer.SerializeToElement(new object[]
                {
                    new { id = Guid.NewGuid(), role = "user", content = "Prüfe Ansatz " + index, createdAt = now.AddSeconds(index), status = "completed" },
                    new { id = Guid.NewGuid(), role = "assistant", content = "Ich prüfe diesen Ansatz.", createdAt = now.AddSeconds(index), status = "streaming" },
                }, JsonOptions);
                ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(child));
                _subagents[id].TabOpen = false;
            }
            var withSources = parent.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            var sources = Enumerable.Range(1, 4).Select(index => JsonSerializer.SerializeToElement(new
            {
                id = "overview-source-" + index, tool = "web.fetch", status = "completed", updatedAt = now.AddSeconds(index + 10),
                outputJson = JsonSerializer.Serialize(new { title = "Fachquelle " + index, url = "https://example.com/overview/" + index }),
            })).ToArray();
            withSources["toolSteps"] = JsonSerializer.SerializeToElement(Items(parent, "toolSteps").Concat(sources));
            _messages[parentMessage] = JsonSerializer.SerializeToElement(withSources, JsonOptions);
            _sourcesSignature = null; RenderSources(_snapshot); RenderSessionTabs();
            Inspector.Visibility = Visibility.Visible; UpdateLayout();
            var newest = addedIds[^1];
            if (SessionSubagents().Length != 4 || SessionSubagents()[0].AgentId != newest
                || SubagentsPanel.Children.Count != 1 || SourcesPanel.Children.Count != 2
                || AutomationProperties.GetName((Button)SubagentsPanel.Children[0]) != "3 arbeiten · 1 fertig"
                || !AutomationProperties.GetName((Button)SourcesPanel.Children[0]).StartsWith("Fachquelle 4 ·", StringComparison.Ordinal))
                throw new InvalidOperationException("Outputs must contain the active subagent count and the latest source with its complete-overview link.");
            var summary = VerifySubagentSummarySmoke("3 arbeiten", "1 fertig");
            var summaryIcons = SubagentSummarySmokeIcons(summary);
            var allSources = (Button)SourcesPanel.Children[1];
            if (AutomationProperties.GetName(allSources) != "Alle Quellen anzeigen")
                throw new InvalidOperationException("Sources must retain their complete-overview affordance independently of the compact subagent summary.");
            var tokenUpdate = _subagents[newest].Snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            tokenUpdate["generatedTokens"] = JsonSerializer.SerializeToElement(2048);
            tokenUpdate["generationUpdatedAt"] = JsonSerializer.SerializeToElement(now.AddMinutes(1));
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(tokenUpdate)); UpdateLayout();
            if (!ReferenceEquals(summary, VerifySubagentSummarySmoke("3 arbeiten", "1 fertig"))
                || !summaryIcons.SequenceEqual(SubagentSummarySmokeIcons(summary)))
                throw new InvalidOperationException("Token-only subagent snapshots must retain the summary's button, icon controls and label.");
            await SaveMathPreviewAsync(Inspector, "native-subagents-active-summary-preview.png");
            await InvokeOverviewButtonAsync(summary); UpdateLayout();
            if (_subagentOverviewTabContainer?.Parent != SessionTabsPanel || SubagentOverviewRows().Length != 4
                || BodyGrid.Visibility != Visibility.Collapsed || _sourcesHost.Visibility != Visibility.Collapsed
                || SubagentOverviewRows()[0].Tag as string != newest || Composer.Text != draft)
                throw new InvalidOperationException("The session's subagent overview lost its complete ordered list or parent draft.");
            await VerifySubagentOverviewTabVisibleAsync();
            foreach (var overviewRow in SubagentOverviewRows())
            {
                var rowAgent = _subagents[(string)overviewRow.Tag];
                if (((Grid)overviewRow.Content).Children.OfType<NativeSubagentAvatar>().Single().Variant != rowAgent.PlanetIndex)
                    throw new InvalidOperationException("Subagent overview rows must use the same planet as their tabs and chat messages.");
            }
            var tabScroll = SubagentOverviewTabScroll()!;
            var navigationOffset = tabScroll.HorizontalOffset;
            var noDateIds = new[] { "undated-first:" + owner, "undated-second:" + owner };
            foreach (var id in noDateIds)
            {
                addedIds.Add(id);
                ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(new
                { sessionId = owner, agentId = id, title = "Ansatz ohne Zeitstempel", status = "running", isRunning = true, messages = Array.Empty<object>() }));
                _subagents[id].TabOpen = false;
            }
            var fallbackOrder = SessionSubagents().Where(state => state.CreatedAt is null).Select(state => state.AgentId).ToArray();
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(new
            { sessionId = owner, agentId = noDateIds[0], title = "Ansatz ohne Zeitstempel", status = "completed", isRunning = false, messages = Array.Empty<object>() }));
            if (!fallbackOrder.SequenceEqual(noDateIds.Reverse())
                || !fallbackOrder.SequenceEqual(SessionSubagents().Where(state => state.CreatedAt is null).Select(state => state.AgentId)))
                throw new InvalidOperationException("Snapshots without creation dates must retain a stable observation order across completion.");
            foreach (var id in noDateIds)
                if (_subagents.Remove(id, out var undated)) SessionTabsPanel.Children.Remove(undated.Container);
            RenderSubagentOverlay();
            // An older child completing changes its row, never the newest entry.
            var older = _subagents[addedIds[0]].Snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            older["title"] = JsonSerializer.SerializeToElement("Erster Ansatz geprüft");
            older["status"] = JsonSerializer.SerializeToElement("completed");
            older["isRunning"] = JsonSerializer.SerializeToElement(false);
            older["generationUpdatedAt"] = JsonSerializer.SerializeToElement(now.AddHours(1));
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(older)); UpdateLayout();
            if (SessionSubagents()[0].AgentId != newest || _activeSubagentOverviewSession != owner
                || !SubagentOverviewRows().Any(button => AutomationProperties.GetName(button) == "Erster Ansatz geprüft · fertig")
                || Math.Abs(tabScroll.HorizontalOffset - navigationOffset) > 1)
                throw new InvalidOperationException("Child metadata must update the overview without changing creation order or its active view.");
            VerifySubagentSummarySmoke("2 arbeiten", "2 fertig");
            var completedSummary = (Button)SubagentsPanel.Children[0];
            foreach (var state in SessionSubagents())
            {
                var finished = state.Snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                finished["status"] = JsonSerializer.SerializeToElement("completed");
                finished["isRunning"] = JsonSerializer.SerializeToElement(false);
                ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(finished));
            }
            UpdateLayout();
            if (!ReferenceEquals(completedSummary, VerifySubagentSummarySmoke("4 fertig"))
                || SubagentSummarySmokeIcons(completedSummary).Length != 4)
                throw new InvalidOperationException("Finishing every subagent must retain the summary button and display four individual planets with its finished count.");
            // The overview hides BodyGrid, which owns Inspector. Expose the
            // overlay for the isolated bitmap without navigating away from its tab.
            var bodyVisibility = BodyGrid.Visibility;
            try
            {
                BodyGrid.Visibility = Visibility.Visible;
                Inspector.Visibility = Visibility.Visible;
                await SaveMathPreviewAsync(Inspector, "native-subagents-finished-summary-preview.png");
            }
            finally { BodyGrid.Visibility = bodyVisibility; UpdateLayout(); }
            await VerifySubagentOverviewTabVisibleAsync();
            await SaveMathPreviewAsync(LayoutRoot, "native-subagent-overview-preview.png");
            await InvokeOverviewButtonAsync(SubagentOverviewRows()[0], newest); UpdateLayout();
            if (_subagentOverviewHost.Visibility != Visibility.Collapsed || Composer.IsReadOnly is false)
                throw new InvalidOperationException("An overview entry must open its child's native readonly transcript.");
            // A parent tool receipt must invalidate the owner-wide source cache
            // even when the child transcript has not changed at all.
            var sourceSignature = _sourcesSignature;
            var parentWithNewSource = _messages[parentMessage].Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            var newestParentSource = JsonSerializer.SerializeToElement(new
            {
                id = "overview-parent-source-after-navigation", tool = "web.fetch", status = "completed", updatedAt = now.AddHours(1),
                outputJson = "{\"title\":\"Neue Hauptagent-Quelle\",\"url\":\"https://example.com/new-parent-source\"}",
            });
            parentWithNewSource["toolSteps"] = JsonSerializer.SerializeToElement(Items(_messages[parentMessage], "toolSteps").Append(newestParentSource));
            _messages[parentMessage] = JsonSerializer.SerializeToElement(parentWithNewSource, JsonOptions);
            RenderMessagesNow(); UpdateLayout();
            if (ActiveSubagent?.AgentId != newest || _sourcesSignature == sourceSignature || SourcesPanel.Children.Count != 2
                || !AutomationProperties.GetName((Button)SourcesPanel.Children[0]).StartsWith("Neue Hauptagent-Quelle ·", StringComparison.Ordinal)
                || !_sessionSources[owner].Any(action => action.Id == "overview-parent-source-after-navigation"))
                throw new InvalidOperationException("A parent web action must refresh the latest source while a child tab stays active.");
            OpenSubagentOverview(); UpdateLayout();
            if (ActiveSubagent is not null || Composer.Text != draft)
                throw new InvalidOperationException("Opening the overview from a child must restore the parent composer.");
            var overviewTab = _subagentOverviewTabContainer ?? throw new InvalidOperationException("The overview tab disappeared.");
            var close = ((Grid)overviewTab.Child).Children.OfType<Button>().Single(button => button != _subagentOverviewTabButton);
            if (!close.Focus(FocusState.Keyboard)
                || new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke) is not IInvokeProvider closeInvoke)
                throw new InvalidOperationException("The overview tab needs its own native close control.");
            closeInvoke.Invoke(); UpdateLayout();
            if (_subagentOverviewTabs.Contains(owner) || _subagents.Count(child => child.Value.SessionId == owner) != 4
                || !_subagents[newest].TabOpen || !_running || Composer.Text != draft)
                throw new InvalidOperationException("Closing an overview must preserve child tabs, the parent run and its draft.");
            OpenSubagentOverview();
            var foreign = Guid.NewGuid();
            _session = foreign; SyncSessionTabs(); RenderSubagentOverlay();
            if (_activeSubagentOverviewSession is not null || _subagentOverviewHost.Visibility != Visibility.Collapsed
                || overviewTab.Parent == SessionTabsPanel || SubagentsSection.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("A subagent overview must never leak into another session.");
            _session = owner; SyncSessionTabs(); RenderSubagentOverlay();
            if (overviewTab.Parent != SessionTabsPanel)
                throw new InvalidOperationException("Returning to a session must restore its open overview tab.");
            OpenSubagentOverview();
            if (SubagentOverviewRows().Length != 4) throw new InvalidOperationException("The restored overview lost its children.");
            OpenSourcesTab(); UpdateLayout();
            if (_subagentOverviewHost.Visibility != Visibility.Collapsed || _activeSubagentOverviewSession is not null
                || _sourcesHost.Content is not ScrollViewer { Content: StackPanel sourceBody }
                || _sessionSources[owner].Length < 5 || sourceBody.Children.Count != _sessionSources[owner].Length + 1)
                throw new InvalidOperationException("The complete source view must retain every action and replace the subagent view.");
        }
        finally
        {
            _session = owner; ShowParentConversation(); ShowChatView();
            _subagentOverviewTabs.Remove(owner); _sourceTabs.Remove(owner);
            foreach (var id in addedIds)
                if (_subagents.Remove(id, out var child)) SessionTabsPanel.Children.Remove(child.Container);
            _messages[parentMessage] = parent;
            _sourcesSignature = _subagentOverlaySignature = null;
            Inspector.Visibility = inspectorVisibility;
            SyncSessionTabs(); RenderSubagentOverlay(); RenderSources(_snapshot); RenderMessagesNow(); UpdateLayout();
        }
    }
}
