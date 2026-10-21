using System.Text.Json;
using Missum.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyContextLimitSmokeAsync()
    {
        if (!IsMessageFooterSmoke) throw new InvalidOperationException("Kontextprüfung benötigt isoliertes Profil.");
        const string model = "context-slider-fixture";
        var previousRunning = _running;
        _running = false;
        var options = new ComposerReasoningOptions(model, "coding", "on", ["none", "on"], "on", true,
            MaximumContextTokens: 1048576, SelectedContextTokens: 131072);
        var panel = new StackPanel { Width = 232 };
        var flyout = new Flyout { Content = panel };
        AddContextLimitSlider(panel, flyout, options);
        try
        {
            flyout.ShowAt(ModelButton);
            await Task.Delay(160, _lifetime.Token);
            var slider = Descendants(panel).OfType<Slider>().Single();
            if (Descendants(panel).OfType<NumberBox>().Any())
                throw new InvalidOperationException("Die Kontextwahl darf kein Zahleneingabefeld enthalten.");
            if (slider.Maximum != 1048576 || slider.Value != 131072 || !slider.IsEnabled)
                throw new InvalidOperationException("Der Kontextslider verwendet nicht die Modellgrenzen.");
            slider.Value = 258000;
            if (slider.Value != 258000 || !panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("258", StringComparison.Ordinal)))
                throw new InvalidOperationException("Die Tokenanzeige und der Slider sind nicht synchron.");
            await SaveMathPreviewAsync(panel, "native-context-limit-preview.png");
            flyout.Hide();
            await WaitForPagingSmokeAsync(() => _settings.Current.ContextLimitsByModel.GetValueOrDefault(model) == 258000,
                "Die Kontextwahl wurde nicht dauerhaft gespeichert.");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(_settings.DataDirectory, "settings.json"), _lifetime.Token));
            if (saved.RootElement.GetProperty("contextLimitsByModel").GetProperty(model).GetInt32() != 258000)
                throw new InvalidOperationException("Die Kontextwahl fehlt in der Einstellungsdatei.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-context-limit-validation.json"),
                JsonSerializer.Serialize(new { passed = true, maximum = 1048576, selected = 258000, slider = true,
                    durableSave = true, numberInput = false, modelRequests = 0, userDataModified = false }), _lifetime.Token);
        }
        finally { flyout.Hide(); _running = previousRunning; }
    }
}
