using System.Text.Json;
using System.Globalization;
using Missum.App.Controls;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // This fixture runs only in the separate portable smoke profile. It changes
    // the live theme without saving settings or dispatching an assistant task.
    private async Task<int> VerifyIconPaletteSmokeAsync()
    {
        if (!IsMessageFooterSmoke)
            throw new InvalidOperationException("Die Iconfarben-Prüfung benötigt ein isoliertes Smoke-Profil.");

        var app = (App)Application.Current;
        var original = _snapshot.Clone();
        var originalTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme switch
        {
            ElementTheme.Light => AppTheme.Light,
            ElementTheme.Dark => AppTheme.Dark,
            _ => AppTheme.System,
        };
        var originalAccent = app.AccentColor;
        var originalBackground = app.BackgroundColor;
        var originalInspector = Inspector.Visibility;
        var originalFixture = _selectionInputSmokeActive;
        var originalChildren = _subagents.ToArray();
        var originalReasoningModel = _reasoningModel;
        var session = Guid.NewGuid();
        var answerId = Guid.NewGuid().ToString();
        var childId = "icon-palette-smoke:" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var completeRead = new AssistantToolStep("palette-read", "coding.read", "completed",
            InputJson: JsonSerializer.Serialize(new { path = "palette.cs", startLine = 1 }),
            OutputJson: JsonSerializer.Serialize(new { path = "palette.cs", startLine = 1, totalLines = 500,
                content = "var accent = settings.AccentColor;\nApplyReadableIcons(accent);", truncated = true }), ContentOffset: 0);
        var deferredRead = completeRead with { DisplayHeader = AssistantToolStepHeaders.Create(completeRead),
            InputJson = null, OutputJson = null, DetailsDeferred = true };
        var results = new List<object>();
        _selectionInputSmokeActive = true;
        _draftTimer.Stop();
        try
        {
            var fixture = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            fixture["activeSessionId"] = JsonSerializer.SerializeToElement(session);
            fixture["chatMode"] = JsonSerializer.SerializeToElement("coding");
            fixture["isRunning"] = JsonSerializer.SerializeToElement(false);
            fixture["runId"] = JsonSerializer.SerializeToElement((string?)null);
            fixture["runMessageId"] = JsonSerializer.SerializeToElement((string?)null);
            fixture["workspacePath"] = JsonSerializer.SerializeToElement(System.IO.Path.Combine(App.Current.DataDirectory, "Farbkonzept"));
            fixture["draft"] = JsonSerializer.SerializeToElement("Prüfe anschließend die Annahmen.");
            fixture["contextUsed"] = JsonSerializer.SerializeToElement(8192);
            fixture["contextLimit"] = JsonSerializer.SerializeToElement(131072);
            fixture["reasoningModelId"] = JsonSerializer.SerializeToElement("DeepSeek");
            fixture["selectedExtensionActionId"] = JsonSerializer.SerializeToElement((string?)null);
            fixture["attachments"] = fixture["documents"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            fixture["historyPage"] = JsonSerializer.SerializeToElement(new { hasMore = false });
            fixture["sessions"] = JsonSerializer.SerializeToElement(new[]
            {
                new { id = session, title = "Einheitliches Farbkonzept", chatMode = "coding", updatedAt = now },
            });
            fixture["sessionGroups"] = JsonSerializer.SerializeToElement(new[]
            {
                new { id = Guid.NewGuid(), name = "Farbkonzept", workspacePath = System.IO.Path.Combine(App.Current.DataDirectory, "Farbkonzept"),
                    chatMode = "coding", isCollapsed = false, sessionIds = new[] { session } },
            });
            fixture["messages"] = JsonSerializer.SerializeToElement(new object[]
            {
                new { id = Guid.NewGuid(), sessionId = session, role = "user", status = "completed",
                    content = "Prüfe die Darstellung und die Lesbarkeit der Werkzeuge.", createdAt = now.AddSeconds(-2), updatedAt = now.AddSeconds(-2) },
                new { id = answerId, sessionId = session, role = "assistant", status = "completed",
                    content = "Die Oberfläche verwendet eine abgestimmte Themenpalette. Jede Werkzeuggruppe besitzt einen eigenen Farbton, passend zur eingestellten Akzentfarbe.",
                    createdAt = now.AddSeconds(-1), updatedAt = now,
                    toolSteps = new object[]
                    {
                        new { id = "palette-search", tool = "web.search", status = "completed", contentOffset = 0,
                            inputJson = JsonSerializer.Serialize(new { query = "Lesbare Benutzeroberflächen" }),
                            outputJson = JsonSerializer.Serialize(new { results = new[] { new { title = "Kontrast und Lesbarkeit", url = "https://example.org/contrast" } } }) },
                        deferredRead,
                    } },
            }, JsonOptions);
            fixture["subagents"] = JsonSerializer.SerializeToElement(new[]
            {
                new { sessionId = session, parentMessageId = answerId, agentId = childId, runId = Guid.NewGuid().ToString(),
                    title = "Darstellung prüfen", status = "completed", isRunning = false, createdAt = now, messages = Array.Empty<object>() },
            });
            fixture.Remove("changesSummary");
            _reasoningModel = "coding:" + _settings.Current.SelectedModel;
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(fixture));
            ShowChatView();
            RenderMessagesNow();
            RenderChanges(JsonSerializer.SerializeToElement(new
            {
                sessionId = session, messageId = answerId, runId = Guid.NewGuid(), revision = 1,
                workspacePath = System.IO.Path.Combine(App.Current.DataDirectory, "Farbkonzept"),
                files = new[] { new { path = "palette.cs", addedLines = 2, removedLines = 1, isBinary = false } },
            }));
            _subagents[childId].TabOpen = true;
            _sourceTabs.Add(_session);
            RenderSessionTabs();
            Inspector.Visibility = Visibility.Visible;
            SelectedToolChip.SetTool("Planen", "\uEA80", NativeIconPalette.BrushFor("plan"));
            SelectedToolChip.Visibility = Visibility.Visible;
            var tool = (ToolStepView)_messageBlocks[answerId]["tool:palette-read"];
            tool.SetExpanded(false);
            string ToolTitle() => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Descendants(tool).OfType<Button>().First())
                .Replace(", eingeklappt", "", StringComparison.Ordinal).Replace(", ausgeklappt", "", StringComparison.Ordinal);
            var initialTitle = ToolTitle();
            if (!initialTitle.Contains("palette.cs · 2 Zeilen gelesen (1–2) · Ausgabe gekürzt", StringComparison.Ordinal)
                || Descendants(tool).OfType<NativeToolResultView>().Any())
                throw new InvalidOperationException("Der eingeklappte Header benötigt Titelmetadaten, aber keine geladenen Werkzeugdetails.");
            tool.Update(JsonSerializer.SerializeToElement(completeRead, JsonOptions));
            tool.SetExpanded(true);
            if (ToolTitle() != initialTitle)
                throw new InvalidOperationException("Das Laden der Details hat den Werkzeugtitel verändert.");
            tool.SetExpanded(false);
            if (ToolTitle() != initialTitle)
                throw new InvalidOperationException("Einklappen hat die vollständigen Titelinformationen entfernt.");
            var groups = new[]
            {
                (Role: "navigation", Label: "Steuerung", Glyph: "\uE700"),
                (Role: "folder", Label: "Projekte", Glyph: "\uE8B7"),
                (Role: "web", Label: "Websuche", Glyph: "\uE774"),
                (Role: "research", Label: "Forschung und Denken", Glyph: "\uE721"),
                (Role: "code", Label: "Code und Mathematik", Glyph: "\uE943"),
                (Role: "speech", Label: "Sprache und Audio", Glyph: "\uE767"),
                (Role: "image", Label: "Bilder und Video", Glyph: "\uEB9F"),
                (Role: "document", Label: "Dokumente und Planung", Glyph: "\uE8A5"),
            };
            var groupOverview = new StackPanel { Spacing = 12 };
            groupOverview.Children.Add(new TextBlock
            {
                Text = "Icongruppen", FontSize = 13, Foreground = NativeThemeBrushes.MutedText,
            });
            var groupIcons = new List<(FontIcon Icon, Brush Brush, string Role)>();
            foreach (var group in groups)
            {
                var icon = new FontIcon { Glyph = group.Glyph, FontSize = 19, Foreground = NativeIconPalette.BrushFor(group.Role) };
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(icon);
                var label = new TextBlock { Text = group.Label, FontSize = 13, Foreground = NativeThemeBrushes.Text,
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                groupOverview.Children.Add(row);
                groupIcons.Add((icon, icon.Foreground, group.Role));
            }
            ProjectsPanel.Children.Add(new Border { Child = groupOverview, Padding = new Thickness(10), Margin = new Thickness(0, 18, 0, 0),
                Background = NativeThemeBrushes.Resource("MissumLayerStrongBrush", 32), CornerRadius = new CornerRadius(12) });
            UpdateLayout();

            var planet = Descendants(SubagentsPanel).OfType<NativeSubagentAvatar>().First();
            var planetPaint = IconSmokePlanetPaint(planet);
            var toolIcon = Descendants(tool).OfType<FontIcon>().First(icon => icon.Glyph != "\uE76C");
            var folderIcon = Descendants(ProjectsPanel).OfType<FontIcon>().First(icon => icon.Glyph == "\uE8B7");
            var footerIcon = Descendants(_messageActionViews[answerId].Read).OfType<FontIcon>().First();
            var chipIcon = Descendants(SelectedToolChip).OfType<FontIcon>().First(icon => icon.Glyph == "\uEA80");
            var tracked = new[]
            {
                (Icon: DictationIcon, Brush: DictationIcon.Foreground, Role: "speech"),
                (Icon: ComposerToolsIcon, Brush: ComposerToolsIcon.Foreground, Role: "speech"),
                (Icon: folderIcon, Brush: folderIcon.Foreground, Role: "folder"),
                (Icon: toolIcon, Brush: toolIcon.Foreground, Role: "code"),
                (Icon: footerIcon, Brush: footerIcon.Foreground, Role: "speech"),
                (Icon: chipIcon, Brush: chipIcon.Foreground, Role: "plan"),
            }.Concat(groupIcons).ToArray();
            var variants = new[]
            {
                (Theme: AppTheme.Light, Accent: "#A970FF", Background: "#181818", File: "native-icons-light-purple.png"),
                (Theme: AppTheme.Dark, Accent: "#A970FF", Background: "#181818", File: "native-icons-dark-purple.png"),
                (Theme: AppTheme.Light, Accent: NativeIconPalette.CyberpunkAccent, Background: "#181818", File: "native-icons-light-cyberpunk.png"),
                (Theme: AppTheme.Dark, Accent: NativeIconPalette.CyberpunkAccent, Background: "#181818", File: "native-icons-dark-cyberpunk.png"),
                (Theme: AppTheme.Light, Accent: "#147D92", Background: "#181818", File: "native-icons-light-teal.png"),
                (Theme: AppTheme.Dark, Accent: "#147D92", Background: "#FFFFFF", File: "native-icons-dark-teal.png"),
                (Theme: AppTheme.Light, Accent: "#FFE56B", Background: "#FFFFFF", File: "native-icons-light-yellow.png"),
            };
            foreach (var variant in variants)
            {
                var previousColors = tracked.Select(item => ((SolidColorBrush)item.Brush).Color).ToArray();
                app.ApplyTheme(variant.Theme);
                app.ApplyBackgroundColor(variant.Background);
                app.ApplyAccentColor(variant.Accent);
                UpdateLayout();
                await WaitForNativePreviewFrameAsync();
                foreach (var item in tracked)
                {
                    if (!ReferenceEquals(item.Brush, item.Icon.Foreground)
                        || !ReferenceEquals(item.Brush, NativeIconPalette.BrushFor(item.Role)))
                        throw new InvalidOperationException("Der Farbwechsel ersetzt einen vorhandenen Icon-Brush: " + item.Role);
                    VerifyIconBrush(item.Icon.Foreground, item.Role);
                }
                if (results.Count > 0 && previousColors.SequenceEqual(tracked.Select(item => ((SolidColorBrush)item.Brush).Color)))
                    throw new InvalidOperationException("Die bestehende Oberfläche hat den Akzent-/Themewechsel nicht übernommen.");
                if (planetPaint != IconSmokePlanetPaint(planet))
                    throw new InvalidOperationException("Ein Akzentwechsel hat die eigenständigen Planetenfarben verändert.");
                var groupColors = groupIcons.Select(item => ((SolidColorBrush)item.Icon.Foreground).Color).ToArray();
                if (groupColors.Distinct().Count() < 7)
                    throw new InvalidOperationException("Die Themenpalette unterscheidet nicht alle sieben Farbgruppen.");
                VerifyIconBrush(((Grid)InspectorProjectRow.Content).Children.OfType<FontIcon>().Single().Foreground, "navigation");
                if (((Grid)ChangesSummaryButton.Content).Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Single().Stroke
                    is not SolidColorBrush changesBrush || changesBrush.Color != NativeThemeBrushes.MutedText.Color)
                    throw new InvalidOperationException("Das Änderungen-Icon muss die gedämpfte Farbe verwenden.");
                if (Descendants(Inspector).OfType<TextBlock>().Any(label => label.Text == "Ausgaben")
                    || Descendants(Inspector).OfType<Button>().Any(button =>
                        Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Ausgaben schließen"))
                    throw new InvalidOperationException("Das Overlay darf keine Überschrift oder eigenes Schließen-Icon mehr enthalten.");
                VerifyIconBrush(InspectorToggleIcon.Stroke, "navigation");
                foreach (var sourceButton in SourcesPanel.Children.OfType<Button>())
                    VerifyIconBrush(((Grid)sourceButton.Content).Children.OfType<FontIcon>().Single().Foreground, "web");
                VerifySourcesSummarySmoke();
                foreach (var tabIcon in Descendants(SessionTabsPanel).OfType<FontIcon>())
                    VerifyIconBrush(tabIcon.Foreground, "navigation");
                if (ActualTheme != (variant.Theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark))
                    throw new InvalidOperationException("Die native Testoberfläche hat das gewählte Theme nicht übernommen.");
                var activeTab = _sessionTabs.Single(tab => tab.Id == _session);
                if (activeTab.Container.BorderThickness != new Thickness(0)
                    || !ReferenceEquals(activeTab.Container.Background, ThemeBrush("MissumSelectionBrush", 46))
                    || activeTab.Container.Background is not SolidColorBrush { Opacity: 1, Color.A: 255 }
                    || !Descendants(ProjectsPanel).OfType<Button>().Any(button => ReferenceEquals(button.Background, activeTab.Container.Background)))
                    throw new InvalidOperationException("Der aktive Tab muss die Sitzungsfläche ohne Rahmen verwenden.");
                if (SendIcon.Foreground is not SolidColorBrush sendForeground || SendButton.Background is not SolidColorBrush sendBackground
                    || App.TextContrastRatio(sendForeground.Color, sendBackground.Color) < 4.5)
                    throw new InvalidOperationException("Das Senden-Icon ist auf seiner Akzentfläche nicht ausreichend lesbar.");
                await SaveMathPreviewAsync(LayoutRoot, variant.File);
                results.Add(new { theme = variant.Theme.ToString(), variant.Accent, variant.Background, screenshot = variant.File,
                    retainedIconBrushes = tracked.Length, actualTheme = ActualTheme.ToString(), planetColorsPreserved = true,
                    groupColors = groupIcons.Select(item => new { group = item.Role,
                        color = ((SolidColorBrush)item.Icon.Foreground).Color.ToString(CultureInfo.InvariantCulture) }).ToArray() });
            }
            fixture["subagents"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(fixture));
            RenderMessagesNow();
            if (SubagentsSection.Visibility != Visibility.Collapsed || SubagentsPanel.Children.Count != 0)
                throw new InvalidOperationException("Der Subagentenbereich muss ohne Aufträge vollständig ausgeblendet sein.");
            OpenSubagentOverview();
            UpdateLayout();
            await WaitForNativePreviewFrameAsync();
            if (_activeSubagentOverviewSession != session || !Descendants(_subagentOverviewHost).OfType<TextBlock>()
                .Any(label => label.Text == "Noch keine Subagenten in dieser Sitzung"))
                throw new InvalidOperationException("Die leere Subagentenübersicht konnte nicht geöffnet werden.");
            ShowChatView();
            _subagentOverviewTabs.Remove(session);
            RenderSessionTabs();
            Inspector.Visibility = Visibility.Visible;
            UpdateLayout();
            await WaitForNativePreviewFrameAsync();
            await SaveMathPreviewAsync(LayoutRoot, "native-empty-subagents.png");
            await File.WriteAllTextAsync(System.IO.Path.Combine(App.Current.DataDirectory, "native-icon-palette-validation.json"),
                JsonSerializer.Serialize(new { passed = true, renderer = "WinUI3", variants = results,
                    collapsedHeaderComplete = true, titleStableAcrossDisclosure = true, tabsBorderless = true,
                    sourcesMatchWebSearch = true, changesUseMutedColor = true, overlayCloseRemoved = true, emptySubagentsHidden = true,
                    composerToolsMatchSpeech = true, tabIconsUniform = true,
                    settingsPersisted = false, modelRequests = 0, clipboardAccess = false, pointerInput = false }, JsonOptions), _lifetime.Token);
            return variants.Length;
        }
        finally
        {
            _draftTimer.Stop();
            _sourceTabs.Remove(session);
            ShowParentConversation();
            foreach (var child in _subagents.Values) SessionTabsPanel.Children.Remove(child.Container);
            _subagents.Clear();
            foreach (var entry in originalChildren) _subagents[entry.Key] = entry.Value;
            _reasoningModel = originalReasoningModel;
            ApplyEvent("state.snapshot", original);
            ShowChatView();
            RenderMessagesNow();
            Inspector.Visibility = originalInspector;
            app.ApplyTheme(originalTheme);
            app.ApplyBackgroundColor(originalBackground);
            app.ApplyAccentColor(originalAccent);
            _selectionInputSmokeActive = originalFixture;
        }
    }

    private static string IconSmokePlanetPaint(NativeSubagentAvatar avatar)
    {
        static string Paint(Brush? brush) => brush switch
        {
            SolidColorBrush solid => solid.Color.ToString(CultureInfo.InvariantCulture),
            GradientBrush gradient => string.Join(",", gradient.GradientStops.Select(stop => stop.Color.ToString(CultureInfo.InvariantCulture))),
            _ => "",
        };
        return avatar.IconColor.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(";", Descendants(avatar).OfType<Shape>()
            .Select(shape => Paint(shape.Fill) + "/" + Paint(shape.Stroke)));
    }
}
