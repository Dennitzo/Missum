using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyDeepResearchSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke) return;
        var originalChildren = _subagents.ToArray();
        var originalInspector = Inspector.Visibility;
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        var owner = Guid.NewGuid();
        var parentId = Guid.NewGuid().ToString();
        var researchId = "research-agent-smoke:" + Guid.NewGuid().ToString("N");
        var otherResearchId = researchId + ":other";
        var nestedResearchId = researchId + ":nested";
        var legacyResearchId = "deep-research:" + Guid.NewGuid().ToString("N");
        var childMessage = Guid.NewGuid().ToString();
        var otherChildMessage = Guid.NewGuid().ToString();
        var nestedChildMessage = Guid.NewGuid().ToString();
        var legacyChildMessage = Guid.NewGuid().ToString();
        var delegatedMessage = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        const string sourceUrl = "https://example.org/research-source";
        try
        {
            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
            snapshot["chatMode"] = JsonSerializer.SerializeToElement("coding");
            snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
            snapshot["runMessageId"] = JsonSerializer.SerializeToElement(parentId);
            snapshot["runId"] = JsonSerializer.SerializeToElement(Guid.NewGuid());
            snapshot["draft"] = JsonSerializer.SerializeToElement("Der Elternentwurf bleibt erhalten.");
            snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
            {
                id = parentId, sessionId = owner, role = "assistant", status = "streaming", content = "Ich prüfe die Quellen.", createdAt = now, updatedAt = now,
                toolSteps = new object[]
                {
                    new { id = "research-one", tool = "web.deepResearch", status = "running", contentOffset = 24 },
                    new { id = "research-one-start", tool = "subagent", status = "running", contentOffset = 24,
                        outputJson = JsonSerializer.Serialize(new { kind = "deepResearch", agentId = researchId, runId = "research-run", originToolStepId = "research-one" }) },
                    new { id = "research-two", tool = "web.deepResearch", status = "completed", contentOffset = 24 },
                    new { id = "research-two-start", tool = "subagent", status = "running", contentOffset = 24,
                        outputJson = JsonSerializer.Serialize(new { kind = "deepResearch", agentId = otherResearchId, runId = "research-run", originToolStepId = "research-two" }) },
                    new { id = "research-legacy", tool = "web.deepResearch", status = "completed", contentOffset = 24 },
                    new { id = "companion", tool = "subagent", status = "completed", contentOffset = 24,
                        outputJson = JsonSerializer.Serialize(new { kind = "deepResearch", agentId = legacyResearchId, runId = "deep-research-run:legacy" }) },
                },
            } }, JsonOptions);
            var real = new { sessionId = owner, parentMessageId = parentId, agentId = "real-agent-smoke", runId = "real-run",
                title = "Echte Delegation", status = "completed", isRunning = false, messages = new object[] { new {
                    id = delegatedMessage, sessionId = owner, role = "assistant", status = "completed", content = "Delegierte Recherche", createdAt = now,
                    toolSteps = new[] { new { id = "research-nested", tool = "web.deepResearch", status = "completed" } },
                } } };
            object Research(string agentId, string stepId, string title, string? originMessageId = null, bool legacy = false) => new
            {
                sessionId = owner, parentMessageId = parentId, originMessageId, agentId, runId = legacy ? "deep-research-run:legacy" : "research-run", kind = "deepResearch", originToolStepId = stepId,
                title, status = "running", isRunning = true, projectionRevision = 1, transcriptRevision = 1, createdAt = now,
                messages = new object[] { new { id = legacy ? legacyChildMessage : stepId == "research-one" ? childMessage : stepId == "research-two" ? otherChildMessage : nestedChildMessage,
                    sessionId = owner, role = "assistant", status = "streaming",
                    content = "Die gefundene Quelle wird verglichen.", createdAt = now, updatedAt = now,
                    toolSteps = new object[]
                    {
                        new { id = "research-reasoning", tool = "assistant.reasoning", status = "completed", detail = "Ich vergleiche die Originalquelle." },
                        new { id = "research-fetch", tool = "web.fetch", status = "completed", inputJson = JsonSerializer.Serialize(new { url = sourceUrl }),
                            outputJson = JsonSerializer.Serialize(new { url = sourceUrl, title = "Originalquelle", content = "Ein echter Quellenauszug aus der Testfixture." }) },
                    } } },
            };
            snapshot["subagents"] = JsonSerializer.SerializeToElement(new[] { (object)real,
                Research(researchId, "research-one", "Erste Recherche"), Research(otherResearchId, "research-two", "Zweite Recherche"),
                Research(nestedResearchId, "research-nested", "Delegierte Recherche", delegatedMessage),
                Research(legacyResearchId, "research-legacy", "Historische Recherche", legacy: true) }, JsonOptions);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); ShowChatView(); RenderMessagesNow(); UpdateLayout();
            if (SessionSubagents().Length != 5 || SubagentsSection.Visibility != Visibility.Visible
                || SubagentsPanel.Children.Count != 1 || _subagentSummaryIcons?.Children.Count != 4
                || _subagentSummaryIcons!.Children.Any(icon => icon is not Missum.App.Controls.NativeSubagentAvatar)
                || Descendants(Inspector).OfType<TextBlock>().Any(label => label.Text == "Deep Research")
                || _messageBlocks[parentId].ContainsKey("tool:companion") || _messageBlocks[parentId].ContainsKey("tool:research-one")
                || _messageBlocks[parentId]["tool:research-one-start"] is not SubagentLifecycleView)
                throw new InvalidOperationException("Deep Research verwendet nicht ausschließlich die gemeinsame Subagentendarstellung mit Planeten.");
            Inspector.Visibility = Visibility.Visible;
            OpenSubagentOverview(); UpdateLayout();
            var overviewResearch = Descendants(_subagentOverviewHost).OfType<Button>()
                .FirstOrDefault(button => button.Tag is string id && id == researchId);
            if (overviewResearch is null)
                throw new InvalidOperationException("Deep Research fehlt in der bestehenden gemeinsamen Subagentenübersicht.");
            if (Descendants(overviewResearch).OfType<Missum.App.Controls.NativeSubagentAvatar>().Count() != 1
                || Descendants(overviewResearch).OfType<TextBlock>().All(label => label.Text != "Erste Recherche"))
                throw new InvalidOperationException("Recherche erhält in der Subagentenübersicht nicht denselben Planeten und Aufgabentitel.");
            await SaveMathPreviewAsync(LayoutRoot, "native-deep-research-overview-preview.png");
            await InvokeOverviewButtonAsync(overviewResearch, researchId);
            ShowParentConversation();
            Inspector.Visibility = Visibility.Visible; UpdateLayout();
            var step = (SubagentLifecycleView)_messageBlocks[parentId]["tool:research-one-start"];
            if (new ButtonAutomationPeer(step).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der Deep-Research-Werkzeugschritt besitzt keine zugängliche Tabnavigation.");
            invoke.Invoke();
            await WaitForPagingSmokeAsync(() => step.NavigationTask is not null, "Deep-Research-Headernavigation wurde nicht aufgerufen.");
            await step.NavigationTask!; RenderMessagesNow(); UpdateLayout();
            var transcriptId = childMessage;
            if (_activeSubagentId != researchId || ActiveSubagent is not { IsDeepResearch: true, IsLegacyDeepResearch: false } || !Composer.IsReadOnly
                || ComposerInteractionHost.IsEnabled || Composer.PlaceholderText != "Aufgabe vom Hauptagenten"
                || _subagents[researchId].Label.Text != "Subagent · Erste Recherche"
                || Descendants(_subagents[researchId].Select).OfType<Missum.App.Controls.NativeSubagentAvatar>().Count() != 1
                || !_messageBlocks[transcriptId].ContainsKey("tool:research-reasoning")
                || _messageBlocks[transcriptId]["tool:research-fetch"] is not ToolStepView
                || !WebSourceActions().Any(action => action.Urls.Contains(sourceUrl, StringComparer.Ordinal))
                || !_messageActionViews[transcriptId].Copy.IsEnabled || !_messageActionViews[transcriptId].Read.IsEnabled)
                throw new InvalidOperationException("Deep Research verwendet nicht die normale schreibgeschützte Transkript-/Werkzeug-/Footeransicht.");
            // Model progress must leave hydrated detail controls and source
            // versions untouched. Both metadata-only and full-envelope updates
            // exercise the same narrow DeepResearch revision contract.
            var activeResearch = _subagents[researchId];
            var retainedMessage = activeResearch.Messages[transcriptId];
            var retainedTool = (ToolStepView)_messageBlocks[transcriptId]["tool:research-fetch"];
            retainedTool.SetExpanded(true);
            var retainedDetail = Descendants(retainedTool).OfType<Missum.App.Controls.NativeToolResultView>().Single();
            var progressSnapshot = activeResearch.Snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            for (var progress = 2; progress <= 3; progress++)
            {
                progressSnapshot["projectionRevision"] = JsonSerializer.SerializeToElement(progress);
                progressSnapshot["generatedTokens"] = JsonSerializer.SerializeToElement(progress * 40);
                progressSnapshot["generationState"] = JsonSerializer.SerializeToElement("tokenProgress");
                progressSnapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(now.AddSeconds(progress));
                progressSnapshot["contextUsed"] = JsonSerializer.SerializeToElement(progress * 1024);
                progressSnapshot["transcriptDeferred"] = JsonSerializer.SerializeToElement(progress == 2);
                progressSnapshot["messages"] = JsonSerializer.SerializeToElement(progress == 2 ? Array.Empty<JsonElement>() : new[] { retainedMessage });
                _messagesDirty = false;
                ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(progressSnapshot));
                if (_messagesDirty || activeResearch.TranscriptDeferred || !activeResearch.Messages[transcriptId].Equals(retainedMessage)
                    || !ReferenceEquals(retainedTool, _messageBlocks[transcriptId]["tool:research-fetch"])
                    || !Descendants(retainedTool).Contains(retainedDetail) || DisplayContextUsed != progress * 1024)
                    throw new InvalidOperationException("Reiner Recherche-Modellfortschritt hat Transkript oder geladene Werkzeugdetails invalidiert.");
            }
            var changedMessage = retainedMessage.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            changedMessage["content"] = JsonSerializer.SerializeToElement("Ein neuer Recherchebefund wurde übernommen.");
            changedMessage["updatedAt"] = JsonSerializer.SerializeToElement(now.AddSeconds(4));
            progressSnapshot["transcriptRevision"] = JsonSerializer.SerializeToElement(2);
            progressSnapshot["projectionRevision"] = JsonSerializer.SerializeToElement(4);
            progressSnapshot["messages"] = JsonSerializer.SerializeToElement(new[] { JsonSerializer.SerializeToElement(changedMessage) });
            _messagesDirty = false;
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(progressSnapshot));
            if (!_messagesDirty || MessageContent(activeResearch.Messages[transcriptId]) != "Ein neuer Recherchebefund wurde übernommen.")
                throw new InvalidOperationException("Eine neue Recherche-Transkriptrevision wurde nicht zur Darstellung übernommen.");
            RenderMessagesNow();
            var sourceMessageCount = _messages.Count;
            await SendAsync();
            if (_messages.Count != sourceMessageCount || !_running)
                throw new InvalidOperationException("Der Recherchetab hat den Elternlauf unerwartet gesteuert.");
            await SaveMathPreviewAsync(LayoutRoot, "native-deep-research-preview.png");
            ShowParentConversation();
            if (Composer.Text != "Der Elternentwurf bleibt erhalten." || Composer.IsReadOnly)
                throw new InvalidOperationException("Die Rechercheansicht hat den Entwurf oder die Eingabefähigkeit des Elternchats verändert.");
            await OpenDeepResearchTabAsync(parentId, "research-two");
            if (_activeSubagentId != otherResearchId)
                throw new InvalidOperationException("Zwei Recherchen derselben Nachricht werden nicht anhand ihrer Werkzeugschritt-ID unterschieden.");
            ShowParentConversation();
            await ActivateSubagentTabAsync(_subagents["real-agent-smoke"]);
            await OpenDeepResearchTabAsync(delegatedMessage, "research-nested");
            if (_activeSubagentId != nestedResearchId)
                throw new InvalidOperationException("Die Recherche eines echten Subagenten wird nicht anhand der ursprünglichen Child-Nachricht geöffnet.");
            ShowParentConversation();
            if (_messageBlocks[parentId]["tool:research-legacy"] is not ToolStepView legacyHeader
                || !Descendants(legacyHeader).OfType<Missum.App.Controls.NativeSubagentAvatar>()
                    .Any(icon => icon.Visibility == Visibility.Visible))
                throw new InvalidOperationException("Der historische Recherche-Werkzeugkopf verwendet keinen Subagenten-Planeten.");
            await OpenDeepResearchTabAsync(parentId, "research-legacy");
            if (_activeSubagentId != legacyResearchId || ActiveSubagent?.IsLegacyDeepResearch != true
                || !Composer.PlaceholderText.Contains("Historische Recherche", StringComparison.Ordinal)
                || Descendants(_subagents[legacyResearchId].Select).OfType<Missum.App.Controls.NativeSubagentAvatar>().Count() != 1)
                throw new InvalidOperationException("Die historische Recherche bleibt nicht mit Planeten lesbar und getrennt von echten Laufsteuerungen.");
            ShowParentConversation();
            var fallback = new ToolStepView { OpenResearch = () => Task.FromResult(false), CanOpenResearch = () => false };
            fallback.Update(JsonSerializer.SerializeToElement(new { id = "legacy", tool = "web.deepResearch", status = "completed", detail = "Historischer Recherchebericht" }));
            fallback.SetExpanded(true);
            if (!fallback.IsExpanded)
                throw new InvalidOperationException("Historische Recherche ohne Projektion besitzt keine normale Detailausweichansicht.");
            // The logical-block path must coalesce the same technical receipt.
            var logical = BuildLogicalBlocks(_messages[parentId], MessageContent(_messages[parentId]), MessageSteps(_messages[parentId]), [], false, []);
            if (logical.Any(block => block.Key is "tool:companion" or "tool:research-one" or "tool:research-two")
                || logical.Count(block => block.Kind == "subagent") != 2
                || logical.Count(block => S(block.Data, "tool") == "web.deepResearch") != 1)
                throw new InvalidOperationException("Die virtuelle Rechercheprojektion enthält doppelte technische Schritte.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-deep-research-validation.json"),
                JsonSerializer.Serialize(new { passed = true, headerOpensMatchingResearch = true, distinctOperations = 3,
                    nestedSubagentOriginResolves = true, historicalDetailsFallback = true, sharedSources = true,
                    tokenOnlySnapshotsRetainHydratedDetails = true, transcriptRevisionStillUpdates = true,
                    sharedSubagentOverview = true, unifiedOutputsSection = true, sharedPlanetIcons = true, sharedTaskTitles = true,
                    realResearchUsesNormalLifecycle = true, legacyResearchRemainsReadable = true,
                    sharedTranscriptAndToolRenderer = true, sharedCopyReadFooter = true, readonlyParentControlsPreserved = true,
                    outputsOrder = "subagents,sources", companionReceiptHiddenInBothRenderers = true,
                    modelRequests = 0, userDataModified = false }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            ShowParentConversation();
            foreach (var child in _subagents.Values) SessionTabsPanel.Children.Remove(child.Container);
            _subagents.Clear(); foreach (var entry in originalChildren) _subagents[entry.Key] = entry.Value;
            ApplyEvent("state.snapshot", original); ShowChatView(); RenderMessagesNow();
            Inspector.Visibility = originalInspector; _selectionInputSmokeActive = previousFixture;
        }
    }
}
