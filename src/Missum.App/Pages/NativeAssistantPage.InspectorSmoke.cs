using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyInspectorSidebarSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Seitenleistenprüfung benötigt ein isoliertes Profil ohne Modellstart.");

        var originalWidth = AssistantFrame.Width;
        var originalAlignment = AssistantFrame.HorizontalAlignment;
        var originalSidebar = Sidebar.Visibility;
        var originalSidebarColumn = SidebarColumn.Width;
        var originalOutputsRequested = _settings.Current.AssistantOutputsVisible;
        var originalFixture = _selectionInputSmokeActive;
        var originalTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme == ElementTheme.Light ? AppTheme.Light : AppTheme.Dark;
        var app = App.Current;
        var originalAccent = app.AccentColor;
        var originalBackground = app.BackgroundColor;
        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _selectionInputSmokeActive = true;
        try
        {
            if (originalSidebarColumn.Value > 0 && originalSidebarColumn.Value != NativeInspectorLayout.SidebarWidth)
                throw new InvalidOperationException("Die rechte Seitenleiste ist nicht genauso breit wie die native Projektleiste.");
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new(0);
            AssistantFrame.HorizontalAlignment = HorizontalAlignment.Left;
            AssistantFrame.Width = 1040;

            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
            snapshot["chatMode"] = JsonSerializer.SerializeToElement("coding");
            snapshot["isRunning"] = JsonSerializer.SerializeToElement(false);
            snapshot["workspacePath"] = JsonSerializer.SerializeToElement("C:\\Inspector-Smoke\\Forschungsprojekt");
            snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
            {
                id = messageId, sessionId = owner, role = "assistant", status = "completed",
                content = "Der Forschungsstand wird anhand der vorhandenen Herleitung weiter geprüft.",
                createdAt = now, updatedAt = now, toolSteps = new[] { new
                {
                    id = "inspector-read", tool = "coding.read", status = "completed",
                    inputJson = JsonSerializer.Serialize(new { path = "Publikation.md", startLine = 1, maximumLines = 2 }, JsonOptions),
                    outputJson = JsonSerializer.Serialize(new { success = true, path = "Publikation.md", startLine = 1,
                        content = "# Publikation\nEine gespeicherte Herleitung.", lineCount = 2, truncated = false }, JsonOptions),
                } },
            } }, JsonOptions);
            snapshot["subagents"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            snapshot["workingPlan"] = JsonSerializer.SerializeToElement(new
            {
                updatedAt = now,
                storedSteps = new[]
                {
                    new { id = "s1", title = "Voraussetzungen und Einheiten prüfen", status = "completed" },
                    new { id = "s2", title = "Herleitung unabhängig nachrechnen", status = "in_progress" },
                    new { id = "s3", title = "Publikation und Literaturverweise aktualisieren", status = "pending" },
                },
                storedAcceptanceCriteria = Array.Empty<object>(),
            }, JsonOptions);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            ShowChatView(); RenderMessagesNow(); RenderSessionTabs();
            await SetInspectorVisibleAsync(true);
            await ArrangeSidebarAsync();
            AssertSidebarGeometry();

            if (Inspector.Parent != WorkspaceGrid || BodyGrid.Parent != WorkspaceGrid
                || Grid.GetColumn(Inspector) != 1 || Grid.GetColumn(BodyGrid) != 0)
                throw new InvalidOperationException("Ausgaben liegt nicht in einer separaten nativen Layoutspalte.");
            if (ChangesSummaryButton.Parent is not Panel summaries
                || summaries.Children.IndexOf(ContextSummaryRow) != summaries.Children.IndexOf(ChangesSummaryButton) + 1)
                throw new InvalidOperationException("Der Kontextzähler steht nicht direkt unter Änderungen.");
            if (InspectorWorkingPlanHost.Visibility != Visibility.Visible
                || _inspectorWorkingPlanView.StepCount != 3 || _workingPlanTabOpen)
                throw new InvalidOperationException("Der Arbeitsplan erscheint nicht ohne automatischen Tab in der Seitenleiste.");

            UpdateContext(JsonSerializer.SerializeToElement(new { contextUsed = 154900, contextLimit = 258000 }));
            AssertCounter("154.9k / 258k", 154900d / 258000 * 100);
            ApplyEvent("chat.started", JsonSerializer.SerializeToElement(new { sessionId = owner }, JsonOptions));
            UpdateContext(JsonSerializer.SerializeToElement(new { contextUsed = 194700, contextLimit = 258000 }));
            _lastHeaderSecond = -1;
            UpdateRunDurations();
            AssertCounter("194.7k / 258k", 194700d / 258000 * 100);
            UpdateContext(JsonSerializer.SerializeToElement(new { contextUsed = 29300, contextLimit = 131072 }));
            _lastHeaderSecond = -1;
            UpdateRunDurations();
            AssertCounter("29.3k / 131.1k", 29300d / 131072 * 100);
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = owner, runStatus = "Fertig" }, JsonOptions));

            var chatWidthWithSidebar = BodyGrid.ActualWidth;
            InvokeSidebarToggle();
            await WaitForPagingSmokeAsync(() => Inspector.Visibility == Visibility.Collapsed && !_settings.Current.AssistantOutputsVisible,
                "Der Ausgaben-Button schließt die native Seitenleiste nicht.");
            await ArrangeSidebarAsync();
            if (InspectorColumn.Width.Value != 0 || BodyGrid.ActualWidth < chatWidthWithSidebar + NativeInspectorLayout.SidebarWidth - 1)
                throw new InvalidOperationException("Das Schließen gibt die reservierte Seitenleistenbreite nicht an den Inhalt zurück.");
            InvokeSidebarToggle();
            await WaitForPagingSmokeAsync(() => Inspector.Visibility == Visibility.Visible && _settings.Current.AssistantOutputsVisible,
                "Der Ausgaben-Button öffnet die native Seitenleiste nicht erneut.");
            await ArrangeSidebarAsync();
            AssertSidebarGeometry();

            AssistantFrame.Width = 850;
            await ArrangeSidebarAsync();
            if (Inspector.Visibility != Visibility.Collapsed || InspectorColumn.Width.Value != 0
                || BodyGrid.ActualWidth < 848 || !_settings.Current.AssistantOutputsVisible)
                throw new InvalidOperationException("Das schmale Fenster blendet Ausgaben nicht automatisch aus, ohne die Nutzerpräferenz zu ändern.");
            AssistantFrame.Width = 1040;
            await ArrangeSidebarAsync();
            AssertSidebarGeometry();

            await SetInspectorVisibleAsync(false);
            AssistantFrame.Width = 850; await ArrangeSidebarAsync();
            AssistantFrame.Width = 1040; await ArrangeSidebarAsync();
            if (Inspector.Visibility != Visibility.Collapsed || _settings.Current.AssistantOutputsVisible)
                throw new InvalidOperationException("Das Vergrößern öffnet eine manuell geschlossene Seitenleiste erneut.");
            await SetInspectorVisibleAsync(true); await ArrangeSidebarAsync();

            await OpenWorkingPlanTabAsync();
            await ArrangeSidebarAsync();
            AssertSidebarGeometry(requireChatVisible: false);
            if (_workingPlanHost.Visibility != Visibility.Visible || InspectorWorkingPlanHost.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Beim Öffnen eines Arbeitsplan-Tabs verschwindet die rechte Seitenleiste.");
            CloseWorkingPlanTab();
            ShowChatView(); await ArrangeSidebarAsync();

            await OpenToolTabAsync(messageId.ToString(), "inspector-read");
            await ArrangeSidebarAsync();
            AssertSidebarGeometry(requireChatVisible: false);
            if (_activeToolTab is not { } readTab || _toolDetailsHost.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Die Werkzeugansicht wurde nicht in ihrem nativen Tab geöffnet.");
            CloseToolTab(readTab);
            ShowChatView(); await ArrangeSidebarAsync();

            // Populate the native sidebar for the requested visual preview;
            // this profile is isolated and never dispatches a model request.
            ObserveWorkingPlan(JsonSerializer.SerializeToElement(new
            {
                workingPlan = new
                {
                    updatedAt = now.AddMinutes(1),
                    storedSteps = new[]
                    {
                        new { id = "s1", title = "Fragestellung und Annahmen klären", status = "completed" },
                        new { id = "s2", title = "Vorhandene Quellen auswerten", status = "completed" },
                        new { id = "s3", title = "Gleichungen herleiten", status = "completed" },
                        new { id = "s4", title = "Ergebnisse unabhängig prüfen", status = "in_progress" },
                        new { id = "s5", title = "Numerische Grenzfälle vergleichen", status = "pending" },
                        new { id = "s6", title = "Plots und Beweise einarbeiten", status = "pending" },
                        new { id = "s7", title = "Publikation abschließend prüfen", status = "pending" },
                    },
                    storedAcceptanceCriteria = new[]
                    {
                        new { id = "a1", title = "Herleitung nachvollziehbar", status = "completed" },
                        new { id = "a2", title = "Gegenprüfung dokumentiert", status = "in_progress" },
                        new { id = "a3", title = "Offene Grenzen ausgewiesen", status = "pending" },
                    },
                },
            }, JsonOptions));
            ObserveLiveSourceMetadata(JsonSerializer.SerializeToElement(new
            {
                sessionId = owner,
                sourceUpdates = Enumerable.Range(1, 12).Select(index => new
                {
                    id = "sidebar-preview-source-" + index, tool = "web.search", title = "Beispielquelle " + index,
                    url = "https://example.org/forschung/quelle-" + index, time = now,
                }).ToArray(),
            }, JsonOptions));
            string[] childTitles = ["Literaturvergleich", "Analytische Gegenprüfung", "Numerische Validierung", "Einheitenprüfung", "Beweisprüfung"];
            for (var index = 0; index < childTitles.Length; index++)
                ObserveSubagentSnapshot(JsonSerializer.SerializeToElement(new
                {
                    sessionId = owner, parentMessageId = messageId, agentId = "sidebar-preview-agent-" + index,
                    runId = Guid.NewGuid(), title = childTitles[index], status = index < 3 ? "running" : "completed",
                    isRunning = index < 3, createdAt = now.AddSeconds(index), messages = Array.Empty<object>(),
                }, JsonOptions));
            RenderSubagentOverlay();
            UpdateContext(JsonSerializer.SerializeToElement(new { contextUsed = 154900, contextLimit = 258000 }));
            var previewChanges = JsonSerializer.SerializeToElement(new[]
            {
                new { path = "Publikation.md", addedLines = 195, removedLines = 18, isBinary = false },
                new { path = "work/validierung.py", addedLines = 42, removedLines = 6, isBinary = false },
            });
            RenderChangesSummaryContent(previewChanges.EnumerateArray().ToArray(), false, "");

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                app.ApplyTheme(theme);
                app.ApplyBackgroundColor(theme == AppTheme.Dark ? "#202027" : "#FFFFFF");
                app.ApplyAccentColor(theme == AppTheme.Dark ? "#E9B557" : "#3267B2");
                await ArrangeSidebarAsync();
                AssertSidebarGeometry();
                await SaveMathPreviewAsync(AssistantFrame, "native-inspector-sidebar-" + theme.ToString().ToLowerInvariant() + ".png");
                await SaveMathPreviewAsync(Inspector, "native-inspector-sidebar-detail-" + theme.ToString().ToLowerInvariant() + ".png");
                AssertSidebarTypography();
            }

            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-inspector-sidebar-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, renderer = "WinUI3", fixedWidth = NativeInspectorLayout.SidebarWidth, nativeLayoutColumn = true,
                    fullContentHeight = true, noOverlayOverlap = true, manualToggleReflowsContent = true, sameWidthAsProjects = true,
                    narrowWindowHides = true, wideWindowRestoresRequestedSidebar = true,
                    manualClosedPreferencePreserved = true, contextDirectlyBelowChanges = true,
                    contextMatchesComposerRing = true, streamingCounterUpdates = true, compactionCounterUpdates = true,
                    planVisibleWithoutPermanentTab = true, sidebarPersistsInPlanTab = true, sidebarPersistsInToolTab = true,
                    uniformTextSize = 13, uniformIconSize = 16, contextIconUsesTextColor = true,
                    changesIconUsesTextColor = true, planHeadingUsesTextColor = true, planHeadingHasNoIcons = true,
                    sidebarUsesTextColorExceptPlanetsAndDiffCounts = true, diffCountsUseGreenAndRed = true,
                    sourcesThenSubagentsThenWorkingPlan = true, uniformGroupHeadings = true,
                    themes = WorkingPlanSmokeThemes, modelRequests = 0, userDataModified = false,
                }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            CloseWorkingPlanTab();
            app.ApplyTheme(originalTheme); app.ApplyBackgroundColor(originalBackground); app.ApplyAccentColor(originalAccent);
            AssistantFrame.Width = originalWidth;
            AssistantFrame.HorizontalAlignment = originalAlignment;
            Sidebar.Visibility = originalSidebar;
            SidebarColumn.Width = originalSidebarColumn;
            ApplyEvent("state.snapshot", original); ShowChatView(); RenderMessagesNow();
            await SetInspectorVisibleAsync(originalOutputsRequested);
            UpdateLayout();
            _selectionInputSmokeActive = originalFixture;
        }

        async Task ArrangeSidebarAsync()
        {
            UpdateLayout(); UpdateResponsiveLayout(); UpdateLayout();
            await WaitForNativePreviewFrameAsync();
            UpdateResponsiveLayout(); UpdateLayout();
        }

        void AssertSidebarGeometry(bool requireChatVisible = true)
        {
            var bounds = Inspector.TransformToVisual(WorkspaceGrid).TransformBounds(new Rect(0, 0, Inspector.ActualWidth, Inspector.ActualHeight));
            if (Inspector.Visibility != Visibility.Visible || Math.Abs(Inspector.ActualWidth - NativeInspectorLayout.SidebarWidth) > 1
                || Math.Abs(bounds.Y) > 1 || Math.Abs(bounds.Bottom - WorkspaceGrid.ActualHeight) > 1
                || Math.Abs(bounds.Right - WorkspaceGrid.ActualWidth) > 1)
                throw new InvalidOperationException("Die native Ausgaben-Seitenleiste hat keine feste Breite oder füllt die Inhaltshöhe nicht aus.");
            if (requireChatVisible)
            {
                var chat = BodyGrid.TransformToVisual(WorkspaceGrid).TransformBounds(new Rect(0, 0, BodyGrid.ActualWidth, BodyGrid.ActualHeight));
                if (BodyGrid.Visibility != Visibility.Visible || chat.Right > bounds.Left + 1)
                    throw new InvalidOperationException("Die Ausgaben-Seitenleiste überlagert weiterhin den Chatinhalt.");
            }
        }

        void AssertCounter(string expected, double ringValue)
        {
            if (InspectorContextCount.Text != expected || Math.Abs(ContextRing.Value - ringValue) > .001)
                throw new InvalidOperationException("Der native Kontextzähler und die Kontextanzeige im Prompt sind nicht synchron: " + InspectorContextCount.Text);
        }

        void AssertSidebarTypography()
        {
            if (SourcesPanel.Parent is not Panel sections || SubagentsSection.Parent != sections || InspectorWorkingPlanHost.Parent != sections
                || sections.Children.IndexOf(SubagentsSection) != sections.Children.IndexOf(SourcesPanel) + 1
                || sections.Children.IndexOf(InspectorWorkingPlanHost) != sections.Children.IndexOf(SubagentsSection) + 1)
                throw new InvalidOperationException("Die rechte Seitenleiste muss Quellen, Subagenten und Arbeitsplan direkt nacheinander anzeigen.");
            var labels = Descendants(Inspector).OfType<TextBlock>().Where(label =>
            {
                for (DependencyObject? parent = VisualTreeHelper.GetParent(label); parent is not null && parent != Inspector;
                    parent = VisualTreeHelper.GetParent(parent))
                    if (parent is FontIcon) return false;
                return true;
            });
            if (labels.Any(label => Math.Abs(label.FontSize - 13) > .01))
                throw new InvalidOperationException("Die Seitenleiste verwendet unterschiedliche Schriftgrößen.");
            var diffCounts = Descendants(ChangesSummaryButton).OfType<TextBlock>()
                .Where(label => label.Text.Length > 1 && label.Text[0] is '+' or '−').ToArray();
            if (diffCounts.Length != 2 || diffCounts.Count(label => label.Text[0] == '+') != 1 || diffCounts.Count(label => label.Text[0] == '−') != 1)
                throw new InvalidOperationException("Die Änderungsübersicht muss genau einen grünen Zuwachs- und einen roten Löschzähler enthalten.");
            foreach (var count in diffCounts)
            {
                var expected = count.Text[0] == '+'
                    ? NativeThemeBrushes.Resource("MissumSuccessBrush", Microsoft.UI.Colors.SeaGreen)
                    : NativeThemeBrushes.Resource("MissumDangerBrush", Microsoft.UI.Colors.Firebrick);
                if (!ReferenceEquals(count.Foreground, expected) || count.Foreground is not SolidColorBrush actual || actual.Color != expected.Color)
                    throw new InvalidOperationException("Die Änderungszahlen verwenden nicht die grünen beziehungsweise roten Git-Diff-Farben.");
            }
            foreach (var label in labels.Where(label => !diffCounts.Contains(label))) VerifySidebarTextBrush(label.Foreground, "Text: " + label.Text);
            foreach (var icon in Descendants(Inspector).OfType<FontIcon>()) VerifySidebarTextBrush(icon.Foreground, "Icon: " + icon.Glyph);
            foreach (var ring in Descendants(InspectorWorkingPlanHost).OfType<ProgressRing>())
                VerifySidebarTextBrush(ring.Foreground);
            foreach (var outline in Descendants(InspectorWorkingPlanHost).OfType<Microsoft.UI.Xaml.Shapes.Ellipse>()
                .Where(outline => VisualTreeHelper.GetParent(outline) is Grid parent && parent.Children.OfType<ProgressRing>().Any()))
                VerifySidebarTextBrush(outline.Stroke);
            FrameworkElement[] icons = [Descendants(InspectorProjectRow).OfType<FontIcon>().Single(),
                Descendants(ChangesSummaryButton).OfType<Microsoft.UI.Xaml.Shapes.Path>().Single(), InspectorContextIcon];
            if (icons.Any(icon => Math.Abs(icon.ActualWidth - 16) > .01 || Math.Abs(icon.ActualHeight - 16) > .01)
                || SourcesSummaryIcon.Width != 16 || SourcesSummaryIcon.Height != 16)
                throw new InvalidOperationException("Die Seitenleisten-Icons teilen nicht dieselbe Größe von 16 × 16.");
            if (InspectorContextIcon.Foreground is not SolidColorBrush contextBrush || contextBrush.Color != NativeThemeBrushes.Text.Color)
                throw new InvalidOperationException("Das Kontext-Icon verwendet nicht die normale Schriftfarbe.");
            var changesIcon = Descendants(ChangesSummaryButton).OfType<Microsoft.UI.Xaml.Shapes.Path>().Single();
            var changesTitle = Descendants(ChangesSummaryButton).OfType<TextBlock>().Single(label => label.Text == "Änderungen");
            if (changesIcon.Stroke is not SolidColorBrush changesBrush || changesTitle.Foreground is not SolidColorBrush titleBrush
                || changesBrush.Color != titleBrush.Color || changesBrush.Color != NativeThemeBrushes.Text.Color)
                throw new InvalidOperationException("Das Änderungen-Icon verwendet nicht dieselbe normale Schriftfarbe wie sein Titel.");
            if (_inspectorWorkingPlanHeader?.Content is not TextBlock { Text: "Arbeitsplan" } heading
                || heading.Foreground is not SolidColorBrush headingBrush || headingBrush.Color != titleBrush.Color
                || Descendants(_inspectorWorkingPlanHeader!).Any(element => element is FontIcon or Microsoft.UI.Xaml.Shapes.Path or Viewbox))
                throw new InvalidOperationException("Arbeitsplan ist keine reine Gruppenüberschrift in derselben Schriftfarbe wie Änderungen.");
            var criteriaHeading = Descendants(InspectorWorkingPlanHost).OfType<TextBlock>().Single(label => label.Text == "Annahmekriterien");
            foreach (var groupHeading in new[] { heading, criteriaHeading })
            {
                if (groupHeading.FontSize != SubagentsHeading.FontSize || groupHeading.FontWeight.Weight != SubagentsHeading.FontWeight.Weight
                    || groupHeading.Padding != SubagentsHeading.Padding || groupHeading.MinHeight != SubagentsHeading.MinHeight
                    || groupHeading.Foreground is not SolidColorBrush groupBrush || SubagentsHeading.Foreground is not SolidColorBrush subagentsBrush
                    || groupBrush.Color != subagentsBrush.Color)
                    throw new InvalidOperationException("Die Gruppenüberschriften müssen wie Subagenten formatiert sein.");
            }
        }

        void InvokeSidebarToggle()
        {
            if (new ButtonAutomationPeer(InspectorToggle).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der Ausgaben-Button bietet keine native Klickinteraktion.");
            invoke.Invoke();
        }
    }

    private static void VerifySidebarTextBrush(Brush? brush, string element = "")
    {
        if (brush is not SolidColorBrush color || color.Color != NativeThemeBrushes.Text.Color)
            throw new InvalidOperationException("Die Ausgaben-Seitenleiste muss außer den Planetenicons und Git-Diff-Zählern die normale Schriftfarbe verwenden. "
                + element + ": " + (brush is SolidColorBrush actual ? actual.Color.ToString(System.Globalization.CultureInfo.InvariantCulture) : brush?.GetType().Name)
                + "; erwartet: " + NativeThemeBrushes.Text.Color.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
