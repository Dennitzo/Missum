using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private void AddContextLimitSlider(StackPanel panel, Flyout flyout, ComposerReasoningOptions? options)
    {
        if (options is null || options.MaximumContextTokens < 2048) return;
        var selected = options.SelectedContextTokens;
        var caption = new TextBlock { Text = $"Kontext: {selected:N0} Token", FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeBrush("MissumAccentReadableBrush", 139), Margin = new(0, 10, 0, 0) };
        panel.Children.Add(caption);
        panel.Children.Add(new TextBlock { Text = $"Modellmaximum: {options.MaximumContextTokens:N0}", FontSize = 12,
            Foreground = ThemeBrush("MissumMutedTextBrush", 185), HorizontalAlignment = HorizontalAlignment.Center });
        var slider = new Slider { Minimum = 2048, Maximum = options.MaximumContextTokens, Value = selected,
            StepFrequency = 1, IsThumbToolTipEnabled = false, IsEnabled = !_running,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Margin = new(0, 2, 0, 0), CornerRadius = new(12) };
        slider.Resources["SliderTrackThemeHeight"] = 24d;
        slider.Resources["SliderHorizontalThumbWidth"] = 24d;
        slider.Resources["SliderHorizontalThumbHeight"] = 24d;
        slider.Resources["SliderThumbCornerRadius"] = new CornerRadius(14);
        slider.Resources["SliderThumbBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        slider.Resources["SliderOuterThumbBackground"] = new SolidColorBrush(Microsoft.UI.Colors.White);
        foreach (var suffix in new[] { "", "PointerOver", "Pressed" })
        {
            slider.Resources["SliderTrackValueFill" + suffix] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            slider.Resources["SliderTrackFill" + suffix] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            slider.Resources["SliderThumbBackground" + suffix] = new SolidColorBrush(Microsoft.UI.Colors.White);
        }
        var color = ((SolidColorBrush)ThemeBrush("MissumAccentBrush", 139)).Color;
        var gradient = new LinearGradientBrush { StartPoint = new(0, .5), EndPoint = new(1, .5) };
        gradient.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(255, (byte)((color.R + 255) / 2),
            (byte)((color.G + 255) / 2), (byte)((color.B + 255) / 2)), Offset = 0 });
        gradient.GradientStops.Add(new() { Color = color, Offset = 1 });
        var fill = new Border { Height = 24, CornerRadius = new(12, 0, 0, 12), Background = gradient,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var host = new Grid();
        host.Children.Add(new Border { Height = 24, CornerRadius = new(12), Background = ThemeBrush("MissumHoverBrush", 69),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        host.Children.Add(fill); host.Children.Add(slider);
        void Refresh() => fill.Width = 12 + Math.Max(0, host.ActualWidth - 24) * (slider.Value - slider.Minimum) / Math.Max(1, slider.Maximum - slider.Minimum);
        host.SizeChanged += (_, _) => Refresh();
        slider.ValueChanged += (_, args) => { selected = (int)Math.Round(args.NewValue); caption.Text = $"Kontext: {selected:N0} Token"; Refresh(); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "Kontexttoken-Limit");
        panel.Children.Add(host);
        if (_running) panel.Children.Add(new TextBlock { Text = "Nach Ende des laufenden Auftrags änderbar.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        flyout.Closed += async (_, _) =>
        {
            if (selected == options.SelectedContextTokens || _running) return;
            try
            {
                await _settings.UpdateAsync(current => current with { ContextLimitsByModel = new(current.ContextLimitsByModel, StringComparer.OrdinalIgnoreCase)
                    { [options.ModelId] = selected } });
                _reasoning = (_reasoning?.ModelId == options.ModelId ? _reasoning : options) with { SelectedContextTokens = selected };
                UpdateContext(System.Text.Json.JsonSerializer.SerializeToElement(new { contextLimit = selected }));
            }
            catch (Exception exception) { ShowError(exception.Message); }
        };
    }
}
