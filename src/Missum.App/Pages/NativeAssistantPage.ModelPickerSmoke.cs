using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static readonly string[] ModelPickerSmokeLevels = ["low", "medium", "high", "xhigh", "ultra"];
    private static readonly string[] ModelPickerSmokeToggleLevels = ["none", "on"];
    private static readonly string[] ModelPickerSmokeSimpleLevels = ["low", "high"];
    private async Task VerifyModelPickerSmokeAsync()
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Modellauswahl-Prüfung benötigt ein isoliertes Profil ohne Modellstart.");

        const string selectedModel = "coding/qwen-test";
        var app = App.Current;
        var originalSettings = _settings.Current;
        var originalRunning = _running;
        var originalMode = _mode;
        var originalChild = _activeSubagentId;
        var originalReasoning = _reasoning;
        var originalReasoningModel = _reasoningModel;
        var originalFixture = _selectionInputSmokeActive;
        var originalAccent = app.AccentColor;
        var originalBackground = app.BackgroundColor;
        var originalTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme == ElementTheme.Light ? AppTheme.Light : AppTheme.Dark;
        var flyouts = new List<Flyout>();
        _selectionInputSmokeActive = true;
        try
        {
            _running = false;
            _mode = "coding";
            _activeSubagentId = null;
            await _settings.UpdateAsync(current => current with { SelectedModel = selectedModel, SelectedCodingModel = selectedModel });
            var options = new ComposerReasoningOptions(selectedModel, "coding", "high", ["low", "medium", "high", "xhigh", "ultra"],
                "high", true, MaximumContextTokens: 1048576, SelectedContextTokens: 258000);
            _reasoning = options;
            _reasoningModel = "coding:" + selectedModel;
            RefreshComposerModelDisplay();
            if (!Descendants(ModelButton).OfType<TextBlock>().Any(label => label.Text == "Aufwand auswählen"))
                throw new InvalidOperationException("Der kompakte Dropdown-Aufruf zeigt nicht Aufwand auswählen.");

            var models = JsonSerializer.SerializeToElement(new[]
            {
                new { id = selectedModel, role = "coding", downloaded = true, displayName = "Qwen3.8-27B", reasoningEfforts = ModelPickerSmokeLevels },
                new { id = "coding/deepseek-test", role = "coding", downloaded = true, displayName = "DeepSeek-V4.1-Flash", reasoningEfforts = ModelPickerSmokeToggleLevels },
                new { id = "coding/qwen-next-test", role = "coding", downloaded = true, displayName = "Qwen3.8-Flash-Next", reasoningEfforts = ModelPickerSmokeSimpleLevels },
            }).EnumerateArray().ToArray();

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                app.ApplyTheme(theme);
                app.ApplyBackgroundColor(theme == AppTheme.Dark ? "#181818" : "#FFFFFF");
                app.ApplyAccentColor("#A970FF");
                UpdateLayout();
                await WaitForNativePreviewFrameAsync();

                var reasoning = CreateReasoningFlyout(options);
                flyouts.Add(reasoning);
                reasoning.ShowAt(ModelButton);
                await WaitForNativePreviewFrameAsync();
                var content = RequireContent(reasoning);
                AssertNoModalControls(content);
                var sliders = Descendants(content).OfType<Slider>().ToArray();
                if (sliders.Length != 2)
                    throw new InvalidOperationException("Die Aufwandsauswahl muss genau einen Reasoning- und einen Kontextslider zeigen.");
                var effort = sliders.Single(slider => AutomationProperties.GetName(slider) == "Reasoning-Aufwand");
                var context = sliders.Single(slider => AutomationProperties.GetName(slider) == "Kontexttoken-Limit");
                if (!effort.IsEnabled || context.Maximum != 1048576 || context.Value != 258000 || !context.IsEnabled)
                    throw new InvalidOperationException("Die Dropdown-Slider verwenden nicht die gespeicherten Modellgrenzen und Auswahlwerte.");
                if (!Descendants(content).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "Modell auswählen"))
                    throw new InvalidOperationException("Die Aufwandskarte besitzt keinen direkten Modell-Dropdown-Aufruf.");
                if (Descendants(content).OfType<Button>().Count() != 1
                    || Descendants(content).OfType<FontIcon>().Any(icon => icon.Glyph is "\uE945" or "\uE72C"))
                    throw new InvalidOperationException("Blitz und Zurücksetzen dürfen nicht im Dropdown erscheinen.");
                if (new SliderAutomationPeer(effort).GetPattern(PatternInterface.RangeValue) is not IRangeValueProvider range)
                    throw new InvalidOperationException("Der Reasoning-Slider bietet keine native barrierefreie Werteingabe.");
                range.SetValue(3);
                if (effort.Value != 3)
                    throw new InvalidOperationException("Die native Slider-Eingabe wurde nicht übernommen.");
                range.SetValue(2);
                if (effort.Value != 2) throw new InvalidOperationException("Die vorherige Reasoning-Stufe wurde nicht wiederhergestellt.");
                await SaveMathPreviewAsync(content, "native-reasoning-dropdown-" + theme.ToString().ToLowerInvariant() + ".png");
                await SaveMathPreviewAsync(ComposerSurface, "native-model-picker-trigger-" + theme.ToString().ToLowerInvariant() + ".png");
                reasoning.Hide();
                await WaitForNativePreviewFrameAsync();

                var selection = CreateModelSelectionFlyout(models);
                flyouts.Add(selection);
                selection.ShowAt(ModelButton);
                await WaitForNativePreviewFrameAsync();
                content = RequireContent(selection);
                await WaitForPagingSmokeAsync(() => Descendants(content).OfType<Button>()
                    .Count(button => button.Tag is string id && models.Any(model => S(model, "id") == id)) == models.Length,
                    "Die Modellliste hat ihre nativen Zeilen nicht realisiert.");
                AssertNoModalControls(content);
                if (!Descendants(content).OfType<TextBlock>().Any(label => label.Text == "Modell auswählen")
                    || !Descendants(content).OfType<ScrollViewer>().Any())
                    throw new InvalidOperationException("Die Modellauswahl ist keine kompakte scrollbare Dropdown-Liste.");
                var rows = Descendants(content).OfType<Button>().Where(button => button.Tag is string id && models.Any(model => S(model, "id") == id)).ToArray();
                if (rows.Length != models.Length)
                    throw new InvalidOperationException("Die Modellliste zeigt nicht sämtliche bereitgestellten Modelle.");
                foreach (var row in rows)
                {
                    var expected = models.Single(model => S(model, "id") == (string)row.Tag);
                    if (AutomationProperties.GetName(row) != S(expected, "displayName"))
                        throw new InvalidOperationException("Der Modellname ist nicht über die native Zeileninteraktion erreichbar.");
                    var checkedIcons = Descendants(row).OfType<FontIcon>().Where(icon => icon.Glyph == "\uE73E" && icon.Visibility == Visibility.Visible).ToArray();
                    if (checkedIcons.Length != ((string)row.Tag == selectedModel ? 1 : 0))
                        throw new InvalidOperationException("Der Auswahlhaken markiert nicht ausschließlich das gespeicherte Modell.");
                }
                await SaveMathPreviewAsync(content, "native-model-selection-dropdown-" + theme.ToString().ToLowerInvariant() + ".png");
                selection.Hide();
                await WaitForNativePreviewFrameAsync();
            }

            var unsupported = CreateReasoningFlyout(options with { Available = false, Selected = null, DefaultLevel = null, Levels = [], Detail = "Keine wählbaren Aufwandsstufen." });
            flyouts.Add(unsupported);
            if (Descendants(RequireContent(unsupported)).OfType<Slider>().Count() != 1
                || Descendants(RequireContent(unsupported)).OfType<Slider>().Single().Maximum != 1048576)
                throw new InvalidOperationException("Ein Modell ohne Reasoning-Stufen muss seinen Kontextslider weiterhin anbieten.");
            _running = true;
            var running = CreateReasoningFlyout(options);
            flyouts.Add(running);
            if (Descendants(RequireContent(running)).OfType<Slider>()
                .Single(slider => AutomationProperties.GetName(slider) == "Kontexttoken-Limit").IsEnabled)
                throw new InvalidOperationException("Das Kontextlimit darf während eines aktiven Laufs nicht verändert werden.");
            await File.WriteAllTextAsync(System.IO.Path.Combine(App.Current.DataDirectory, "native-model-picker-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, renderer = "WinUI3", compactTrigger = true, flyoutInsteadOfDialog = true,
                    modelCheckmark = true, nativeSliderAutomation = true, headerUtilityButtons = false,
                    perModelContextMaximum = 1048576, savedContextSelection = 258000,
                    contextAvailableWithoutReasoning = true, contextLockedDuringRun = true,
                    noNumberInput = true, themes = WorkingPlanSmokeThemes, accent = "#A970FF",
                    modelRequests = 0, userDataModified = false,
                }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            foreach (var flyout in flyouts) flyout.Hide();
            _running = originalRunning;
            _mode = originalMode;
            _activeSubagentId = originalChild;
            await _settings.UpdateAsync(current => current with
            {
                SelectedModel = originalSettings.SelectedModel,
                SelectedCodingModel = originalSettings.SelectedCodingModel,
            });
            _reasoning = originalReasoning;
            _reasoningModel = originalReasoningModel;
            app.ApplyTheme(originalTheme);
            app.ApplyBackgroundColor(originalBackground);
            app.ApplyAccentColor(originalAccent);
            RefreshComposerModelDisplay();
            _selectionInputSmokeActive = originalFixture;
        }

        static FrameworkElement RequireContent(Flyout flyout) => flyout.Content as FrameworkElement
            ?? throw new InvalidOperationException("Das Dropdown besitzt keinen nativen Inhalt.");


        static void AssertNoModalControls(FrameworkElement content)
        {
            if (Descendants(content).Any(element => element is ContentDialog or ComboBox or NumberBox))
                throw new InvalidOperationException("Die Auswahl darf keine modalen Dialoge, Comboboxen oder Zahleneingabefelder enthalten.");
        }
    }
}
