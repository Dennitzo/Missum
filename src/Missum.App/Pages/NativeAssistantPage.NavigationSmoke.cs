using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Runs only in the isolated portable smoke profile, on the real WinUI thread.
    // Returning to A must not reuse a footer still parented to A's discarded bubble.
    private async Task<int> VerifyMessageNavigationSmokeAsync()
    {
        var original = _snapshot.Clone();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var messageA = Guid.NewGuid();
        var messageB = Guid.NewGuid();
        var visits = 0;
        foreach (var (session, message, mode) in new[]
        {
            (sessionA, messageA, "general"), (sessionB, messageB, "coding"),
            (sessionA, messageA, "general"), (sessionB, messageB, "coding"),
            (sessionA, messageA, "general"),
        })
        {
            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(session);
            snapshot["chatMode"] = JsonSerializer.SerializeToElement(mode);
            snapshot["messages"] = JsonSerializer.SerializeToElement(new object[] { new { id = Guid.NewGuid(), sessionId = session, role = "user", content = "Ein einzelner Prompt",
                status = "completed", createdAt = DateTimeOffset.UtcNow.AddSeconds(-1), updatedAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, new
            {
                id = message, sessionId = session, role = "assistant", content = "Navigation smoke",
                status = "completed", createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow,
            } }, JsonOptions);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow();
            var key = message.ToString();
            var bubble = _messageViews[key].View;
            if (_messageActionViews.Count != 2 || _messageActionViews[key].Panel.Parent != MessageBody(bubble))
                throw new InvalidOperationException("Message footer has an invalid visual owner after navigation.");
            if (PromptTimeline.Visibility != Microsoft.UI.Xaml.Visibility.Visible || _promptTimelineMarkers.Count != 1)
                throw new InvalidOperationException("The timeline must display even a single prompt after navigation.");
            var userActions = _messageActionViews.Values.Single(view => !view.IsAssistant);
            if (userActions.Panel.Visibility != Microsoft.UI.Xaml.Visibility.Visible || !userActions.Read.IsEnabled || !userActions.Copy.IsEnabled)
                throw new InvalidOperationException("User messages lack working copy/read footer actions.");
            visits++;
        }
        VerifySharedSidebarSmoke();
        // A multiline Windows prompt must converge to one durable message when its
        // normalized repository content arrives through the native event path.
        var multilinePrompt = "Forschungsfrage\r\n\r\nPrüfe die Feldgleichungen.\rNächster Absatz.";
        var pendingPrompt = AddPendingUserMessage(_session, multilinePrompt);
        var committedPromptId = Guid.NewGuid().ToString();
        ApplyEvent("conversation.messageCommitted", JsonSerializer.SerializeToElement(new
        {
            sessionId = _session,
            message = new { id = committedPromptId, sessionId = _session, role = "user",
                content = Missum.Core.Chat.ChatContentSanitizer.Sanitize(multilinePrompt), status = "completed", createdAt = DateTimeOffset.UtcNow.ToString("O") }
        }));
        RenderMessagesNow();
        if (_pendingUserMessages.ContainsKey(pendingPrompt) || _messages.ContainsKey(pendingPrompt)
            || _messageViews.ContainsKey(pendingPrompt) || !_messageViews.ContainsKey(committedPromptId))
            throw new InvalidOperationException("A multiline research prompt remained duplicated after persistence.");
        _messages.Remove(committedPromptId);
        RenderMessagesNow();
        // Exercise actual native controls for the streaming-to-completed transition.
        var activeId = messageA.ToString();
        var active = _messages[activeId].Deserialize<Dictionary<string, JsonElement>>()!;
        active["status"] = JsonSerializer.SerializeToElement("streaming");
        active["content"] = JsonSerializer.SerializeToElement("Ein gestreamter Absatz");
        var activeMessage = JsonSerializer.SerializeToElement(active);
        var body = MessageBody(_messageViews[activeId].View);
        UpdateMessageBlocks(activeId, activeMessage, body);
        UpdateMessageActions(activeId, activeMessage);
        if (_messageActionViews[activeId].Panel.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
            throw new InvalidOperationException("The streaming message footer must remain hidden until completion.");
        var header = _messageBlocks[activeId]["header"];
        UpdateMessageHeader(header, activeMessage);
        var label = (TextBlock)((StackPanel)header).Children[0];
        if (!label.Text.StartsWith("In Bearbeitung seit ", StringComparison.Ordinal) || label.Text.Contains("Token", StringComparison.Ordinal))
            throw new InvalidOperationException("The active header must display elapsed time without token counts or status prefixes.");
        var stable = label.Text;
        active["content"] = JsonSerializer.SerializeToElement("Ein gestreamter Absatz mit weiteren Wörtern");
        UpdateMessageBlocks(activeId, JsonSerializer.SerializeToElement(active), body);
        if (label.Text != stable) throw new InvalidOperationException("A text delta reset the generation header.");
        AssertChatCursorAbsent(body);
        active["status"] = JsonSerializer.SerializeToElement("completed");
        UpdateMessageBlocks(activeId, JsonSerializer.SerializeToElement(active), body);
        UpdateMessageActions(activeId, JsonSerializer.SerializeToElement(active));
        if (_messageActionViews[activeId].Panel.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
            throw new InvalidOperationException("Completion did not restore the message footer actions.");
        AssertChatCursorAbsent(body);
        if (!label.Text.Contains("Uhr ·", StringComparison.Ordinal) || !label.Text.EndsWith("lang gearbeitet", StringComparison.Ordinal))
            throw new InvalidOperationException("Completion did not finalize the header.");
        var readInput = JsonSerializer.SerializeToElement(new { path = "src/example.cs", startLine = 10 });
        var readOutput = JsonSerializer.SerializeToElement(new { path = "src/example.cs", totalLines = 500, startLine = 10, content = "10: var x = 1;\n11: return x;\n", truncated = true });
        var summary = ToolStepView.ToolSummary("coding.read", readInput, readOutput);
        if (!summary.Contains("example.cs", StringComparison.Ordinal) || !summary.Contains("2 Zeilen gelesen (10–11)", StringComparison.Ordinal) || summary.Contains("500 Zeilen gelesen", StringComparison.Ordinal))
            throw new InvalidOperationException("A partial read must report returned lines, not the total file size.");
        foreach (var expandByDefault in new[] { false, true })
        foreach (var tool in new[] { "coding.read", "assistant.reasoning", "assistant.progress" })
        {
            var stepView = new ToolStepView(expandByDefault);
            var step = JsonSerializer.SerializeToElement(new { tool, status = "running", detail = "Erster Abschnitt" });
            stepView.Update(step);
            if (stepView.IsExpanded != expandByDefault)
                throw new InvalidOperationException("The expansion preference must apply to every step, including reasoning.");
            stepView.SetExpanded(!expandByDefault);
            stepView.Update(JsonSerializer.SerializeToElement(new { tool, status = "completed", detail = "Vollständiger Abschnitt" }));
            if (stepView.IsExpanded == expandByDefault)
                throw new InvalidOperationException("A step update must preserve the user's manual disclosure choice.");
        }
        var receipt = new ToolStepView();
        body.Children.Add(receipt);
        var receiptData = JsonSerializer.SerializeToElement(new { tool = "coding.read", status = "completed", inputJson = readInput.GetRawText(), outputJson = readOutput.GetRawText() });
        receipt.Update(receiptData, 87);
        receipt.SetExpanded(true);
        receipt.Measure(new Windows.Foundation.Size(700, double.PositiveInfinity));
        if (receipt.DesiredSize.Height < 100 || receipt.DesiredSize.Width > 700)
            throw new InvalidOperationException("The expanded receipt does not fit the available chat width.");
        var expandedHeight = receipt.DesiredSize.Height;
        receipt.SetExpanded(false);
        receipt.Measure(new Windows.Foundation.Size(700, double.PositiveInfinity));
        if (receipt.DesiredSize.Height >= expandedHeight)
            throw new InvalidOperationException("Collapsing a receipt did not release its details.");
        receipt.SetExpanded(true);
        receipt.Update(receiptData, 88);
        receipt.Measure(new Windows.Foundation.Size(700, double.PositiveInfinity));
        if (receipt.DesiredSize.Height < expandedHeight)
            throw new InvalidOperationException("Updating a receipt lost the disclosure state.");
        if (ConversationScroll.Padding.Left != ConversationScroll.Padding.Right)
            throw new InvalidOperationException("Conversation margins are not symmetrical.");
        var python = "import math\n# Fachliche Berechnung\nvalue = math.sqrt(4)\nprint(value)";
        var scienceReceipt = new ToolStepView(true);
        body.Children.Add(scienceReceipt);
        scienceReceipt.Update(JsonSerializer.SerializeToElement(new { tool = "research.code.write", status = "completed",
            inputJson = JsonSerializer.Serialize(new { projectId = "research-smoke", path = "calculation.py", content = python }),
            outputJson = JsonSerializer.Serialize(new { success = true, file = "calculation.py" }) }));
        scienceReceipt.Measure(new Windows.Foundation.Size(700, double.PositiveInfinity));
        scienceReceipt.UpdateLayout();
        var pythonBlock = Descendants(scienceReceipt).OfType<TextBlock>().SingleOrDefault(text =>
            string.Concat(text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(run => run.Text)) == python);
        if (pythonBlock is null || pythonBlock.FontFamily.Source != "Cascadia Mono" || pythonBlock.Inlines.Count < 4
            || Descendants(scienceReceipt).OfType<Missum.App.Controls.NativeToolResultView>().Count() != 2)
            throw new InvalidOperationException("Python research input must use the shared native code card and syntax colors.");
        await SaveMathPreviewAsync(scienceReceipt, "native-python-receipt-preview.png");
        scienceReceipt.SetExpanded(false);
        scienceReceipt.Measure(new Windows.Foundation.Size(700, double.PositiveInfinity));
        if (scienceReceipt.DesiredSize.Height > 80)
            throw new InvalidOperationException("A collapsed scientific code receipt is not compact.");
        body.Children.Remove(scienceReceipt);
        await VerifyConversationSelectionSmokeAsync();
        await VerifyComposerFooterSmokeAsync();
        await VerifyMathRenderingSmokeAsync(body);
        await VerifyMarkdownTableSmokeAsync(body);
        await VerifyMarkdownHeadingSmokeAsync(original);
        await VerifyLooseMathSmokeAsync(original);
        await VerifyAnswerStreamingSmokeAsync(original);
        await VerifyThinkingIndicatorSmokeAsync(original);
        await VerifyToolIconColorsSmokeAsync();
        await VerifyContinuationSmokeAsync(original);
        await VerifySourcesSmokeAsync(original);
        await VerifyChangesReviewSmokeAsync();
        await VerifyScienceViewsSmokeAsync();
        await VerifySubagentSmokeAsync(original);
        await VerifyPlanetPaletteSmokeAsync();
        ApplyEvent("state.snapshot", original);
        RenderMessagesNow();
        return visits;
    }

    private void VerifySharedSidebarSmoke()
    {
        var owner = _session;
        var beforeMessages = _messages.Keys.Order().ToArray();
        var previousDraft = Composer.Text;
        _rendering = true;
        Composer.Text = "Dieser Entwurf bleibt auf dem PC.";
        _rendering = false;
        var group = Guid.NewGuid();
        ApplyEvent("session.grouped", JsonSerializer.SerializeToElement(new
        {
            chatMode = _mode,
            sessions = new[] { new { id = owner, title = "Gemeinsam bearbeiteter Chat", chatMode = _mode } },
            sessionGroups = new[] { new { id = group, name = "Browserprojekt", workspacePath = "", isCollapsed = false,
                chatMode = _mode, sessionIds = new[] { owner } } },
            activeSessionId = Guid.NewGuid(), draft = "Dieser fremde Entwurf darf nicht übernommen werden.",
        }));
        ProjectsPanel.Measure(new Windows.Foundation.Size(288, double.PositiveInfinity));
        ProjectsPanel.UpdateLayout();
        var sidebarLabels = Descendants(ProjectsPanel).OfType<TextBlock>().Select(text => text.Text).ToArray();
        if (_session != owner || Composer.Text != "Dieser Entwurf bleibt auf dem PC."
            || !beforeMessages.SequenceEqual(_messages.Keys.Order())
            || !sidebarLabels.Contains("Browserprojekt") || !sidebarLabels.Contains("Gemeinsam bearbeiteter Chat"))
            throw new InvalidOperationException("A shared sidebar update must render the browser project without replacing the native conversation or draft. "
                + JsonSerializer.Serialize(new { ownerPreserved = _session == owner, draft = Composer.Text,
                    messagesPreserved = beforeMessages.SequenceEqual(_messages.Keys.Order()), sidebarLabels }));
        ApplyEvent("session.grouped", JsonSerializer.SerializeToElement(new { chatMode = "coding",
            sessions = Array.Empty<object>(), sessionGroups = Array.Empty<object>() }));
        if (!Descendants(ProjectsPanel).OfType<TextBlock>().Any(text => text.Text == "Browserprojekt"))
            throw new InvalidOperationException("Sidebar metadata from another chat mode replaced the native sidebar.");
        _rendering = true;
        Composer.Text = previousDraft;
        _rendering = false;
    }

    private async Task VerifySourcesSmokeAsync(JsonElement original)
    {
        var owner = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var webFixture = Environment.GetEnvironmentVariable("MISSUM_SOURCE_SMOKE_JSON_PATH");
        var actualWebResult = !string.IsNullOrWhiteSpace(webFixture) && File.Exists(webFixture) ? await File.ReadAllTextAsync(webFixture) : null;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("general");
        snapshot["documents"] = snapshot["attachments"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new { id = Guid.NewGuid(), sessionId = owner, role = "assistant", status = "streaming", content = "",
            createdAt = now, updatedAt = now, toolSteps = Enumerable.Range(0, 4).Select(index => new { id = "search-" + index, tool = "web.search", status = "completed",
                inputJson = JsonSerializer.Serialize(new { query = "Recherche " + index }),
                outputJson = actualWebResult ?? JsonSerializer.Serialize(new { results = new[] { new { title = "Wissenschaftliche Quelle " + index,
                    url = "https://example.org/source-" + index, thumbnailUrl = "https://example.org/preview.png" } } }),
                updatedAt = now.AddSeconds(index) }).ToArray() } });
        ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); RenderMessagesNow();
        if (SourcesPanel.Children.Count != 2 || _sessionSources[owner].Length != 4 || !_sessionSources[owner][0].Title.EndsWith('3')
            || !_sessionSources[owner].Select(action => action.Id).SequenceEqual(Enumerable.Range(0, 4).Reverse().Select(index => "search-" + index)))
            throw new InvalidOperationException("Sources must show only the latest web action and an all-sources action, while retaining the complete ordered history.");
        var latestSource = _sessionSources[owner][0];
        // Inspect our labels rather than the FontIcon's internal glyph TextBlock.
        var sourceLabels = ((Grid)((Button)SourcesPanel.Children[0]).Content).Children.OfType<StackPanel>().Single()
            .Children.OfType<TextBlock>().ToArray();
        if (sourceLabels.Length != 2 || sourceLabels[0].Text != SourcePageTitle(latestSource, latestSource.Urls[0])
            || sourceLabels[1].Text != latestSource.Urls[0] || sourceLabels[1].Opacity >= sourceLabels[0].Opacity
            || sourceLabels[1].FontSize >= sourceLabels[0].FontSize)
            throw new InvalidOperationException("Web sources must display the page title above a quieter, smaller URL.");
        var allSources = (Button)SourcesPanel.Children[^1];
        var allRow = (Grid)allSources.Content;
        var allIcon = allRow.Children.OfType<FontIcon>().Single();
        if (allRow.Children.OfType<TextBlock>().Single().Opacity >= 1 || allIcon.Glyph != "\uE71B" || allIcon.Opacity >= 1
            || Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(allSources) != "Alle Quellen anzeigen")
            throw new InvalidOperationException("The all-sources action must use the muted link symbol.");
        VerifyIconBrush(allIcon.Foreground, "link");
        VerifyIconBrush(((Grid)((Button)SourcesPanel.Children[0]).Content).Children.OfType<FontIcon>().Single().Foreground, "web");
        VerifyIconBrush(((Grid)InspectorProjectRow.Content).Children.OfType<FontIcon>().Single().Foreground, "folder");
        VerifyIconBrush(((Grid)ChangesSummaryButton.Content).Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Single().Stroke, "code");
        VerifyIconBrush(DictationIcon.Foreground, "speech");
        var parsedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsedTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectSourceUrls(JsonSerializer.Serialize(new { result = JsonSerializer.Serialize(new { results = new[] {
            new { title = "Feldgleichungen", url = "https://example.org/field", thumbnailUrl = "https://example.org/cover.png" } } }) }), parsedUrls, parsedTitles);
        CollectSourceUrls(JsonSerializer.Serialize(new { url = "https://example.org/field" }), parsedUrls, parsedTitles);
        if (parsedUrls.Count != 1 || parsedTitles["https://example.org/field"] != "Feldgleichungen")
            throw new InvalidOperationException("Source metadata must preserve webpage titles and exclude thumbnail URLs.");
        var redirected = ResolveSourceTitles([
            new("fetch", "Webseite", ["https://example.org/final", "https://example.org/field"], now),
            new("search", "Websuche", ["https://example.org/field"], now.AddSeconds(-1), PageTitles: parsedTitles)
        ], []);
        if (SourcePageTitle(redirected[0], "https://example.org/final") != "Feldgleichungen")
            throw new InvalidOperationException("A webpage fetch must retain a title discovered by the same session's search.");
        var inspectorVisibility = Inspector.Visibility;
        Inspector.Visibility = Microsoft.UI.Xaml.Visibility.Visible; UpdateLayout();
        await SaveMathPreviewAsync(Inspector, "native-outputs-preview.png");
        await SaveMathPreviewAsync(LayoutRoot, "native-colored-chrome-preview.png");
        Inspector.Visibility = inspectorVisibility;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            OpenSourcesTab(); UpdateLayout(); await Task.Delay(30);
            if (_sourcesHost.Visibility != Microsoft.UI.Xaml.Visibility.Visible
                || _sourcesHost.Content is not ScrollViewer { Content: StackPanel sourceBody }
                || sourceBody.Children.Count != _sessionSources[owner].Length + 1
                || !sourceBody.Children.OfType<StackPanel>().Select(group => group.Children.OfType<TextBlock>().Single().Text)
                    .SequenceEqual(_sessionSources[owner].Select(action => action.Title)))
                throw new InvalidOperationException("The sources tab must open the complete ordered source history, independently of the single overlay entry.");
            ShowChatView(); UpdateLayout(); await Task.Delay(30);
            AssertChatCursorAbsent(ConversationContent);
            if (_messages.Values.All(message => S(message, "status") != "streaming")
                || _messageBlocks.Values.Any(blocks => blocks.Values.OfType<Missum.App.Controls.NativeStreamingMarkdown>()
                    .Any(markdown => markdown.Children.Count == 0)))
                throw new InvalidOperationException("Tab navigation lost the active run or introduced an empty streaming placeholder.");
        }
        ApplyEvent("state.snapshot", original);
        if (_activeSourcesSession is not null || _sourcesHost.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed || _sourcesTabContainer?.Parent == SessionTabsPanel)
            throw new InvalidOperationException("Sources leaked across sessions.");
    }

    private static void VerifyIconBrush(Microsoft.UI.Xaml.Media.Brush brush, string key)
    {
        if (brush is not Microsoft.UI.Xaml.Media.SolidColorBrush colorBrush
            || colorBrush.Color != Missum.App.Controls.NativeIconPalette.ColorFor(key))
            throw new InvalidOperationException("A native functional icon lost its shared semantic color: " + key);
    }

    private async Task VerifyToolIconColorsSmokeAsync()
    {
        var preview = new StackPanel { Spacing = 6, Padding = new Microsoft.UI.Xaml.Thickness(16), MaxWidth = 740,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 25, 25)) };
        MessagesPanel.Children.Add(preview);
        try
        {
            foreach (var (tool, key) in new[] {
                ("web.search", "web"), ("coding.read", "code"), ("coding.updatePlan", "plan"),
                ("research.code.execute", "code"), ("research.deep", "research"), ("math.formalProof", "research"),
                ("math.smt", "research"), ("assistant.reasoning", "research"), ("assistant.progress", "research"),
                ("document.create", "document"), ("image.generate", "image"), ("speech.synthesize", "speech"),
                ("audiobook.generate", "audiobook"), ("captions.stream", "captions"), ("extension.custom", "tool") })
            {
                var step = new ToolStepView(); preview.Children.Add(step);
                foreach (var status in new[] { "running", "completed" })
                foreach (var expanded in new[] { false, true })
                {
                    step.Update(JsonSerializer.SerializeToElement(new { tool, status, detail = "Lesbarer Werkzeugschritt" }));
                    step.SetExpanded(expanded);
                    // Button templates may not be loaded while an offscreen step
                    // is being prepared; inspect its actual content icon directly.
                    var stepBody = (StackPanel)step.Children[0];
                    var icon = ((Grid)((Button)stepBody.Children[0]).Content).Children.OfType<FontIcon>().First();
                    var expected = ToolGlyphColor(key, tool);
                    if (icon.Foreground is not Microsoft.UI.Xaml.Media.SolidColorBrush brush || brush.Color != expected
                        || brush.Color.A != 255 || Math.Max(brush.Color.R, Math.Max(brush.Color.G, brush.Color.B))
                            - Math.Min(brush.Color.R, Math.Min(brush.Color.G, brush.Color.B)) < 30)
                        throw new InvalidOperationException("A tool-step icon lost its menu color during status or disclosure changes: " + tool);
                }
                step.SetExpanded(false);
            }
            await SaveMathPreviewAsync(preview, "native-tool-icons-preview.png");
        }
        finally { MessagesPanel.Children.Remove(preview); }
    }

    private async Task VerifyChangesReviewSmokeAsync()
    {
        var original = _snapshot.Clone();
        var originalSummary = _currentChangesSummary.ValueKind == JsonValueKind.Object ? _currentChangesSummary.Clone() : default;
        var previousReview = _reviewTabs.FirstOrDefault(tab => tab.RunId == _activeReviewRunId);
        var previousSources = _activeSourcesSession;
        var previousResearch = _activeResearchSessionId;
        var previousSimulation = _simulationView;
        var owner = Guid.NewGuid(); var message = Guid.NewGuid(); var run = Guid.NewGuid();
        var longLine = "var payload = \"" + new string('x', 320) + "\";";
        var firstDiff = "diff --git a/first.cs b/first.cs\n--- /dev/null\n+++ b/first.cs\n@@ -0,0 +1,81 @@\n+" + longLine + "\n"
            + string.Join("\n", Enumerable.Range(1, 80).Select(index => $"+var value{index} = {index};"));
        var summary = JsonSerializer.SerializeToElement(new { sessionId = owner, messageId = message, runId = run, revision = 1,
            workspacePath = "C:\\Missum-Smoke", files = new[]
            {
                new { path = "first.cs", addedLines = 81, removedLines = 0, isBinary = false, diff = firstDiff },
                new { path = "second.cs", addedLines = 1, removedLines = 0, isBinary = false,
                    diff = "diff --git a/second.cs b/second.cs\n--- /dev/null\n+++ b/second.cs\n@@ -0,0 +1 @@\n+return 42;" },
            } });
        try
        {
            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
            snapshot["chatMode"] = JsonSerializer.SerializeToElement("coding");
            snapshot["conversationRevision"] = JsonSerializer.SerializeToElement(1);
            snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new { id = message, sessionId = owner,
                role = "assistant", status = "completed", content = "Die beiden Dateien wurden geändert.",
                createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow } });
            snapshot.Remove("changesSummary");
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); RenderMessagesNow();
            var chatChildren = MessagesPanel.Children.ToArray();
            RenderChanges(summary); UpdateLayout();
            if (_currentChangesSummary.ValueKind != JsonValueKind.Object || S(_currentChangesSummary, "runId") != run.ToString()
                || !ChangesSummaryButton.IsEnabled || !chatChildren.SequenceEqual(MessagesPanel.Children)
                || Descendants(Inspector).OfType<TextBlock>().Any(text => text.Text.Contains("first.cs", StringComparison.Ordinal)
                    || text.Text.Contains("second.cs", StringComparison.Ordinal)))
                throw new InvalidOperationException("A file receipt must only update the overlay summary, without file lists in chat or outputs.");

            OpenChangesReview(summary); UpdateLayout(); await Task.Delay(30);
            var diffs = Descendants(ReviewChangesPanel).OfType<Missum.App.Controls.NativeDiffView>().ToArray();
            if (diffs.Length != 2 || ReviewScroll.ScrollableWidth > .5 || ReviewScroll.ScrollableHeight <= 0
                || diffs.Any(diff => Descendants(diff).OfType<ScrollViewer>().Any()
                    || diff.ActualWidth > ReviewChangesPanel.ActualWidth + .5))
                throw new InvalidOperationException("The changes tab must have one vertical scroll area and no nested or horizontal diff scrolling.");
            var firstRows = diffs[0].Content as StackPanel
                ?? throw new InvalidOperationException("The review diff is not in its inline layout.");
            var code = firstRows.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<TextBlock>())
                .Where(text => Grid.GetColumn(text) == 3).ToArray();
            if (code.Length != 81 || code.Any(text => text.TextWrapping != Microsoft.UI.Xaml.TextWrapping.Wrap)
                || code[0].ActualHeight <= code[0].LineHeight * 1.5
                || firstRows.Children.OfType<Microsoft.UI.Xaml.Controls.Border>()
                    .Any(row => row.Child is TextBlock { TextWrapping: Microsoft.UI.Xaml.TextWrapping.NoWrap }))
                throw new InvalidOperationException("Long source and metadata lines must wrap without losing line numbers or source rows.");
            await SaveMathPreviewAsync(ReviewScroll, "native-changes-preview.png");
            ReviewScroll.ChangeView(null, ReviewScroll.ScrollableHeight, null, true);
            UpdateLayout(); await Task.Delay(30);
            var second = ReviewChangesPanel.Children.Last() as Microsoft.UI.Xaml.FrameworkElement
                ?? throw new InvalidOperationException("The second changed file is missing.");
            var bottom = second.TransformToVisual(ReviewScroll).TransformPoint(new Windows.Foundation.Point()).Y + second.ActualHeight;
            if (ReviewScroll.VerticalOffset <= 0 || bottom > ReviewScroll.ActualHeight + .5)
                throw new InvalidOperationException("The outer changes scroll area cannot reach the second file.");
        }
        finally
        {
            ShowChatView();
            var fixtureTab = _reviewTabs.FirstOrDefault(tab => tab.RunId == run);
            if (fixtureTab is not null) { _reviewTabs.Remove(fixtureTab); SessionTabsPanel.Children.Remove(fixtureTab.Container); }
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            if (originalSummary.ValueKind == JsonValueKind.Object) RenderChanges(originalSummary);
            if (previousReview is not null && previousReview.SessionId == _session) ShowReviewTab(previousReview);
            else if (previousSources == _session) OpenSourcesTab();
            else if (previousResearch == _session) OpenResearchView(previousSimulation);
            RenderSessionTabs();
        }

    }

    private static IEnumerable<Microsoft.UI.Xaml.DependencyObject> Descendants(Microsoft.UI.Xaml.DependencyObject root)
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private async Task VerifyScienceViewsSmokeAsync()
    {
        var previousMode = _mode;
        try
        {
            _mode = "claudescience";
            RenderSessionTabs();
            if (_researchTabButton?.Parent != SessionTabsPanel || _simulationTabButton?.Parent != SessionTabsPanel)
                throw new InvalidOperationException("Claude Science must expose publication and simulation tabs for its active session.");
            _activeResearchSessionId = _session;
            ResearchHost.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            _simulationView = false; RenderResearchView();
            if (ResearchHost.Content is not Grid) throw new InvalidOperationException("Publication view was not created.");
            _simulationView = true; RenderResearchView();
            if (ResearchHost.Content is not Grid simulationRoot || simulationRoot.RowDefinitions.Count != 3
                || simulationRoot.Children[0] is not Grid simulationHeader
                || !simulationHeader.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>())
                    .Any(text => text.Text == "Simulation"))
                throw new InvalidOperationException("Simulation view must share the publication header and content layout.");
            foreach (var (projectSelected, loading, heading) in new[]
            {
                (false, false, "Stelle im Chat eine Forschungsfrage."),
                (true, false, "Noch keine Simulation vorhanden."),
                (true, true, "Simulationen werden geladen."),
            })
            {
                if (BuildSimulationView(null, projectSelected, loading) is not StackPanel empty)
                    throw new InvalidOperationException("Simulation needs the same informative empty state as Publication.");
                var texts = empty.Children.OfType<TextBlock>().Select(text => text.Text).ToArray();
                if (texts.Length != 2 || texts[0] != heading || !texts[1].Contains("Python", StringComparison.Ordinal)
                    || !texts[1].Contains("Daten", StringComparison.Ordinal) || empty.Children.OfType<Image>().Any())
                    throw new InvalidOperationException("Simulation needs an informative empty/loading state without synthetic evidence images.");
            }
            await SaveMathPreviewAsync(ResearchHost, "native-simulation-empty-preview.png");
            var loadingAlreadyActive = _researchLoading.Contains(_session);
            try
            {
                _researchLoading.Add(_session); RenderResearchView();
                if (!ReferenceEquals(ResearchHost.Content, simulationRoot))
                    throw new InvalidOperationException("Background polling rebuilt the simulation view without a content change.");
                _researchLoading.Remove(_session); RenderResearchView();
                if (!ReferenceEquals(ResearchHost.Content, simulationRoot))
                    throw new InvalidOperationException("Finishing a background poll rebuilt the simulation view without a content change.");
            }
            finally { if (loadingAlreadyActive) _researchLoading.Add(_session); }
            var scienceState = ResearchState(_session);
            var previousError = scienceState.Error;
            try
            {
                scienceState.Error = "Simulation-Smoke: Darstellung konnte nicht geladen werden.";
                RenderResearchView();
                if (ReferenceEquals(ResearchHost.Content, simulationRoot) || ResearchHost.Content is not Grid errorRoot
                    || errorRoot.Children[1] is not StackPanel errorDescription
                    || !errorDescription.Children.OfType<InfoBar>().Any(bar => bar.Message == scienceState.Error))
                    throw new InvalidOperationException("Simulation errors must update the view even when its artifacts have not changed.");
            }
            finally { scienceState.Error = previousError; RenderResearchView(); }
            var fixture = Environment.GetEnvironmentVariable("MISSUM_SCIENCE_PDF_SMOKE_PATH");
            if (!string.IsNullOrWhiteSpace(fixture) && File.Exists(fixture))
            {
                var pdf = new Missum.App.Controls.NativePublicationView();
                ResearchHost.Content = pdf;
                await pdf.LoadAsync(fixture);
                if (pdf.PageCount == 0 || !pdf.HasRenderedPage) throw new InvalidOperationException("Native scientific PDF preview failed.");
                if (pdf.PageVisualCount != pdf.PageCount || Descendants(pdf).OfType<Button>().Any())
                    throw new InvalidOperationException("Native PDF must contain every page in one continuous view without page buttons.");
                UpdateLayout(); await SaveMathPreviewAsync(pdf, "native-publication-preview.png");
                await pdf.ScrollToEndAsync();
                if (!pdf.IsLastPageRendered) throw new InvalidOperationException("Scrolling through the native PDF did not render its final page: " + pdf.ScrollDiagnostics);
                await SaveMathPreviewAsync(pdf, "native-publication-last-page-preview.png");
                // Exercise real loaded/unloaded PDF visuals, not only an offscreen measurement.
                for (var iteration = 0; iteration < 12; iteration++)
                {
                    ResearchHost.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; BodyGrid.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    UpdateLayout(); await Task.Delay(30);
                    BodyGrid.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; ResearchHost.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                    UpdateLayout(); await Task.Delay(30);
                    if (!pdf.HasRenderedPage) throw new InvalidOperationException("Tab switching lost the rendered publication.");
                }
                ResearchHost.Content = null;
                _publicationViews[_session] = pdf;
                for (var iteration = 0; iteration < 6; iteration++)
                {
                    var container = new Grid(); container.Children.Add(pdf); ResearchHost.Content = container;
                    UpdateLayout(); await Task.Delay(30);
                    ResearchHost.Content = null; container.Children.Remove(pdf);
                    _simulationView = true; _scienceViewSignature = null; RenderResearchView();
                    UpdateLayout(); await Task.Delay(30);
                }
            }
        }
        finally
        {
            _mode = previousMode; ShowChatView(); RenderSessionTabs();
        }
        if (_simulationTabButton?.Parent == SessionTabsPanel) throw new InvalidOperationException("Science tabs leaked into another mode.");
    }

    private async Task VerifyConversationSelectionSmokeAsync()
    {
        var fixture = new StackPanel { Spacing = 18, Padding = new Microsoft.UI.Xaml.Thickness(12), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) };
        var firstRun = new Microsoft.UI.Xaml.Documents.Run { Text = "Alpha erste Antwort" };
        var first = new TextBlock { IsTextSelectionEnabled = true, FontSize = 16, Inlines = { firstRun } };
        var middle = new RichTextBlock { IsTextSelectionEnabled = true, FontSize = 16 };
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Bold { Inlines = { new Microsoft.UI.Xaml.Documents.Run { Text = "Beta formatierter Absatz" } } });
        middle.Blocks.Add(paragraph);
        var code = new TextBlock { Text = "var value = 42;", IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"), FontSize = 16 };
        var lastRun = new Microsoft.UI.Xaml.Documents.Run { Text = "Gamma zweite Antwort" };
        var last = new TextBlock { IsTextSelectionEnabled = true, FontSize = 16, Inlines = { lastRun } };
        fixture.Children.Add(first); fixture.Children.Add(middle); fixture.Children.Add(code); fixture.Children.Add(last);
        MessagesPanel.Children.Add(fixture);
        ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
        fixture.UpdateLayout();
        try
        {
            var start = firstRun.ContentStart.Offset + 2;
            var end = lastRun.ContentStart.Offset + 3;
            _conversationSelection!.SelectForSmoke(first, start, last, end);
            var suffix = Missum.App.Controls.NativeConversationSelection.ReadableSuffix([fixture], middle);
            if (suffix != "Beta formatierter Absatz\n\nvar value = 42;\n\nGamma zweite Antwort")
                throw new InvalidOperationException("Read from here must begin at the paragraph start and include following paragraphs: " + suffix);
            var expected = "pha erste Antwort\n\nBeta formatierter Absatz\n\nvar value = 42;\n\nGam";
            if (_conversationSelection.SelectedText != expected)
                throw new InvalidOperationException("Cross-answer selection text differs: " + _conversationSelection.SelectedText);
            if (first.TextHighlighters.Count == 0 || middle.TextHighlighters.Count == 0 || last.TextHighlighters.Count == 0)
                throw new InvalidOperationException("Cross-answer selection is not highlighted in every text container.");
            await SaveMathPreviewAsync(fixture, "native-selection-preview.png");
            _conversationSelection.SelectForSmoke(last, end, first, start);
            if (_conversationSelection.SelectedText != expected)
                throw new InvalidOperationException("Reverse selection has a different reading order.");
            _conversationSelection.Clear();
            if (_conversationSelection.HasSelection || first.TextHighlighters.Count != 0 || middle.TextHighlighters.Count != 0)
                throw new InvalidOperationException("Selection reset left text or highlights behind.");
        }
        finally { _conversationSelection?.Clear(); MessagesPanel.Children.Remove(fixture); }
    }

    private async Task VerifyComposerFooterSmokeAsync()
    {
        var originalWidth = ComposerSurface.Width;
        var originalStatus = ChatStatus;
        var originalSession = _session;
        var originalRunning = _running;
        try
        {
            _running = false;
            ComposerSurface.Width = 600;
            SelectedToolChip.SetTool("Planen", "\uEA80", Missum.App.Controls.NativeIconPalette.ColorFor("plan"));
            SelectedToolChip.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            Composer.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            SelectedToolChip.SetPointerState(false);
            ComposerSurface.UpdateLayout();
            var width = SelectedToolChip.ActualWidth;
            VerifyChipColor(SelectedToolChip, "plan");
            if (width < 50 || SelectedToolChip.ActualHeight != 28 || ComposerChipsScroll.ActualWidth <= 0)
                throw new InvalidOperationException("The footer tool chip has an invalid layout.");
            await SaveMathPreviewAsync(ComposerSurface, "native-composer-preview.png");
            SelectedToolChip.SetPointerState(true);
            await Task.Delay(150);
            ComposerSurface.UpdateLayout();
            if (!SelectedToolChip.IsRemoveAffordanceVisible || SelectedToolChip.ActualWidth != width)
                throw new InvalidOperationException("Hover changed the chip width or did not expose removal.");
            VerifyChipColor(SelectedToolChip, "plan");
            await SaveMathPreviewAsync(ComposerSurface, "native-composer-hover-preview.png");
            SelectedToolChip.SetPointerState(false);
            // The portable smoke window is hidden, so exercise the same focus-state handler directly.
            SelectedToolChip.SetKeyboardFocusState(true);
            if (!SelectedToolChip.IsRemoveAffordanceVisible)
                throw new InvalidOperationException("Keyboard focus did not expose chip removal.");
            Composer.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            SelectedToolChip.SetKeyboardFocusState(false);
            SelectedToolChip.SetTool("Ein sehr langer Werkzeugname für die Platzprüfung", "\uE8A5", Missum.App.Controls.NativeIconPalette.ColorFor("document"));
            CaptionChip.SetTool("Live-Untertitel", "\uE7F4", Missum.App.Controls.NativeIconPalette.ColorFor("speech"));
            VerifyChipColor(SelectedToolChip, "document");
            VerifyChipColor(CaptionChip, "speech");
            CaptionChip.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            ComposerSurface.Width = 460;
            ComposerSurface.UpdateLayout();
            if (ComposerChipsScroll.ActualWidth <= 0 || ComposerChipsScroll.ScrollableWidth <= 0)
                throw new InvalidOperationException("Multiple footer chips cannot scroll at narrow widths.");
            var send = SendButton.TransformToVisual(ComposerSurface).TransformPoint(new Windows.Foundation.Point());
            if (send.X + SendButton.ActualWidth > ComposerSurface.ActualWidth)
                throw new InvalidOperationException("Footer chips pushed the send button out of the composer.");
            await SaveMathPreviewAsync(ComposerSurface, "native-composer-narrow-preview.png");
            ChatStatus = "Export wird vorbereitet";
            ChatStatus = "Export abgeschlossen";
            _chatErrors[_session] = "Prüfhinweis"; RefreshChatNotices();
            if (StatusText.Text != "Export abgeschlossen" || !ErrorBar.IsOpen)
                throw new InvalidOperationException("Chat notices did not update in place.");
            _session = Guid.NewGuid(); RefreshChatNotices();
            if (ErrorBar.IsOpen || StatusText.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
                throw new InvalidOperationException("Chat notices leaked into another session.");
            _session = originalSession; RefreshChatNotices();
            if (!ErrorBar.IsOpen) throw new InvalidOperationException("The session error was lost on navigation.");
        }
        finally
        {
            _session = originalSession; _running = originalRunning;
            _chatErrors.Remove(_session); ChatStatus = originalStatus;
            ComposerSurface.Width = originalWidth;
            ClearToolSelection(); CaptionChip.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
        static void VerifyChipColor(Missum.App.Controls.NativeComposerToolChip chip, string key) =>
            VerifyIconBrush(((Grid)((Grid)chip.Content).Children[0]).Children.OfType<FontIcon>().Single().Foreground, key);
    }

    private async Task VerifyMathRenderingSmokeAsync(StackPanel body)
    {
        var math = new Missum.App.Controls.NativeStreamingMarkdown("Ein Bruch $\\frac{1}{2}");
        body.Children.Add(math);
        if (math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Any())
            throw new InvalidOperationException("An unfinished streamed formula was rendered prematurely.");
        math.UpdateText("Ein Bruch $\\frac{1}{2}$ im Satz.");
        var paragraph = math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Single();
        var formula = paragraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.InlineUIContainer>()
            .Select(inline => inline.Child).OfType<Missum.App.Controls.NativeFormulaView>().Single();
        AssertChatCursorAbsent(math);
        if (!formula.IsTypeset || math.Children.Count != 1)
            throw new InvalidOperationException("Inline math failed or an extra streaming decoration was rendered.");
        math.UpdateText("Ein Bruch $\\frac{1}{2}$ im Satz. Weiterer Text");
        if (!ReferenceEquals(formula, paragraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.InlineUIContainer>().Single().Child))
            throw new InvalidOperationException("A text delta rebuilt an unchanged inline formula.");
        math.UpdateText("```latex\n$\\frac{1}{2}$\n```\n`$x^2$` bleibt Code.");
        if (math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Any() || math.Children.OfType<ScrollViewer>().Any())
            throw new InvalidOperationException("Code examples must preserve their literal LaTeX source.");
        foreach (var protectedSource in new[] { "`code\n$x$\n`", "$$\n$x$" })
        {
            math.UpdateText(protectedSource);
            if (math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Any() || math.Children.OfType<ScrollViewer>().Any())
                throw new InvalidOperationException("Multiline code and unfinished display formulas must remain literal while streaming.");
        }
        math.UpdateText("**Die Formel $x^2$ gilt**");
        var boldParagraph = math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Single();
        if (!boldParagraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.Bold>().Any())
            throw new InvalidOperationException("Mathematics lost the surrounding Markdown emphasis.");
        math.UpdateText("[Wert $x$](https://example.com)");
        var linkParagraph = math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Single();
        if (!linkParagraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.Hyperlink>().Any()
            || !linkParagraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.InlineUIContainer>().Any(inline => inline.Child is Missum.App.Controls.NativeFormulaView { IsTypeset: true }))
            throw new InvalidOperationException("Mathematics lost the surrounding Markdown hyperlink.");
        var unsupported = new Missum.App.Controls.NativeFormulaView("$\\thisCommandDoesNotExist{x}$", false);
        if (unsupported.IsTypeset || unsupported.Content is not TextBlock { Text: "$\\thisCommandDoesNotExist{x}$" })
            throw new InvalidOperationException("Unsupported formulas must preserve their readable LaTeX source.");
        math.UpdateText("## Mathematische Darstellung\nEin Bruch $\\frac{1}{2}$ und die Wurzel $\\sqrt{x^2+y^2}$ im laufenden Text.\n\n$$\\int_0^\\infty e^{-x}\\,dx=1$$\n\n$$\\begin{aligned}a&=b+c\\\\d&=e-f\\end{aligned}$$\n\n$$A=\\begin{pmatrix}1&2\\\\3&4\\end{pmatrix}$$\n\nFormeln bleiben als LaTeX kopierbar.");
        if (math.Children.OfType<ScrollViewer>().Count() != 3 || math.Children.OfType<ScrollViewer>().Any(view => view.Content is not Missum.App.Controls.NativeFormulaView { IsTypeset: true }))
            throw new InvalidOperationException("Display formulas, aligned equations or matrices failed in the portable native runtime.");
        math.MaxWidth = 740;
        math.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
        await SaveMathPreviewAsync(math, "native-math-preview.png");
        if (Environment.GetEnvironmentVariable("MISSUM_SMOKE_MATH_INPUT") is { Length: > 0 } liveInput)
        {
            var liveText = await File.ReadAllTextAsync(liveInput);
            math.UpdateText(liveText);
            var displayCount = Missum.App.Controls.NativeMathSyntax.Split(liveText).Count(segment => segment.IsMath && segment.Display);
            if (displayCount == 0 || math.Children.OfType<ScrollViewer>().Count() != displayCount
                || math.Children.OfType<ScrollViewer>().Any(view => view.Content is not Missum.App.Controls.NativeFormulaView { IsTypeset: true }))
                throw new InvalidOperationException("The real model response could not be typeset in the portable runtime.");
            await SaveMathPreviewAsync(math, "native-math-live-preview.png");
        }
    }

    private async Task VerifyMarkdownTableSmokeAsync(StackPanel body)
    {
        const string source = "## Die wichtigsten Kandidaten und ihre offenen Punkte\n"
            + "Die Planck-Länge $\\ell_P \\approx 1{,}6\\times10^{-35}$ m liegt außerhalb der experimentellen Reichweite.\n\n"
            + "| Theorie | Stärken | Offene Lücken |\n\n|---|---|---|\n\n"
            + "| **Stringtheorie** | Vereinheitlicht alle Kräfte; enthält Gravitation | Keine eindeutige Vorhersage |\n\n"
            + "| **Loop-Quantengravitation** | Diskrete Geometrie | Klassischer Grenzfall und Materiekopplung |";
        var markdown = new Missum.App.Controls.NativeStreamingMarkdown(source) { MaxWidth = 920 };
        body.Children.Add(markdown);
        try
        {
            var table = markdown.RenderedTables.Single();
            if (table.ColumnDefinitions.Count != 3 || table.RowDefinitions.Count != 3 || table.Children.Count != 9)
                throw new InvalidOperationException("The screenshot's loose Markdown table did not render as native rows and columns.");
            var cells = table.Children.ToArray();
            markdown.UpdateText(source + "\n\n| **AdS/CFT** | Exakte Dualität | Übertragung auf de-Sitter-Räume |\n\nFormeln und Text verwenden dieselbe Schriftgröße.");
            if (!ReferenceEquals(table, markdown.RenderedTables.Single()) || table.RowDefinitions.Count != 4
                || cells.Where((cell, index) => !ReferenceEquals(cell, table.Children[index])).Any())
                throw new InvalidOperationException("A streamed table row replaced existing cells or lost its table layout.");
            var formulaParagraph = markdown.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Single();
            var inlineFormula = formulaParagraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.InlineUIContainer>()
                .Select(inline => inline.Child).OfType<Missum.App.Controls.NativeFormulaView>().Single();
            if (!inlineFormula.IsTypeset || inlineFormula.FontSize != formulaParagraph.FontSize)
                throw new InvalidOperationException("Inline formulas and answer text must use the same font size.");
            AssertChatCursorAbsent(markdown);
            await SaveMathPreviewAsync(markdown, "native-table-math-preview.png");
            markdown.Width = 420; markdown.UpdateLayout();
            if (table.ActualWidth > 421 || table.Children.OfType<Microsoft.UI.Xaml.FrameworkElement>().Any(cell => cell.ActualWidth > 421))
                throw new InvalidOperationException("Table columns failed to wrap inside the narrow chat width.");
            await SaveMathPreviewAsync(markdown, "native-table-narrow-preview.png");
        }
        finally { body.Children.Remove(markdown); }
    }

    private async Task VerifyAnswerStreamingSmokeAsync(JsonElement original)
    {
        var session = Guid.NewGuid(); var id = Guid.NewGuid().ToString();
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(session);
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new { id, sessionId = session, role = "assistant", content = "",
            status = "streaming", createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow } });
        try
        {
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            foreach (var content in new[] { "Die Antwort beginnt bereits", "Die Antwort beginnt bereits während des Laufs.",
                "Die Antwort beginnt bereits während des Laufs. Weitere Textteile erscheinen fortlaufend." })
            {
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = session, messageId = id, content }));
                // Do not force a render: verify the production render timer.
                await Task.Delay(180);
                if (!_messageBlocks.TryGetValue(id, out var blocks) || !blocks.TryGetValue("text:0", out var view)
                    || view is not Missum.App.Controls.NativeStreamingMarkdown markdown || !markdown.Children.OfType<TextBlock>()
                        .SelectMany(text => text.Inlines).OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => run.Text == content)
                    || S(_messages[id], "status") != "streaming")
                    throw new InvalidOperationException("Answer text was not visibly rendered before run completion.");
                AssertChatCursorAbsent(MessageBody(_messageViews[id].View));
            }
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-chat-streaming-validation.json"),
                JsonSerializer.Serialize(new { renderer = "WinUI3", passed = true, visibleDeltasBeforeCompletion = 3,
                    tableColumns = 3, tableRows = 4, sharedMathFontSize = 16, reasoningDisclosureDefaultCollapsed = true,
                    chatCursorAbsent = true }));
        }
        finally { ApplyEvent("state.snapshot", original); RenderMessagesNow(); }
    }

    private static async Task SaveMathPreviewAsync(Microsoft.UI.Xaml.FrameworkElement math, string filename)
    {
        math.UpdateLayout();
        await Task.Delay(100);
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(math);
        if (bitmap.PixelWidth < 100 || bitmap.PixelHeight < 60)
            throw new InvalidOperationException("The native formula preview has no visible layout.");
        var pixels = await bitmap.GetPixelsAsync();
        using var file = File.Create(Path.Combine(App.Current.DataDirectory, filename));
        using var stream = file.AsRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
    }
}
