using System.Text.Json;
using Missum.App.Controls;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyToolTabSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Der Werkzeugtab-Test benötigt ein isoliertes Smoke-Profil ohne Modellstart.");
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        var chats = App.Current.GetService<IChatRepository>();
        var originalSession = Guid.Parse(S(original, "activeSessionId"));
        var fixtureSession = Guid.Empty;
        var maximumHeightDrift = 0d;
        try
        {
            var fixture = await Task.Run(async () =>
            {
                var session = await chats.CreateSessionAsync("Isolierter Werkzeugtab-Test", ChatMode.General, _lifetime.Token);
                fixtureSession = session.Id;
                await chats.AddMessageAsync(session.Id, ChatRole.User, "Prüfe die vollständige Werkzeugdarstellung.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
                    "Die gespeicherten Werkzeuge bleiben im Chat dauerhaft kompakt; ihre Details werden nur im eigenen Tab geladen.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var at = DateTimeOffset.UtcNow;
                var output = string.Join('\n', Enumerable.Range(0, 3200).Select(index => $"Kontrollzeile {index:D4}: vollständige gespeicherte Ausgabe."))
                    + "\nEND_OF_FULL_TOOL_OUTPUT";
                var diff = "diff --git a/analysis.py b/analysis.py\n--- a/analysis.py\n+++ b/analysis.py\n@@ -1,80 +1,80 @@\n"
                    + string.Join('\n', Enumerable.Range(1, 80).SelectMany(index => new[]
                    {
                        $"-value_{index} = {index}",
                        index == 1 ? "+description = '" + string.Concat(Enumerable.Repeat("Lange Zeile muss in der verfügbaren Tabbreite umbrechen. ", 40)) + "'"
                            : index == 80 ? "+END_OF_TOOL_DIFF = True" : $"+value_{index} = {index * 2}",
                    }));
                AssistantToolStep[] steps =
                [
                    new("tool-tab-read", "coding.read", "completed", InputJson: "{\"path\":\"analysis.py\",\"startLine\":1}",
                        OutputJson: JsonSerializer.Serialize(new { path = "analysis.py", content = "import math\nresult = math.sqrt(4)\n", startLine = 1, totalLines = 2 }),
                        ContentOffset: 0, UpdatedAt: at),
                    new("tool-tab-command", "coding.command", "completed", InputJson: "{\"command\":\"python analysis.py\"}",
                        OutputJson: JsonSerializer.Serialize(new { stdout = output, exitCode = 0 }), ContentOffset: 0, UpdatedAt: at),
                    new("tool-tab-edit", "coding.edit", "completed", InputJson: "{\"path\":\"analysis.py\"}",
                        OutputJson: JsonSerializer.Serialize(new { path = "analysis.py", diff }), ContentOffset: 0, UpdatedAt: at),
                ];
                foreach (var step in steps) await chats.SaveToolStepAsync(assistant.Id, step, _lifetime.Token);
                var durable = JsonSerializer.Serialize(await chats.ListMessagesAsync(session.Id, _lifetime.Token));
                return new { Session = session, MessageId = assistant.Id.ToString(), Steps = steps, Durable = durable };
            }, _lifetime.Token);

            if (!await CommandAsync("session.open", new { sessionId = fixture.Session.Id }))
                throw new InvalidOperationException("Die gespeicherte Werkzeugtab-Sitzung konnte nicht geöffnet werden.");
            ShowChatView();
            RenderMessagesNow();
            foreach (var step in fixture.Steps)
            {
                var initial = MessageSteps(_messages[fixture.MessageId]).Single(item => S(item, "id") == step.Id);
                if (!PagingSmokeFlag(initial, "detailsDeferred") || S(initial, "outputJson").Length != 0)
                    throw new InvalidOperationException("Werkzeugdetails wurden vor dem Öffnen ihres Tabs geladen.");

                var tool = await PagingSmokeToolInViewportAsync(fixture.MessageId, step.Id);
                AssertCompactTool(tool);
                ConversationContent.UpdateLayout();
                var height = ConversationScroll.ScrollableHeight;
                var rowHeight = tool.ActualHeight;
                var previousTabs = _toolTabs.Count;
                InvokePagingSmokeTool(tool);
                await WaitForPagingSmokeAsync(() => _activeToolTab is { } active && active.MessageId == fixture.MessageId
                    && active.StepId == step.Id && !ToolTabDetailsDeferred(active.Step)
                    && Descendants(active.Details).OfType<NativeToolResultView>().Any(),
                    "Der Klick hat keinen Werkzeugtab mit nachgeladenen Details geöffnet.");
                var tab = _activeToolTab!;
                if (_toolTabs.Count != previousTabs + 1 || tab.StepId != step.Id || tab.SessionId != fixture.Session.Id
                    || S(tab.Step, "outputJson") != step.OutputJson || !tab.Details.IsExpanded
                    || _toolDetailsHost.Visibility != Visibility.Visible || BodyGrid.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Der Werkzeugtab hat seine vollständigen gespeicherten Inhalte oder die Eigentümerzuordnung verloren.");
                AssertCompactTool(tool);
                tab.Scroll.UpdateLayout();
                await Task.Delay(30, _lifetime.Token);
                AssertSingleNativeScroll(tab);
                if (step.Tool == "coding.command")
                {
                    if (tab.Scroll.ScrollableHeight <= 0 || tab.Details.ActualHeight <= 520)
                        throw new InvalidOperationException("Eine lange Ausgabe nutzt nicht den nativen Werkzeugtab als einzigen Scrollbereich.");
                    for (var page = 0; page < 64; page++)
                    {
                        var next = Descendants(tab.Details).OfType<Button>()
                            .FirstOrDefault(button => button.Content?.ToString() == "Weitere Zeilen" && button.IsEnabled);
                        if (next is null) break;
                        if (new ButtonAutomationPeer(next).GetPattern(PatternInterface.Invoke) is not IInvokeProvider advance)
                            throw new InvalidOperationException("Die vollständige Werkzeugausgabe besitzt keine bedienbare Seitennavigation.");
                        advance.Invoke();
                        await Task.Delay(10, _lifetime.Token);
                    }
                    if (!Descendants(tab.Details).OfType<TextBlock>().Any(text => text.Text.Contains("END_OF_FULL_TOOL_OUTPUT", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Der Werkzeugtab stellt das Ende der vollständigen Ausgabe nicht dar.");
                    await AssertOuterScrollReachesEndAsync(tab, "END_OF_FULL_TOOL_OUTPUT");
                    await SaveMathPreviewAsync(LayoutRoot, "native-tool-detail-preview.png");
                }
                if (step.Tool == "coding.edit")
                {
                    var diffView = Descendants(tab.Details).OfType<NativeDiffView>().Single();
                    if (diffView.IsPreparing || diffView.ActualHeight <= 420 || tab.Scroll.ScrollableHeight <= 0
                        || diffView.Content is not StackPanel diffRows)
                        throw new InvalidOperationException("Der Dateivergleich ist weiterhin in einem begrenzten inneren Fenster eingebettet.");
                    var code = diffRows.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<TextBlock>())
                        .Where(text => Grid.GetColumn(text) == 3).ToArray();
                    if (code.Length != 160 || code.Any(text => text.TextWrapping != TextWrapping.Wrap)
                        || !code.Any(text => text.ActualHeight > text.LineHeight * 2))
                        throw new InvalidOperationException("Der Werkzeug-Diff nutzt die verfügbare Tabbreite nicht mit vollständig umbrechenden Codezeilen.");
                    await AssertOuterScrollReachesEndAsync(tab, "END_OF_TOOL_DIFF");
                    await SaveMathPreviewAsync(LayoutRoot, "native-tool-diff-preview.png");
                }

                ShowChatView();
                RenderMessagesNow();
                tool = await PagingSmokeToolInViewportAsync(fixture.MessageId, step.Id);
                AssertCompactTool(tool);
                InvokePagingSmokeTool(tool);
                await WaitForPagingSmokeAsync(() => ReferenceEquals(_activeToolTab, tab),
                    "Erneutes Öffnen hat nicht den vorhandenen Werkzeugtab aktiviert.");
                if (_toolTabs.Count != previousTabs + 1)
                    throw new InvalidOperationException("Erneutes Öffnen hat einen doppelten Werkzeugtab erzeugt.");

                if (new ButtonAutomationPeer(tab.Close).GetPattern(PatternInterface.Invoke) is not IInvokeProvider close)
                    throw new InvalidOperationException("Der Werkzeugtab besitzt keinen bedienbaren Schließen-Button.");
                close.Invoke();
                await WaitForPagingSmokeAsync(() => _activeToolTab is null && BodyGrid.Visibility == Visibility.Visible
                    && !_toolTabs.Contains(tab), "Das Schließen hat die kompakte Chatansicht nicht wiederhergestellt.");
                tool = await PagingSmokeToolInViewportAsync(fixture.MessageId, step.Id);
                ConversationContent.UpdateLayout();
                maximumHeightDrift = Math.Max(maximumHeightDrift, Math.Abs(ConversationScroll.ScrollableHeight - height));
                if (Math.Abs(tool.ActualHeight - rowHeight) > 2 || maximumHeightDrift > 2)
                    throw new InvalidOperationException("Das Öffnen/Schließen eines Werkzeugtabs hat die Höhe des Chatverlaufs verändert.");
                AssertCompactTool(tool);
            }

            var unchanged = JsonSerializer.Serialize(await chats.ListMessagesAsync(fixture.Session.Id, _lifetime.Token));
            if (unchanged != fixture.Durable)
                throw new InvalidOperationException("Das Öffnen von Werkzeugdetails hat den gespeicherten Chatverlauf verändert.");
            await SaveMathPreviewAsync(LayoutRoot, "native-tool-tabs-preview.png");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-tool-tabs-validation.json"),
                JsonSerializer.Serialize(new { passed = true, cases = fixture.Steps.Select(step => step.Tool),
                    permanentlyCompact = true, detailsLoadedOnlyOnOpen = true, fullReceiptPreserved = true,
                    sameTabReopened = true, singleNativeScroll = true, detailsUseAvailableWidth = true,
                    wrappedDiffLines = true, outerScrollReachesPageEnd = true,
                    maximumHeightDrift, historyUnchanged = true, modelRequests = 0 }));
        }
        finally
        {
            foreach (var tab in _toolTabs.Where(tab => tab.SessionId == fixtureSession).ToArray()) CloseToolTab(tab);
            ShowChatView();
            await CommandAsync("session.open", new { sessionId = originalSession });
            if (fixtureSession != Guid.Empty) await chats.DeleteSessionAsync(fixtureSession, _lifetime.Token);
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }

        static void AssertCompactTool(ToolStepView tool)
        {
            if (tool.IsExpanded || Descendants(tool).OfType<NativeToolResultView>().Any()
                || Descendants(tool).OfType<FontIcon>().Any(icon => icon.Visibility == Visibility.Visible
                    && icon.Glyph is "\uE70D" or "\uE8A7"))
                throw new InvalidOperationException("Die Werkzeugzeile hat einen Dropdown oder Details im Chat angezeigt.");
        }

        void AssertSingleNativeScroll(NativeToolTab tab)
        {
            if (Descendants(tab.Details).OfType<ScrollViewer>().Any()
                || tab.Scroll.ScrollableWidth > .5
                || Math.Abs(tab.Scroll.ActualWidth - _toolDetailsHost.ActualWidth) > 2
                || tab.Scroll.Content is not FrameworkElement page || !double.IsPositiveInfinity(page.MaxWidth)
                || tab.Details.ActualWidth < tab.Scroll.ActualWidth - 88)
                throw new InvalidOperationException("Werkzeugdetails müssen ohne verschachtelte Scrollbereiche die verfügbare Fensterbreite nutzen.");
        }

        async Task AssertOuterScrollReachesEndAsync(NativeToolTab tab, string marker)
        {
            tab.Scroll.UpdateLayout();
            tab.Scroll.ChangeView(null, tab.Scroll.ScrollableHeight, null, disableAnimation: true);
            tab.Scroll.UpdateLayout();
            await Task.Delay(30, _lifetime.Token);
            AssertSingleNativeScroll(tab);
            var last = Descendants(tab.Details).OfType<TextBlock>().LastOrDefault(text =>
                text.Text.Contains(marker, StringComparison.Ordinal)
                || text.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>()
                    .Any(run => run.Text.Contains(marker, StringComparison.Ordinal)))
                ?? throw new InvalidOperationException("Das Ende des Werkzeuginhalts ist nicht vorhanden.");
            var bottom = last.TransformToVisual(tab.Scroll).TransformPoint(new Windows.Foundation.Point()).Y + last.ActualHeight;
            if (tab.Scroll.VerticalOffset <= 0 || bottom > tab.Scroll.ActualHeight + 2
                || bottom < 0 || Math.Abs(tab.Scroll.VerticalOffset - tab.Scroll.ScrollableHeight) > 2)
                throw new InvalidOperationException("Das Ende der Werkzeugausgabe ist über den nativen Tab-Scrollbereich nicht erreichbar.");
        }
    }
}
