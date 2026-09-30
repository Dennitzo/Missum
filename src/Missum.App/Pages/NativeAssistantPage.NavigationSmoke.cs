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
        var header = _messageBlocks[activeId]["header"];
        UpdateMessageHeader(header, activeMessage);
        var label = (TextBlock)((StackPanel)header).Children[0];
        if (!label.Text.StartsWith("Modell generiert", StringComparison.Ordinal) || !label.Text.Contains("Token · In Bearbeitung seit", StringComparison.Ordinal))
            throw new InvalidOperationException("The active header does not display session tokens and elapsed time.");
        var stable = label.Text;
        active["content"] = JsonSerializer.SerializeToElement("Ein gestreamter Absatz mit weiteren Wörtern");
        UpdateMessageBlocks(activeId, JsonSerializer.SerializeToElement(active), body);
        if (label.Text != stable) throw new InvalidOperationException("A text delta reset the generation header.");
        var markdown = body.Children.OfType<Missum.App.Controls.NativeStreamingMarkdown>().Last();
        bool HasCursor() => markdown.Children.OfType<TextBlock>().SelectMany(block => block.Inlines).OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => run.Text == "▍");
        if (!HasCursor() || markdown.Children.Last() is not TextBlock cursorLine || cursorLine.Inlines.Count != 1)
            throw new InvalidOperationException("The streaming cursor is not on its own final line.");
        active["status"] = JsonSerializer.SerializeToElement("completed");
        UpdateMessageBlocks(activeId, JsonSerializer.SerializeToElement(active), body);
        if (HasCursor() || !label.Text.Contains("Uhr ·", StringComparison.Ordinal) || !label.Text.EndsWith("lang gearbeitet", StringComparison.Ordinal))
            throw new InvalidOperationException("Completion did not remove the cursor and finalize the header.");
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
        await VerifyConversationSelectionSmokeAsync();
        await VerifyComposerFooterSmokeAsync();
        await VerifyMathRenderingSmokeAsync(body);
        await VerifyScienceViewsSmokeAsync();
        ApplyEvent("state.snapshot", original);
        RenderMessagesNow();
        return visits;
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
            if (ResearchHost.Content is not Grid) throw new InvalidOperationException("Simulation view was not created.");
            var fixture = Environment.GetEnvironmentVariable("MISSUM_SCIENCE_PDF_SMOKE_PATH");
            if (!string.IsNullOrWhiteSpace(fixture) && File.Exists(fixture))
            {
                var pdf = new Missum.App.Controls.NativePublicationView();
                ResearchHost.Content = pdf;
                await pdf.LoadAsync(fixture);
                if (pdf.PageCount == 0 || !pdf.HasRenderedPage) throw new InvalidOperationException("Native scientific PDF preview failed.");
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
            SelectedToolChip.SetTool("Planen", "\uEA80");
            SelectedToolChip.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            Composer.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            SelectedToolChip.SetPointerState(false);
            ComposerSurface.UpdateLayout();
            var width = SelectedToolChip.ActualWidth;
            if (width < 50 || SelectedToolChip.ActualHeight != 28 || ComposerChipsScroll.ActualWidth <= 0)
                throw new InvalidOperationException("The footer tool chip has an invalid layout.");
            await SaveMathPreviewAsync(ComposerSurface, "native-composer-preview.png");
            SelectedToolChip.SetPointerState(true);
            await Task.Delay(150);
            ComposerSurface.UpdateLayout();
            if (!SelectedToolChip.IsRemoveAffordanceVisible || SelectedToolChip.ActualWidth != width)
                throw new InvalidOperationException("Hover changed the chip width or did not expose removal.");
            await SaveMathPreviewAsync(ComposerSurface, "native-composer-hover-preview.png");
            SelectedToolChip.SetPointerState(false);
            // The portable smoke window is hidden, so exercise the same focus-state handler directly.
            SelectedToolChip.SetKeyboardFocusState(true);
            if (!SelectedToolChip.IsRemoveAffordanceVisible)
                throw new InvalidOperationException("Keyboard focus did not expose chip removal.");
            Composer.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            SelectedToolChip.SetKeyboardFocusState(false);
            SelectedToolChip.SetTool("Ein sehr langer Werkzeugname für die Platzprüfung", "\uE8A5");
            CaptionChip.SetTool("Live-Untertitel", "\uE7F4");
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
    }

    private static async Task VerifyMathRenderingSmokeAsync(StackPanel body)
    {
        var math = new Missum.App.Controls.NativeStreamingMarkdown("Ein Bruch $\\frac{1}{2}");
        body.Children.Add(math);
        if (math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Any())
            throw new InvalidOperationException("An unfinished streamed formula was rendered prematurely.");
        math.SetStreaming(true);
        math.UpdateText("Ein Bruch $\\frac{1}{2}$ im Satz.");
        var paragraph = math.Children.OfType<Missum.App.Controls.NativeMathParagraph>().Single();
        var formula = paragraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.InlineUIContainer>()
            .Select(inline => inline.Child).OfType<Missum.App.Controls.NativeFormulaView>().Single();
        if (!formula.IsTypeset || paragraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Any(run => run.Text == "▍")
            || math.Children.Last() is not TextBlock cursorLine || cursorLine.Inlines.Count != 1
            || cursorLine.Inlines[0] is not Microsoft.UI.Xaml.Documents.Run { Text: "▍" })
            throw new InvalidOperationException("Inline math or the separate streaming cursor line failed in the native renderer.");
        math.UpdateText("Ein Bruch $\\frac{1}{2}$ im Satz. Weiterer Text");
        if (!ReferenceEquals(formula, paragraph.TrailingInlines.OfType<Microsoft.UI.Xaml.Documents.InlineUIContainer>().Single().Child))
            throw new InvalidOperationException("A text delta rebuilt an unchanged inline formula.");
        math.SetStreaming(false);
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
