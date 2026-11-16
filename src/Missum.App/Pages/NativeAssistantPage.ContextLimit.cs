using Missum.App.Services;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Func<Task<bool>>? _commitOpenContextLimit;
    private readonly ContextLimitCommitQueue _contextLimitCommits = new();

    private Task<bool> CommitContextLimitBeforeSendAsync() =>
        _commitOpenContextLimit?.Invoke() ?? _contextLimitCommits.Pending;

    private void AddContextLimitSlider(StackPanel panel, Flyout flyout, ComposerReasoningOptions? options)
    {
        if (options is null || options.MaximumContextTokens < 2048) return;
        var experimental = options.TrainingContextTokens is >= 2048 && options.MaximumContextTokens == 1_048_576
            && options.MaximumContextTokens > options.TrainingContextTokens;
        var nativeMaximum = experimental ? options.TrainingContextTokens!.Value : options.MaximumContextTokens;
        var selected = options.SelectedContextTokens;
        var extended = experimental && selected > nativeMaximum;
        var lastNative = selected <= nativeMaximum ? Math.Clamp(selected, 2048, nativeMaximum) : nativeMaximum;
        // Retire older extended presets in favour of the single 1M option.
        if (extended && !_running) selected = options.MaximumContextTokens;
        var changed = selected != options.SelectedContextTokens;
        var caption = new TextBlock { Text = $"Kontextlimit: {selected:N0}", FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ThemeBrush("MissumTextBrush", 248), Margin = new(0, 12, 0, 0) };
        panel.Children.Add(caption);
        panel.Children.Add(new TextBlock { Text = $"Modellmaximum: {nativeMaximum:N0}", FontSize = 12,
            Foreground = ThemeBrush("MissumMutedTextBrush", 185), HorizontalAlignment = HorizontalAlignment.Center });
        var slider = new Slider { Minimum = 2048, Maximum = experimental && !extended ? nativeMaximum : options.MaximumContextTokens,
            Value = experimental && extended ? options.MaximumContextTokens : selected,
            StepFrequency = 1, IsThumbToolTipEnabled = false, IsEnabled = !_running && !extended };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "Kontexttoken-Limit");
        panel.Children.Add(new NativeComposerSlider(slider));
        if (experimental)
        {
            const string extensionLabel = "Kontextvorgabe auf 1.048.576 Token erhöhen";
            static TextBlock CreateExtensionLabel() => new()
            {
                Text = extensionLabel, FontSize = 12, MaxWidth = 176, TextWrapping = TextWrapping.Wrap,
            };
            var toggle = new ToggleSwitch
            {
                OnContent = CreateExtensionLabel(), OffContent = CreateExtensionLabel(),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 0, IsOn = extended, IsEnabled = !_running, Margin = new(0, 4, 0, 0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, extensionLabel);
            var syncing = false;
            slider.ValueChanged += (_, args) =>
            {
                if (syncing || toggle.IsOn) return;
                selected = lastNative = (int)Math.Round(args.NewValue);
                changed = true;
                caption.Text = $"Kontextlimit: {selected:N0}";
            };
            toggle.Toggled += (_, _) =>
            {
                if (syncing) return;
                changed = true;
                syncing = true;
                if (toggle.IsOn)
                {
                    lastNative = Math.Clamp(selected, 2048, nativeMaximum);
                    slider.Maximum = options.MaximumContextTokens;
                    slider.Value = options.MaximumContextTokens;
                    selected = options.MaximumContextTokens;
                }
                else
                {
                    slider.Value = lastNative;
                    slider.Maximum = nativeMaximum;
                    selected = lastNative;
                }
                slider.IsEnabled = !_running && !toggle.IsOn;
                syncing = false;
                caption.Text = $"Kontextlimit: {selected:N0}";
            };
            panel.Children.Add(toggle);
        }
        else
        {
            slider.ValueChanged += (_, args) => { selected = (int)Math.Round(args.NewValue); changed = true; caption.Text = $"Kontextlimit: {selected:N0}"; };
        }
        if (_running) panel.Children.Add(new TextBlock { Text = "Nach Ende des laufenden Auftrags änderbar.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var lastRequested = options.SelectedContextTokens;
        Task<bool>? lastCommit = null;
        async Task<bool> SaveAsync(int value)
        {
            try
            {
                await _settings.UpdateAsync(current => current with { ContextLimitsByModel = new(current.ContextLimitsByModel, StringComparer.OrdinalIgnoreCase)
                    { [options.ModelId] = value } });
                if (!_disposed && ActiveSubagent is null && ModelRole == options.Role && _settings.Current.SelectedModel == options.ModelId)
                {
                    if (_reasoning?.ModelId == options.ModelId) _reasoning = _reasoning with { SelectedContextTokens = value };
                    UpdateContext(System.Text.Json.JsonSerializer.SerializeToElement(new { contextLimit = value }));
                }
                return true;
            }
            catch (Exception exception)
            {
                if (!_disposed) ShowError(exception.Message);
                return false;
            }
        }
        Task<bool> Commit()
        {
            if (!changed || lastCommit is not null && selected == lastRequested
                && (!ReferenceEquals(lastCommit, _contextLimitCommits.Pending) || !lastCommit.IsCompleted
                    || lastCommit.IsCompletedSuccessfully && lastCommit.Result))
                return _contextLimitCommits.Pending;
            lastRequested = selected;
            var value = selected;
            return lastCommit = _contextLimitCommits.Enqueue(() => SaveAsync(value));
        }
        Func<Task<bool>> commit = Commit;
        _commitOpenContextLimit = commit;
        flyout.Closed += async (_, _) =>
        {
            var save = commit();
            if (ReferenceEquals(_commitOpenContextLimit, commit)) _commitOpenContextLimit = null;
            await save;
        };
    }
}
