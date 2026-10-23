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
        var originalTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme == ElementTheme.Light ? AppTheme.Light : AppTheme.Dark;
        Guid fixtureId = default, emptyId = default;
        _selectionInputSmokeActive = true;
        try
        {
            await _settings.UpdateAsync(value => value with { ShowWorkingPlan = true, IsAutomaticSpeechEnabled = false });
            var fixture = await Task.Run(async () =>
            {
                var session = await chats.CreateSessionAsync("Arbeitsplan · vollständige Etappen", ChatMode.Coding, _lifetime.Token);
                fixtureId = session.Id;
                var empty = await chats.CreateSessionAsync("Ohne Arbeitsplan", ChatMode.Coding, _lifetime.Token);
                emptyId = empty.Id;
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
                var firstStep = new AssistantToolStep("plan-first", "coding.updatePlan", "completed",
                    InputJson: JsonSerializer.Serialize(new { steps }, JsonOptions),
                    OutputJson: JsonSerializer.Serialize(new { success = true, storedSteps = steps, storedAcceptanceCriteria = Array.Empty<object>() }, JsonOptions),
                    UpdatedAt: DateTimeOffset.UtcNow);
                await chats.SaveToolStepAsync(first.Id, firstStep, _lifetime.Token);
                for (var index = 0; index < 30; index++)
                    await chats.AddMessageAsync(session.Id, ChatRole.User, "Gespeicherter Verlauf " + index,
                        MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var last = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
                    "Die Voraussetzungen sind geprüft. Als Nächstes rechne ich die Herleitung unabhängig nach.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                return new { Session = session, First = first, Last = last, Steps = steps, FirstStep = firstStep };
            }, _lifetime.Token);

            await CommandAsync("session.open", new { sessionId = emptyId });
            if (WorkingPlanView.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Ein leerer Plan wird angezeigt.");
            await CommandAsync("session.open", new { sessionId = fixtureId });
            ShowChatView();
            RenderMessagesNow();
            UpdateLayout();
            await WaitForNativePreviewFrameAsync();
            if (_messages.ContainsKey(fixture.First.Id.ToString()) || !_historyHasMore)
                throw new InvalidOperationException("Die Arbeitsplan-Prüfung hat den Plan nicht außerhalb des geladenen Verlaufs angelegt.");
            AssertPlan(fixture.Steps.Length);
            AssertPlanAnimation(active: false, pendingOutlines: 6);
            ApplyEvent("chat.started", JsonSerializer.SerializeToElement(new { sessionId = fixtureId }, JsonOptions));
            AssertPlanAnimation(active: true, pendingOutlines: 5);
            var expandedHeight = ConversationScroll.ActualHeight;
            InvokeToggle();
            UpdateLayout();
            if (WorkingPlanView.IsExpanded || ConversationScroll.ActualHeight < expandedHeight + 80)
                throw new InvalidOperationException("Das Einklappen vergrößert das Chatfenster nicht.");
            AssertPlanAnimation(active: false, pendingOutlines: 5);
            InvokeToggle();
            UpdateLayout();
            var planTop = WorkingPlanView.TransformToVisual(BodyGrid).TransformPoint(default).Y;
            var chatTop = ConversationScroll.TransformToVisual(BodyGrid).TransformPoint(default).Y;
            var composerTop = ComposerSurface.TransformToVisual(BodyGrid).TransformPoint(default).Y;
            if (chatTop + ConversationScroll.ActualHeight > planTop + 1
                || planTop + WorkingPlanView.ActualHeight > composerTop + 1)
                throw new InvalidOperationException("Arbeitsplan, Chat oder Promptfenster überlagern sich.");
            var rings = Descendants(WorkingPlanView).OfType<ProgressRing>().ToArray();
            if (rings.Count(ring => ring.IsActive) != 1) throw new InvalidOperationException("Die aktive Etappe animiert nicht eindeutig.");
            foreach (var terminal in new[] { "Abgebrochen", "Fehlgeschlagen", "Fertig" })
            {
                ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = fixtureId, runStatus = terminal }, JsonOptions));
                AssertPlanAnimation(active: false, pendingOutlines: 6);
                // A later plan receipt must not restart animation while the session is idle.
                ApplyEvent("workingPlan.changed", JsonSerializer.SerializeToElement(new { sessionId = fixtureId,
                    workingPlan = AssistantCoordinator.WorkingPlanDto(fixture.FirstStep) }, JsonOptions));
                AssertPlanAnimation(active: false, pendingOutlines: 6);
                ApplyEvent("chat.started", JsonSerializer.SerializeToElement(new { sessionId = fixtureId }, JsonOptions));
                AssertPlanAnimation(active: true, pendingOutlines: 5);
            }
            ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = fixtureId, runStatus = "Abgebrochen" }, JsonOptions));
            AssertPlanAnimation(active: false, pendingOutlines: 6);
            var retainedPlan = await chats.GetMessageAsync(fixture.First.Id, _lifetime.Token);
            if (retainedPlan?.ToolSteps?.Single(step => step.Id == fixture.FirstStep.Id).OutputJson != fixture.FirstStep.OutputJson)
                throw new InvalidOperationException("Das Anhalten der Arbeitsplan-Animation hat gespeicherte Etappenstatus geändert.");

            var updatedSteps = fixture.Steps.Select(item => item.Id == "s2" ? item with { Status = "completed" }
                : item.Id == "s3" ? item with { Status = "in_progress" } : item).Append(new("s8", "Unabhängige Gegenprüfung integrieren", "pending")).ToArray();
            var updatedStep = fixture.FirstStep with
            {
                Id = "plan-partial", UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1),
                InputJson = "{\"steps\":[{\"id\":\"s2\",\"status\":\"completed\"}]}",
                OutputJson = JsonSerializer.Serialize(new { success = true, storedSteps = updatedSteps, storedAcceptanceCriteria = Array.Empty<object>() }, JsonOptions),
            };
            await _coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, fixture.Last,
                    ToolStep: updatedStep, Status: "Arbeitsplan aktualisiert"),
                (type, data, id) => EmitAsync(type, data, id), "working-plan-smoke");
            AssertPlan(updatedSteps.Length);
            var retainedRow = Descendants(WorkingPlanView).OfType<TextBlock>().Single(text => text.Text == updatedSteps[0].Title);
            ApplyEvent("workingPlan.changed", JsonSerializer.SerializeToElement(new { sessionId = Guid.NewGuid(),
                workingPlan = AssistantCoordinator.WorkingPlanDto(fixture.FirstStep) }, JsonOptions));
            ApplyEvent("workingPlan.changed", JsonSerializer.SerializeToElement(new { sessionId = fixtureId,
                workingPlan = AssistantCoordinator.WorkingPlanDto(fixture.FirstStep) }, JsonOptions));
            AssertPlan(updatedSteps.Length);
            if (!ReferenceEquals(retainedRow, Descendants(WorkingPlanView).OfType<TextBlock>().Single(text => text.Text == updatedSteps[0].Title)))
                throw new InvalidOperationException("Ein unveränderter Plan erzeugt seine Etappenansicht erneut.");
            await chats.SaveToolStepAsync(fixture.Last.Id, updatedStep, _lifetime.Token);

            await _settings.UpdateAsync(value => value with { ShowWorkingPlan = false });
            await WaitForPagingSmokeAsync(() => WorkingPlanView.Visibility == Visibility.Collapsed, "Die Einstellung blendet den Plan nicht aus.");
            await _settings.UpdateAsync(value => value with { ShowWorkingPlan = true });
            await WaitForPagingSmokeAsync(() => WorkingPlanView.Visibility == Visibility.Visible, "Die Einstellung blendet den vorhandenen Plan nicht wieder ein.");

            var childId = "working-plan-smoke:" + Guid.NewGuid().ToString("N");
            ObserveSubagentSnapshot(JsonSerializer.SerializeToElement(new
            {
                sessionId = fixtureId, agentId = childId, parentMessageId = fixture.Last.Id, runId = Guid.NewGuid(),
                title = "Unabhängige Gegenprüfung", status = "completed", isRunning = false, messages = Array.Empty<object>(),
            }, JsonOptions));
            await ActivateSubagentTabAsync(_subagents[childId]);
            if (WorkingPlanView.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Der Hauptplan erscheint im Subagenten-Chat.");
            ShowParentConversation();
            AssertPlan(updatedSteps.Length);

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                app.ApplyTheme(theme);
                app.ApplyBackgroundColor(theme == AppTheme.Dark ? "#202027" : "#FFFFFF");
                app.ApplyAccentColor(theme == AppTheme.Dark ? "#E9B557" : "#3267B2");
                UpdateLayout();
                await WaitForNativePreviewFrameAsync();
                if (!ReferenceEquals(WorkingPlanView.Background, NativeThemeBrushes.Resource("MissumLayerBrush", 27)))
                    throw new InvalidOperationException("Der Arbeitsplan folgt nicht der Missum-Hintergrundfarbe.");
                await SaveMathPreviewAsync(LayoutRoot, "native-working-plan-" + theme.ToString().ToLowerInvariant() + ".png");
            }
            await CommandAsync("session.open", new { sessionId = emptyId });
            if (WorkingPlanView.Visibility != Visibility.Collapsed || WorkingPlanView.StepCount != 0)
                throw new InvalidOperationException("Beim Sitzungswechsel bleibt ein fremder Plan sichtbar.");
            await CommandAsync("session.open", new { sessionId = fixtureId });
            AssertPlan(updatedSteps.Length);
            AssertPlanAnimation(active: false, pendingOutlines: 6);
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-working-plan-validation.json"),
                JsonSerializer.Serialize(new { passed = true, planOutsideLoadedHistory = true, fullPlanSteps = updatedSteps.Length,
                    partialUpdateRetainsAllSteps = true, staleAndForeignEventsIgnored = true, retainedRows = true,
                    nativeInLayout = true, clickToCollapse = true, activeRing = true, settingsLive = true,
                    idlePlanNotAnimating = true, terminalStatesStopAnimation = true, resumedRunRestartsAnimation = true,
                    latePlanReceiptDoesNotRestartAnimation = true, persistedStatusesUnchanged = true,
                    themes = WorkingPlanSmokeThemes, sessionIsolation = true, subagentIsolation = true, durablePlanRestored = true, modelRequests = 0 }));
        }
        finally
        {
            app.ApplyTheme(originalTheme); app.ApplyBackgroundColor(originalBackground); app.ApplyAccentColor(originalAccent);
            await _settings.UpdateAsync(value => value with { ShowWorkingPlan = settings.ShowWorkingPlan, IsAutomaticSpeechEnabled = settings.IsAutomaticSpeechEnabled });
            await CommandAsync("session.open", new { sessionId = originalSession });
            if (fixtureId != Guid.Empty) await chats.DeleteSessionAsync(fixtureId, _lifetime.Token);
            if (emptyId != Guid.Empty) await chats.DeleteSessionAsync(emptyId, _lifetime.Token);
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            _selectionInputSmokeActive = originalFixture;
        }

        void AssertPlan(int count)
        {
            if (WorkingPlanView.Visibility != Visibility.Visible || WorkingPlanView.StepCount != count
                || WorkingPlanView.VisibleTitles.Count != count || WorkingPlanView.VisibleTitles.Any(title => title.Contains(" · pending", StringComparison.Ordinal)
                    || title.Contains(" · in_progress", StringComparison.Ordinal) || title.Contains(" · completed", StringComparison.Ordinal))
                || Descendants(WorkingPlanView).OfType<Button>().Any(button => button.Content?.ToString() == "Kopieren"))
                throw new InvalidOperationException("Der native Arbeitsplan zeigt nicht alle Etappen mit ausschließlich symbolischem Status.");
        }
        void InvokeToggle()
        {
            if (new ButtonAutomationPeer(WorkingPlanView.Toggle).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Der Arbeitsplan-Reiter ist nicht per Klick bedienbar.");
            invoke.Invoke();
        }
        void AssertPlanAnimation(bool active, int pendingOutlines)
        {
            if (Descendants(WorkingPlanView).OfType<ProgressRing>().Count(ring => ring.IsActive) != (active ? 1 : 0)
                || Descendants(WorkingPlanView).OfType<Ellipse>().Count(outline => outline.Visibility == Visibility.Visible
                    && outline.Parent is Grid icon && icon.Children.OfType<ProgressRing>().Any()) != pendingOutlines)
                throw new InvalidOperationException("Die Arbeitsplan-Animation entspricht nicht dem aktuellen Laufzustand.");
        }
    }
}
