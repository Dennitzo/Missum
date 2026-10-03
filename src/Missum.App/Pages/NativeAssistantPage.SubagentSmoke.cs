using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
                    || view.Icon.Foreground is not SolidColorBrush actual
                    || SubagentIcon(state.AgentId, 14).Foreground is not SolidColorBrush expected || actual.Color != expected.Color)
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
                || _messageActionViews[childMessage].Panel.Visibility != Visibility.Collapsed)
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
            }
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-subagent-validation.json"),
                JsonSerializer.Serialize(new { renderer = "WinUI3", passed = true, ownerChatMode, parentStable = true, childUsesNativeRenderer = true,
                    overlayAboveSources = true, closableAndReopenable = true, parentDraftPreserved = true,
                    parentRunRemainedActive = true, chatCursorAbsent = true,
                    childModelIdentityPreserved = true,
                    compactLifecycleRow = true, lifecycleStartEntryPreserved = true, lifecycleCompletionPostedOnce = true, lifecycleClickOpensChild = true,
                    lifecycleKeyboardAccessible = true, acceptedManagerStepsCoalesced = true,
                    lifecycleCompletionWaitsForDelivery = true,
                    childSourcesVisibleInParent = true, sourceSessionIsolated = true, liveChildSourcesVisibleInParent,
                    liveLifecycleClickOpensChild = live.ValueKind == JsonValueKind.Object,
                    scienceTabsPreserved = ownerChatMode == "claudescience",
                    liveTranscriptRendered = live.ValueKind == JsonValueKind.Object }));
        }
        finally
        {
            await _settings.UpdateAsync(settings => settings with { SelectedModel = originalSelectedModel });
            ShowParentConversation();
            foreach (var state in _subagents.Values) SessionTabsPanel.Children.Remove(state.Container);
            _subagents.Clear(); foreach (var entry in originalChildren) _subagents[entry.Key] = entry.Value;
            _subagentOverlaySignature = null; Inspector.Visibility = originalInspector;
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            _reasoning = originalReasoning; _reasoningModel = originalReasoningModel; RefreshComposerModelDisplay();
        }
    }
}
