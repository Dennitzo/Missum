using System.Text.Json;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Path = System.IO.Path;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // These fixtures exist only in a separate smoke profile, and remain there so
    // both real chat interactions can be inspected and operated in the window.
    private async Task VerifyPlanModeInteractionSmokeAsync()
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Planmodus-Prüfung benötigt ein isoliertes Profil ohne Modellstart.");

        var chats = App.Current.GetService<IChatRepository>();
        var questionPayload = JsonSerializer.Serialize(new
        {
            kind = "questions",
            questions = new[]
            {
                new
                {
                    id = "cameras", text = "Welche Kameras soll die Vision-Analyse in der ersten Version auswerten?",
                    options = new[]
                    {
                        new { id = "unreal", label = "Unreal-Kameras", description = "Kameras und Viewports im Unreal-Projekt liefern Bilder zur Szenenprüfung.", recommended = true },
                        new { id = "webcam", label = "Unreal und Webcam", description = "Zusätzlich werden angeschlossene physische Kameras als Bildquelle unterstützt.", recommended = false },
                        new { id = "runtime", label = "Auch Laufzeit-Feeds", description = "Zusätzlich werden Kamerabilder während Play-in-Editor oder aus laufenden Spielen unterstützt.", recommended = false },
                    },
                    allowFreeText = true,
                },
            },
        }, JsonOptions);
        var multiPayload = JsonSerializer.Serialize(new
        {
            kind = "questions",
            questions = new[]
            {
                new { id = "first", text = "Welches Projekt?", options = new[] { new { id = "a", label = "Erstes Projekt", description = "" } }, allowFreeText = true },
                new { id = "second", text = "Welche Kamera?", options = new[] { new { id = "b", label = "Zweite Kamera", description = "" } }, allowFreeText = true },
            },
        }, JsonOptions);
        const string planTitle = "Unreal Engine als Missum-Programmintegration";
        const string planEnd = "Ende des vollständigen Plans: Kameraergebnisse eindeutig zuordnen.";
        var planMarkdown = "## Ziel und Ausgangslage\n\nMissum erhält unter **Programmintegrationen** ein Unreal-Engine-Werkzeug für Blueprint-Programmierung und die visuelle Prüfung von Unreal-Szenen.\n\n"
            + string.Join("\n\n", Enumerable.Range(1, 12).Select(index => $"## Umsetzung {index}\n\nEditor starten, Projekt und Blueprint-Graphen prüfen und Änderungen dokumentieren."))
            + "\n\n## Abnahme\n\n" + planEnd;
        var planPayload = JsonSerializer.Serialize(new
        {
            kind = "plan", title = planTitle, markdown = planMarkdown,
        }, JsonOptions);
        var questions = await chats.CreateSessionAsync("Planmodus · Rückfrage", ChatMode.Coding, _lifetime.Token);
        var plan = await chats.CreateSessionAsync("Planmodus · Abschluss", ChatMode.Coding, _lifetime.Token);
        var multi = await chats.CreateSessionAsync("Planmodus · Mehrfachfragen", ChatMode.Coding, _lifetime.Token);
        // Real project grouping makes both sessions discoverable in the sidebar
        // and supplies the workspace permission boundary used by integrations.
        var project = Path.Combine(App.Current.DataDirectory, "Planmodus-Testprojekt");
        Directory.CreateDirectory(project);
        await chats.SetCodingWorkspacePathAsync(questions.Id, project, activateCoding: false, _lifetime.Token);
        await chats.SetCodingWorkspacePathAsync(plan.Id, project, activateCoding: false, _lifetime.Token);
        await chats.SetCodingWorkspacePathAsync(multi.Id, project, activateCoding: false, _lifetime.Token);
        var grouped = await chats.GetSessionAsync(questions.Id, _lifetime.Token);
        if (grouped?.SessionGroupId is not { } groupId)
            throw new InvalidOperationException("Die Planmodus-Test-Sitzung besitzt keine Projektgruppe.");
        await chats.SetSessionGroupCollapsedAsync(groupId, false, _lifetime.Token);
        await chats.SetPersistentExtensionActionIdAsync(questions.Id, BuiltInActionIds.PlanMode, _lifetime.Token);
        await chats.SetPersistentExtensionActionIdAsync(plan.Id, BuiltInActionIds.PlanMode, _lifetime.Token);
        await chats.SetPersistentExtensionActionIdAsync(multi.Id, BuiltInActionIds.PlanMode, _lifetime.Token);
        var questionMessage = await chats.AddMessageAsync(questions.Id, ChatRole.Assistant,
            "```assistant-plan\n" + questionPayload + "\n```", MessageStatus.Completed, cancellationToken: _lifetime.Token);
        var planMessage = await chats.AddMessageAsync(plan.Id, ChatRole.Assistant,
            "```assistant-plan\n" + planPayload + "\n```", MessageStatus.Completed, cancellationToken: _lifetime.Token);
        var multiMessage = await chats.AddMessageAsync(multi.Id, ChatRole.Assistant,
            "```assistant-plan\n" + multiPayload + "\n```", MessageStatus.Completed, cancellationToken: _lifetime.Token);
        var intercepted = new List<(string? Prompt, bool Leave)>();
        _planInteractionSmokeAdapter = (prompt, leave) => { intercepted.Add((prompt, leave)); return Task.CompletedTask; };

        await OpenFixtureAsync(plan.Id);
        var planCard = GetCard(planMessage.Id);
        AssertText(planCard, "Diesen Plan implementieren?");
        AssertButton(planCard, "Ja, diesen Plan implementieren");
        AssertText(planCard, "✓");
        AssertInput(planCard, "Änderungswünsche zum Plan");
        AssertNoExcludedActions(planCard);
        AssertButton(planCard, "Vollständigen Plan öffnen: " + planTitle);
        Invoke(Descendants(planCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Ja, diesen Plan implementieren"));
        await WaitForPagingSmokeAsync(() => intercepted.Count == 1, "Die Planfreigabe wurde nicht ausgelöst.");
        if (!intercepted[0].Leave || intercepted[0].Prompt?.Contains(planEnd, StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Die Planfreigabe muss den vollständigen Plan übermitteln und den Planmodus verlassen.");
        intercepted.Clear();
        if (RenderedText(planCard).Contains(planEnd, StringComparison.Ordinal))
            throw new InvalidOperationException("Der Chat darf nur eine Zusammenfassung statt des gesamten Plans enthalten.");
        await SaveMathPreviewAsync(_messageViews[planMessage.Id.ToString()].View, "native-plan-mode-approval-preview.png");
        var open = Descendants(planCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Vollständigen Plan öffnen: " + planTitle);
        Invoke(open);
        await WaitForPagingSmokeAsync(() => _activePlanModeTab is not null && _planModeTabHost.Visibility == Visibility.Visible,
            "Der vollständige Plan wurde nicht in einem eigenen Tab geöffnet.");
        await WaitForPagingSmokeAsync(() => RenderedText(_planModeTabHost).Contains(planTitle, StringComparison.Ordinal)
            && RenderedText(_planModeTabHost).Contains(planEnd, StringComparison.Ordinal),
            "Der Plan-Tab enthält nicht Titel und Ende des vollständigen Plans.");
        AssertButton(SessionTabsPanel, "Plan-Tab: " + planTitle);
        await SaveMathPreviewAsync(_planModeTabHost, "native-plan-mode-full-plan-preview.png");
        var close = Descendants(SessionTabsPanel).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Plan-Tab schließen: " + planTitle);
        Invoke(close);
        await WaitForPagingSmokeAsync(() => _activePlanModeTab is null && _planModeTabHost.Visibility == Visibility.Collapsed,
            "Der geschlossene Plan-Tab kehrt nicht zum Chat zurück.");
        Invoke(open);
        await WaitForPagingSmokeAsync(() => _activePlanModeTab is not null && _planModeTabHost.Visibility == Visibility.Visible,
            "Der geschlossene Plan-Tab konnte nicht erneut geöffnet werden.");

        await OpenFixtureAsync(questions.Id);
        if (_activePlanModeTab is not null || _planModeTabHost.Visibility != Visibility.Collapsed
            || Descendants(SessionTabsPanel).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Plan-Tab: " + planTitle))
            throw new InvalidOperationException("Der Plan-Tab darf in einer anderen Sitzung nicht sichtbar sein.");
        foreach (var actionId in new[] { "com.missum.blender/edit", "com.missum.unreal/edit" })
        {
            var descriptor = Items(_snapshot, "actionDescriptors").FirstOrDefault(item => S(item, "actionId") == actionId);
            if (descriptor.ValueKind != JsonValueKind.Object || S(descriptor, "disabledReason").Length > 0)
                throw new InvalidOperationException("Die Programmintegration ist im Testprojekt nicht auswählbar: " + actionId + " " + S(descriptor, "disabledReason"));
        }
        var questionCard = GetCard(questionMessage.Id);
        AssertText(questionCard, "Welche Kameras soll die Vision-Analyse in der ersten Version auswerten?");
        foreach (var option in new[] { "Unreal-Kameras", "Unreal und Webcam", "Auch Laufzeit-Feeds" }) AssertButton(questionCard, option);
        AssertText(questionCard, "Empfohlen");
        AssertInput(questionCard, "Eigene Antwort");
        AssertNoExcludedActions(questionCard);
        if (Descendants(questionCard).OfType<ComboBox>().Any())
            throw new InvalidOperationException("Die Planmodus-Antwortoptionen dürfen nicht in einem nativen Dropdown versteckt sein.");
        if (Descendants(questionCard).OfType<Button>().Any(button => button.Content is string label && label == "Antworten senden"))
            throw new InvalidOperationException("Ein separater Antworten-senden-Button darf nicht angezeigt werden.");
        var firstOption = Descendants(questionCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Unreal-Kameras");
        var ownAnswer = Descendants(questionCard).OfType<TextBox>().Single(input => AutomationProperties.GetName(input) == "Eigene Antwort");
        var initialColor = (firstOption.Background as SolidColorBrush)?.Color;
        ownAnswer.Text = "Eigene Antwort";
        await WaitForNativePreviewFrameAsync();
        if (new ButtonAutomationPeer(firstOption).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Die Planmodus-Antwortoption ist nicht barrierefrei bedienbar.");
        invoke.Invoke();
        await WaitForPagingSmokeAsync(() => intercepted.Count == 1, "Eine einzelne Antwortoption muss unmittelbar gesendet werden.");
        if (intercepted[0].Prompt?.Contains("Unreal-Kameras", StringComparison.Ordinal) != true || intercepted[0].Leave)
            throw new InvalidOperationException("Die unmittelbar gesendete Auswahl stimmt nicht mit der gewählten Option überein.");
        if (ownAnswer.Text.Length != 0 || (firstOption.Background as SolidColorBrush)?.Color == initialColor)
            throw new InvalidOperationException("Die Auswahl einer Antwort muss den Freitext löschen und sichtbar markiert sein.");
        ownAnswer.Text = "Andere Kamera";
        await WaitForNativePreviewFrameAsync();
        if ((firstOption.Background as SolidColorBrush)?.Color != initialColor)
            throw new InvalidOperationException("Eine eigene Antwort muss die gewählte Option wieder abwählen.");
        Invoke(Descendants(questionCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Antworten senden"));
        await WaitForPagingSmokeAsync(() => intercepted.Count == 2, "Die eigene Antwort wurde über den Pfeil nicht gesendet.");
        if (intercepted[1].Prompt?.Contains("Andere Kamera", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Der Freitext wurde nicht korrekt über den Pfeil übergeben.");
        ownAnswer.Text = "";
        await SaveMathPreviewAsync(_messageViews[questionMessage.Id.ToString()].View, "native-plan-mode-questions-preview.png");
        intercepted.Clear();
        await OpenFixtureAsync(multi.Id);
        var multiCard = GetCard(multiMessage.Id);
        Invoke(Descendants(multiCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Erstes Projekt"));
        await WaitForNativePreviewFrameAsync();
        if (intercepted.Count != 0) throw new InvalidOperationException("Bei mehreren Fragen darf eine unvollständige Auswahl nicht gesendet werden.");
        Invoke(Descendants(multiCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Zweite Kamera"));
        await WaitForPagingSmokeAsync(() => intercepted.Count == 1, "Vollständig beantwortete Mehrfachfragen wurden nicht automatisch gesendet.");
        if (intercepted[0].Prompt?.Contains("Erstes Projekt", StringComparison.Ordinal) != true
            || intercepted[0].Prompt?.Contains("Zweite Kamera", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Der gemeinsame Antworttext der Mehrfachfragen ist unvollständig.");
        intercepted.Clear();
        var multiInputs = Descendants(multiCard).OfType<TextBox>().Where(input => AutomationProperties.GetName(input) == "Eigene Antwort").ToArray();
        var multiSends = Descendants(multiCard).OfType<Button>().Where(button => AutomationProperties.GetName(button) == "Antworten senden").ToArray();
        if (multiInputs.Length != 2 || multiSends.Length != 2)
            throw new InvalidOperationException("Jede der zwei Rückfragen muss ein eigenes Freitextfeld mit Pfeil besitzen.");
        multiInputs[1].Text = "Kamera nach Wunsch";
        await WaitForNativePreviewFrameAsync();
        if (intercepted.Count != 0) throw new InvalidOperationException("Unbestätigter Freitext darf nicht automatisch gesendet werden.");
        Invoke(multiSends[1]);
        await WaitForPagingSmokeAsync(() => intercepted.Count == 1, "Der bestätigte Freitext hat die vollständigen Antworten nicht gesendet.");
        if (intercepted[0].Prompt?.Contains("Erstes Projekt", StringComparison.Ordinal) != true
            || intercepted[0].Prompt?.Contains("Kamera nach Wunsch", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Die gemeinsame Antwort aus Option und bestätigtem Freitext ist unvollständig.");
        // Keep interception enabled in this isolated test window for subsequent
        // manual UI inspection; it must never start a model or mutate a project.
        intercepted.Clear();
        await OpenFixtureAsync(questions.Id);
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-plan-mode-validation.json"), JsonSerializer.Serialize(new
        {
            passed = true, questionSessionId = questions.Id, planSessionId = plan.Id,
            questionMessageId = questionMessage.Id, planMessageId = planMessage.Id,
            visibleOptions = 3, freeTextAvailable = true, optionSelectionAndFreeTextExclusive = true,
            singleQuestionAutoSubmit = true, multiQuestionWaitsUntilComplete = true, freeTextArrow = true,
            approvalAvailable = true, fullPlanInOwnTab = true, fullPlanCloseReturnsToChat = true,
            fullPlanReopen = true, sessionIsolation = true, projectSessionsVisible = true,
            programIntegrationsSelectable = true, modelRequests = 0,
        }, JsonOptions), _lifetime.Token);

        async Task OpenFixtureAsync(Guid id)
        {
            if (!await CommandAsync("session.open", new { sessionId = id }))
                throw new InvalidOperationException("Die Planmodus-Test-Sitzung konnte nicht geöffnet werden.");
            ShowChatView(); RenderSessionTabs(); RenderMessagesNow(); UpdateLayout();
            await WaitForNativePreviewFrameAsync();
        }

        Border GetCard(Guid id) => Descendants(_messageViews[id.ToString()].View).OfType<Border>()
            .FirstOrDefault(card => card.Name == PlanCardName)
            ?? throw new InvalidOperationException("Die Planmodus-Interaktionskarte fehlt im Chat.");

        static void AssertText(Border card, string text)
        {
            if (!Descendants(card).OfType<TextBlock>().Any(block => block.Text == text))
                throw new InvalidOperationException("Der Titel fehlt auf der Planmodus-Karte: " + text);
        }

        static void AssertButton(FrameworkElement card, string name)
        {
            if (!Descendants(card).OfType<Button>().Any(button => button.IsEnabled && AutomationProperties.GetName(button) == name))
                throw new InvalidOperationException("Die interaktive Planmodus-Option fehlt: " + name);
        }

        static void AssertInput(Border card, string name)
        {
            if (!Descendants(card).OfType<TextBox>().Any(input => input.IsEnabled && AutomationProperties.GetName(input) == name))
                throw new InvalidOperationException("Das beschriftete Planmodus-Eingabefeld fehlt: " + name);
        }

        static void AssertNoExcludedActions(Border card)
        {
            if (Descendants(card).OfType<Button>().Any(button =>
                AutomationProperties.GetName(button) is "Überspringen" or "Schließen" or "Mikrofon"))
                throw new InvalidOperationException("Die Planmodus-Karte zeigt eine ausgeschlossene Aktion.");
        }

        static void Invoke(Button button)
        {
            if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Die Planmodus-Aktion ist nicht barrierefrei bedienbar: " + AutomationProperties.GetName(button));
            invoke.Invoke();
        }

        static string RenderedText(FrameworkElement element) => string.Join("\n", Descendants(element).OfType<TextBlock>()
            .Select(block => block.Inlines.Count == 0 ? block.Text : string.Concat(block.Inlines.Select(InlineText))));

        static string InlineText(Inline inline) => inline switch
        {
            Run run => run.Text,
            LineBreak => "\n",
            Span span => string.Concat(span.Inlines.Select(InlineText)),
            _ => "",
        };
    }
}
