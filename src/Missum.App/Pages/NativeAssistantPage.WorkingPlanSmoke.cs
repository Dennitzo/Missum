using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static readonly string[] WorkingPlanSmokeThemes = ["dark", "light"];

    private async Task VerifyWorkingPlanSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Der Arbeitsplan-Test benötigt ein isoliertes Profil ohne Modellstart.");
        var chats = App.Current.GetService<IChatRepository>();
        var settings = _settings.Current;
        var originalSession = _session;
        var originalFixture = _selectionInputSmokeActive;
        var app = App.Current;
        var originalAccent = app.AccentColor;
        var originalBackground = app.BackgroundColor;
        var originalWindowSize = app.MainWindow?.AppWindow.Size;
        var originalTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme == ElementTheme.Light ? AppTheme.Light : AppTheme.Dark;
        var fixtureIds = new List<Guid>();
        var animationPlayerObserved = false;
        _selectionInputSmokeActive = true;
        try
        {
            await _settings.UpdateAsync(value => value with { IsAutomaticSpeechEnabled = false });
            app.MainWindow?.AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1000));
            await WaitForNativePreviewFrameAsync();
            await SetInspectorVisibleAsync(true);
            var fixture = await Task.Run(async () =>
            {
                var session = await chats.CreateSessionAsync("Arbeitsplan · vollständige Etappen", ChatMode.Coding, _lifetime.Token);
                fixtureIds.Add(session.Id);
                var empty = await chats.CreateSessionAsync("Noch ohne Arbeitsplan", ChatMode.Coding, _lifetime.Token);
                fixtureIds.Add(empty.Id);
                var science = await chats.CreateSessionAsync("Science ohne Arbeitsplan", ChatMode.ClaudeScience, _lifetime.Token);
                fixtureIds.Add(science.Id);
                var general = await chats.CreateSessionAsync("Chat ohne Arbeitsplan", ChatMode.General, _lifetime.Token);
                fixtureIds.Add(general.Id);
                var first = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Ich prüfe die Herleitung in einzelnen Etappen.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var steps = Enumerable.Range(1, 7).Select(index => new NativeWorkingPlanProjection.Item("s" + index,
                    index switch
                    {
                        1 => "Voraussetzungen und Einheiten kontrollieren",
                        2 => "Herleitung unabhängig nachrechnen",
                        3 => "Grenzfälle und offene Annahmen überprüfen",
                        4 => "Numerische Ergebnisse mit der Herleitung vergleichen",
                        5 => "Aussagekräftige Plots erstellen und erläutern",
                        6 => "Publikation und Literaturverweise einarbeiten",
                        _ => "Ergebnisse und verbleibende Grenzen dokumentieren",
                    }, index == 1 ? "completed" : index == 2 ? "in_progress" : "pending")).ToArray();
                NativeWorkingPlanProjection.Item[] criteria =
                [
                    new("a1", "Voraussetzungen sind ausdrücklich dokumentiert", "completed"),
                    new("a2", "Unabhängige Gegenprüfung stimmt innerhalb der begründeten Genauigkeit überein", "pending"),
                ];
                var firstStep = new AssistantToolStep("plan-first", "coding.updatePlan", "completed",
                    InputJson: JsonSerializer.Serialize(new { steps, acceptanceCriteria = criteria }, JsonOptions),
                    OutputJson: JsonSerializer.Serialize(new { success = true, storedSteps = steps, storedAcceptanceCriteria = criteria }, JsonOptions),
                    UpdatedAt: DateTimeOffset.UtcNow);
                await chats.SaveToolStepAsync(first.Id, firstStep, _lifetime.Token);
                for (var index = 0; index < 30; index++)
                    await chats.AddMessageAsync(session.Id, ChatRole.User, "Gespeicherter Verlauf " + index,
                        MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var last = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
                    "Die Voraussetzungen sind geprüft. Als Nächstes rechne ich die Herleitung unabhängig nach.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                return new { Session = session, Empty = empty, Science = science, General = general,
                    First = first, Last = last, Steps = steps, Criteria = criteria, FirstStep = firstStep };
            }, _lifetime.Token);

            foreach (var emptySession in new[] { fixture.Empty, fixture.Science })
            {
                await OpenSessionAsync(emptySession.Id);
                AssertTabAbsent();
                if (InspectorWorkingPlanHost.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Ohne Plan darf keine Arbeitsplan-Gruppe erscheinen.");
                AssertAnimation(false);
            }
            await OpenSessionAsync(fixture.General.Id);
            AssertTabAbsent();
            await OpenSessionAsync(fixture.Session.Id);
            if (_messages.ContainsKey(fixture.First.Id.ToString()) || !_historyHasMore)
                throw new InvalidOperationException("Der gespeicherte Plan liegt nicht außerhalb des geladenen Verlaufs.");
            await ClickWorkingPlanAsync();
            AssertPlan(fixture.Steps, fixture.Criteria);
            AssertInspectorPlan(fixture.Steps, fixture.Criteria);
            AssertAnimation(false);
            ApplyEvent("chat.started", JsonSerializer.SerializeToElement(new { sessionId = fixture.Session.Id }, JsonOptions));
            await AssertAnimationAsync(true);
            ShowChatView(); RenderSessionTabs(); UpdateLayout();
            AssertAnimation(false);
            if (Descendants(BodyGrid).Any(element => ReferenceEquals(element, _workingPlanView)))
                throw new InvalidOperationException("Ein Arbeitsplan-Fenster liegt weiterhin über dem Promptfenster.");
            await ClickWorkingPlanAsync();
            await AssertAnimationAsync(true);
            foreach (var terminal in new[] { "Abgebrochen", "Fehlgeschlagen", "Fertig" })
            {
                ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = fixture.Session.Id, runStatus = terminal }, JsonOptions));
                AssertAnimation(false);
                ApplyEvent("workingPlan.changed", JsonSerializer.SerializeToElement(new { sessionId = fixture.Session.Id,
                    workingPlan = AssistantCoordinator.WorkingPlanDto(fixture.FirstStep) }, JsonOptions));
                AssertAnimation(false);
                ApplyEvent("chat.started", JsonSerializer.SerializeToElement(new { sessionId = fixture.Session.Id }, JsonOptions));
                await AssertAnimationAsync(true);
            }
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = fixture.Session.Id, runStatus = "Abgebrochen" }, JsonOptions));
            AssertAnimation(false);

            var updatedSteps = fixture.Steps.Select(item => item.Id == "s2" ? item with { Status = "completed" }
                : item.Id == "s3" ? item with { Status = "in_progress" } : item)
                .Append(new("s8", "Unabhängige Gegenprüfung integrieren", "pending")).ToArray();
            var updatedCriteria = fixture.Criteria.Append(new("a3", "Verbleibende Grenzen sind in der Publikation ausgewiesen", "pending")).ToArray();
            var updatedStep = fixture.FirstStep with
            {
                Id = "plan-partial", UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1),
                InputJson = "{\"steps\":[{\"id\":\"s2\",\"status\":\"completed\"}]}",
                OutputJson = JsonSerializer.Serialize(new { success = true, storedSteps = updatedSteps, storedAcceptanceCriteria = updatedCriteria }, JsonOptions),
            };
            await chats.SaveToolStepAsync(fixture.Last.Id, updatedStep, _lifetime.Token);
            await _coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, fixture.Last,
                    ToolStep: updatedStep, Status: "Arbeitsplan aktualisiert"),
                (type, data, id) => EmitAsync(type, data, id), "working-plan-smoke");
            AssertPlan(updatedSteps, updatedCriteria);
            AssertInspectorPlan(updatedSteps, updatedCriteria);
            var retainedRow = Descendants(_workingPlanView).OfType<TextBlock>().Single(text => text.Text == updatedSteps[0].Title);
            ApplyEvent("workingPlan.changed", JsonSerializer.SerializeToElement(new { sessionId = Guid.NewGuid(),
                workingPlan = AssistantCoordinator.WorkingPlanDto(fixture.FirstStep) }, JsonOptions));
            ApplyEvent("workingPlan.changed", JsonSerializer.SerializeToElement(new { sessionId = fixture.Session.Id,
                workingPlan = AssistantCoordinator.WorkingPlanDto(fixture.FirstStep) }, JsonOptions));
            AssertPlan(updatedSteps, updatedCriteria);
            if (!ReferenceEquals(retainedRow, Descendants(_workingPlanView).OfType<TextBlock>().Single(text => text.Text == updatedSteps[0].Title)))
                throw new InvalidOperationException("Eine veraltete oder fremde Meldung hat den aktuellen Arbeitsplan ersetzt.");

            ShowChatView();
            await LoadEarlierMessagesAsync();
            if (!_messages.ContainsKey(fixture.First.Id.ToString()))
                throw new InvalidOperationException("Der historische Arbeitsplanschritt konnte nicht per Lazy Load geladen werden.");
            var toolTabsBefore = _toolTabs.Count;
            foreach (var receipt in new[] { (fixture.First.Id, fixture.FirstStep.Id), (fixture.Last.Id, updatedStep.Id),
                (fixture.First.Id, fixture.FirstStep.Id) })
            {
                await OpenToolTabAsync(receipt.Item1.ToString(), receipt.Item2);
                AssertPlan(updatedSteps, updatedCriteria);
                if (_toolTabs.Count != toolTabsBefore || _activeToolTab is not null)
                    throw new InvalidOperationException("Ein Arbeitsplanschritt hat einen zusätzlichen Werkzeug-Tab statt des gemeinsamen Arbeitsplans geöffnet.");
                AssertTabPresent();
                ShowChatView();
            }
            await ClickWorkingPlanAsync();
            AssertPlan(updatedSteps, updatedCriteria);
            if (SessionTabsPanel.Children.Count(element => ReferenceEquals(element, _workingPlanTabContainer)) != 1)
                throw new InvalidOperationException("Der temporäre Arbeitsplan-Tab wurde dupliziert.");
            await ClosePlanTabAsync();
            ApplyEvent("workingPlan.changed", JsonSerializer.SerializeToElement(new { sessionId = fixture.Session.Id,
                workingPlan = AssistantCoordinator.WorkingPlanDto(updatedStep) }, JsonOptions));
            AssertTabAbsent();
            AssertInspectorPlan(updatedSteps, updatedCriteria);
            await ClickWorkingPlanAsync();
            AssertPlan(updatedSteps, updatedCriteria);

            var childId = "working-plan-smoke:" + Guid.NewGuid().ToString("N");
            var childMessage = Guid.NewGuid().ToString();
            NativeWorkingPlanProjection.Item[] childSteps = [new("child1", "Unabhängigen Grenzfall prüfen", "in_progress")];
            var childStep = new AssistantToolStep("child-plan", "coding.updatePlan", "completed",
                OutputJson: JsonSerializer.Serialize(new { storedSteps = childSteps, storedAcceptanceCriteria = Array.Empty<object>() }, JsonOptions),
                UpdatedAt: DateTimeOffset.UtcNow.AddSeconds(2));
            ObserveSubagentSnapshot(JsonSerializer.SerializeToElement(new
            {
                sessionId = fixture.Session.Id, agentId = childId, parentMessageId = fixture.Last.Id, runId = Guid.NewGuid(),
                title = "Unabhängige Gegenprüfung", status = "running", isRunning = true,
                messages = new[] { new { id = childMessage, role = "assistant", content = "", status = "streaming",
                    createdAt = DateTimeOffset.UtcNow, toolSteps = new[] { childStep } } },
            }, JsonOptions));
            await OpenToolTabAsync(childMessage, childStep.Id);
            AssertPlan(childSteps, []);
            AssertInspectorPlan(updatedSteps, updatedCriteria);
            await AssertAnimationAsync(true);
            if (_toolTabs.Count != toolTabsBefore || _workingPlanAgentId != childId || _running)
                throw new InvalidOperationException("Der Subagentenplan hat keinen eigenen Plan- und Laufbezug.");
            ShowChatView();
            AssertAnimation(false);
            await OpenWorkingPlanTabAsync();
            AssertPlan(updatedSteps, updatedCriteria);
            AssertAnimation(false);

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                app.ApplyTheme(theme);
                app.ApplyBackgroundColor(theme == AppTheme.Dark ? "#202027" : "#FFFFFF");
                app.ApplyAccentColor(theme == AppTheme.Dark ? "#E9B557" : "#3267B2");
                UpdateLayout();
                await WaitForNativePreviewFrameAsync();
                if (!Descendants(_workingPlanView).OfType<Border>().Any(border =>
                        ReferenceEquals(border.Background, NativeThemeBrushes.Resource("MissumLayerBrush", 27))))
                    throw new InvalidOperationException("Der Arbeitsplan folgt nicht der Missum-Hintergrundfarbe.");
                AssertSingleScroll();
                await SaveMathPreviewAsync(LayoutRoot, "native-working-plan-" + theme.ToString().ToLowerInvariant() + ".png");
            }
            await OpenSessionAsync(fixture.Empty.Id);
            AssertTabAbsent();
            if (InspectorWorkingPlanHost.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Eine andere Sitzung hat den vorherigen Arbeitsplan übernommen.");
            AssertAnimation(false);
            await OpenSessionAsync(fixture.Session.Id);
            await ClickWorkingPlanAsync();
            AssertPlan(updatedSteps, updatedCriteria);
            AssertAnimation(false);
            if (_messages.ContainsKey(fixture.First.Id.ToString()) || !_historyHasMore)
                throw new InvalidOperationException("Das Wiederöffnen des Plan-Tabs lädt unnötig den gesamten Chatverlauf.");
            foreach (var stored in new[] { (fixture.First.Id, fixture.FirstStep), (fixture.Last.Id, updatedStep) })
            {
                var durable = await chats.GetMessageAsync(stored.Item1, _lifetime.Token);
                if (durable?.ToolSteps?.Single(step => step.Id == stored.Item2.Id).OutputJson != stored.Item2.OutputJson)
                    throw new InvalidOperationException("Tabwechsel oder Animation haben die gespeicherten Etappenstatus geändert.");
            }
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-working-plan-validation.json"),
                JsonSerializer.Serialize(new { passed = true, planOutsideLoadedHistory = true, fullPlanSteps = updatedSteps.Length,
                    fullAcceptanceCriteria = updatedCriteria.Length, partialUpdateRetainsAllSteps = true,
                    staleAndForeignEventsIgnored = true, retainedRows = true, temporaryClosablePlanTab = true,
                    closedTabStaysClosedOnUpdate = true, sidebarReopensPlan = true, automaticSidebarUpdates = true,
                    noEmptyCodingScienceOrGeneralTabs = true, composerPlanRemoved = true,
                    historicalReceiptsOpenLatestPlan = true, noDuplicateToolTabs = true, singleNativeScroll = true,
                    activeRing = true, animationPlayerObserved, hiddenTabStopsAnimation = true, idlePlanNotAnimating = true,
                    terminalStatesStopAnimation = true, resumedRunRestartsAnimation = true,
                    latePlanReceiptDoesNotRestartAnimation = true, persistedStatusesUnchanged = true,
                    themes = WorkingPlanSmokeThemes, sessionIsolation = true, subagentIsolation = true,
                    durablePlanRestored = true, modelRequests = 0 }));
        }
        finally
        {
            app.ApplyTheme(originalTheme); app.ApplyBackgroundColor(originalBackground); app.ApplyAccentColor(originalAccent);
            if (originalWindowSize is { } size) app.MainWindow?.AppWindow.Resize(size);
            await _settings.UpdateAsync(value => value with { IsAutomaticSpeechEnabled = settings.IsAutomaticSpeechEnabled });
            await SetInspectorVisibleAsync(settings.AssistantOutputsVisible);
            ShowChatView(); ShowParentConversation();
            await CommandAsync("session.open", new { sessionId = originalSession });
            foreach (var id in fixtureIds) await chats.DeleteSessionAsync(id, _lifetime.Token);
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            _selectionInputSmokeActive = originalFixture;
        }

        async Task OpenSessionAsync(Guid id)
        {
            if (!await CommandAsync("session.open", new { sessionId = id }))
                throw new InvalidOperationException("Die gespeicherte Arbeitsplan-Test-Sitzung konnte nicht geöffnet werden.");
            ShowChatView(); RenderSessionTabs(); RenderMessagesNow(); UpdateLayout();
            await WaitForNativePreviewFrameAsync();
        }

        void AssertTabPresent()
        {
            if (_workingPlanTabButton is null || _workingPlanTabContainer is null || !SessionTabsPanel.Children.Contains(_workingPlanTabContainer)
                || !Descendants(_workingPlanTabButton).OfType<TextBlock>().Any(text => text.Text == "Arbeitsplan")
                || !Descendants(_workingPlanTabButton).OfType<Microsoft.UI.Xaml.Shapes.Path>().Any()
                || _workingPlanTabCloseButton is null || !Descendants(_workingPlanTabContainer).Contains(_workingPlanTabCloseButton))
                throw new InvalidOperationException("Der temporäre schließbare Arbeitsplan-Tab mit Notizzettel-Icon fehlt.");
            UpdateLayout();
            if (_workingPlanTabButton.Content is not StackPanel row || row.Children[0] is not Viewbox icon
                || Math.Abs(icon.ActualWidth - 13) > .5 || Math.Abs(icon.ActualHeight - 13) > .5)
                throw new InvalidOperationException("Das Notizzettel-Icon ist nicht auf die gemeinsame Tabgröße skaliert.");
            var bounds = icon.TransformToVisual(_workingPlanTabButton).TransformBounds(new(0, 0, icon.ActualWidth, icon.ActualHeight));
            if (Math.Abs(bounds.Y + bounds.Height / 2 - _workingPlanTabButton.ActualHeight / 2) > 1
                || bounds.Y < 0 || bounds.Bottom > _workingPlanTabButton.ActualHeight)
                throw new InvalidOperationException("Das Notizzettel-Icon ist nicht vertikal im Tab zentriert.");
        }

        async Task ClickWorkingPlanAsync()
        {
            var button = HasWorkingPlanTab ? _workingPlanTabButton : _inspectorWorkingPlanHeader;
            if (button is null || new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der Arbeitsplan-Tab ist nicht per Klick bedienbar.");
            invoke.Invoke();
            await WaitForPagingSmokeAsync(() => _activeWorkingPlanSession == _session && _workingPlanHost.Visibility == Visibility.Visible,
                "Der Klick hat den temporären Arbeitsplan-Tab nicht geöffnet.");
            UpdateLayout(); await WaitForNativePreviewFrameAsync();
            AssertTabPresent();
        }

        void AssertTabAbsent()
        {
            if (HasWorkingPlanTab || _workingPlanTabContainer is not null && SessionTabsPanel.Children.Contains(_workingPlanTabContainer))
                throw new InvalidOperationException("Ein geschlossener oder noch nicht geöffneter Arbeitsplan-Tab wird automatisch angezeigt.");
        }

        async Task ClosePlanTabAsync()
        {
            AssertTabPresent();
            if (new ButtonAutomationPeer(_workingPlanTabCloseButton!).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der Arbeitsplan-Tab hat keinen bedienbaren Schließen-Button.");
            invoke.Invoke();
            await WaitForPagingSmokeAsync(() => !HasWorkingPlanTab && _workingPlanHost.Visibility == Visibility.Collapsed,
                "Der Arbeitsplan-Tab wurde nicht geschlossen.");
            UpdateLayout(); AssertTabAbsent(); AssertAnimation(false);
        }

        void AssertInspectorPlan(IReadOnlyList<NativeWorkingPlanProjection.Item> steps, IReadOnlyList<NativeWorkingPlanProjection.Item> criteria)
        {
            UpdateLayout();
            if (InspectorWorkingPlanHost.Visibility != Visibility.Visible || Inspector.Visibility != Visibility.Visible
                || _inspectorWorkingPlanView.StepCount != steps.Count || _inspectorWorkingPlanView.AcceptanceCriteriaCount != criteria.Count
                || !_inspectorWorkingPlanView.VisibleTitles.SequenceEqual(steps.Select(item => item.Title))
                || criteria.Any(item => !Descendants(_inspectorWorkingPlanView).OfType<TextBlock>().Any(text => text.Text == item.Title))
                || Descendants(_inspectorWorkingPlanView).OfType<ScrollViewer>().Any())
                throw new InvalidOperationException("Die Seitenleiste zeigt nicht den vollständigen aktuellen Sitzungsplan ohne innere Scrollfenster.");
        }

        void AssertPlan(IReadOnlyList<NativeWorkingPlanProjection.Item> steps, IReadOnlyList<NativeWorkingPlanProjection.Item> criteria)
        {
            UpdateLayout();
            if (_activeWorkingPlanSession != _session || _workingPlanHost.Visibility != Visibility.Visible
                || BodyGrid.Visibility != Visibility.Collapsed || _workingPlanView.StepCount != steps.Count
                || _workingPlanView.AcceptanceCriteriaCount != criteria.Count
                || !_workingPlanView.VisibleTitles.SequenceEqual(steps.Select(item => item.Title))
                || criteria.Any(item => !Descendants(_workingPlanView).OfType<TextBlock>().Any(text => text.Text == item.Title))
                || Descendants(_workingPlanView).OfType<TextBlock>().Any(text => text.Text.Contains(" · pending", StringComparison.Ordinal)
                    || text.Text.Contains(" · in_progress", StringComparison.Ordinal) || text.Text.Contains(" · completed", StringComparison.Ordinal))
                || Descendants(_workingPlanView).OfType<Button>().Any())
                throw new InvalidOperationException("Der Arbeitsplan zeigt nicht den vollständigen aktuellen Plan mit symbolischem Status.");
            AssertSingleScroll();
        }

        void AssertSingleScroll()
        {
            if (Descendants(_workingPlanView).OfType<ScrollViewer>().Any()
                || !ReferenceEquals(_workingPlanHost.Content, _workingPlanScroll)
                || _workingPlanScroll.ScrollableWidth > .5
                || Math.Abs(_workingPlanScroll.ActualWidth - _workingPlanHost.ActualWidth) > 2)
                throw new InvalidOperationException("Der Arbeitsplan muss den nativen Tab ohne innere Scrollfenster ausfüllen.");
        }

        void AssertAnimation(bool active)
        {
            if (Descendants(_workingPlanView).OfType<ProgressRing>().Count(ring => ring.IsActive) != (active ? 1 : 0))
                throw new InvalidOperationException("Die Arbeitsplan-Animation entspricht nicht dem sichtbaren aktiven Lauf.");
        }

        async Task AssertAnimationAsync(bool active)
        {
            UpdateLayout();
            await WaitForPagingSmokeAsync(() => Descendants(_workingPlanView).OfType<ProgressRing>().Count(ring => ring.IsActive) == (active ? 1 : 0),
                "Die Arbeitsplan-Ladeschnecke wurde nicht passend zum Lauf und sichtbaren Tab animiert.");
            if (active)
            {
                var players = Descendants(_workingPlanView).OfType<AnimatedVisualPlayer>().ToArray();
                if (players.Length > 0)
                {
                    await WaitForPagingSmokeAsync(() => players.Any(player => player.IsPlaying),
                        "Die aktive Arbeitsplan-Ladeschnecke spielt ihre Animation nicht ab.");
                    animationPlayerObserved = true;
                }
            }
        }
    }
}
