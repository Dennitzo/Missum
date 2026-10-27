using Missum.App.Services;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private void AddContextLimitSlider(StackPanel panel, Flyout flyout, ComposerReasoningOptions? options)
    {
        if (options is null || options.MaximumContextTokens < 2048) return;
        var selected = options.SelectedContextTokens;
        var caption = new TextBlock { Text = $"Kontextlimit: {selected:N0}", FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeBrush("MissumTextBrush", 248), Margin = new(0, 12, 0, 0) };
        panel.Children.Add(caption);
        panel.Children.Add(new TextBlock { Text = $"Modellmaximum: {options.MaximumContextTokens:N0}", FontSize = 12,
            Foreground = ThemeBrush("MissumMutedTextBrush", 185), HorizontalAlignment = HorizontalAlignment.Center });
        var slider = new Slider { Minimum = 2048, Maximum = options.MaximumContextTokens, Value = selected,
            StepFrequency = 1, IsThumbToolTipEnabled = false, IsEnabled = !_running };
        slider.ValueChanged += (_, args) => { selected = (int)Math.Round(args.NewValue); caption.Text = $"Kontextlimit: {selected:N0}"; };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "Kontexttoken-Limit");
        panel.Children.Add(new NativeComposerSlider(slider));
        if (_running) panel.Children.Add(new TextBlock { Text = "Nach Ende des laufenden Auftrags änderbar.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        flyout.Closed += async (_, _) =>
        {
            if (selected == options.SelectedContextTokens || _running) return;
            try
            {
                await _settings.UpdateAsync(current => current with { ContextLimitsByModel = new(current.ContextLimitsByModel, StringComparer.OrdinalIgnoreCase)
                    { [options.ModelId] = selected } });
                if (_disposed || ActiveSubagent is not null || ModelRole != options.Role || _settings.Current.SelectedModel != options.ModelId) return;
                if (_reasoning?.ModelId == options.ModelId) _reasoning = _reasoning with { SelectedContextTokens = selected };
                UpdateContext(System.Text.Json.JsonSerializer.SerializeToElement(new { contextLimit = selected }));
            }
            catch (Exception exception) { ShowError(exception.Message); }
        };
    }
}
