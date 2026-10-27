using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Flyout? _reasoningFlyout;
    private Flyout? _modelSelectionFlyout;
    private long _modelPickerGeneration;
    private string? _composerPickerOwner;

    private async void OnReasoningClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || ActiveSubagent is not null) return;
        if (_reasoningFlyout is { } open) { open.Hide(); return; }
        if (_modelSelectionFlyout is { } modelMenu) { modelMenu.Hide(); return; }
        var generation = ++_modelPickerGeneration;
        var model = _settings.Current.SelectedModel;
        var role = ModelRole;
        var options = _reasoning is { } cached && cached.ModelId == model && cached.Role == role ? cached : null;
        var flyout = CreateReasoningFlyout(options);
        ShowReasoningFlyout(flyout);
        if (options is not null) return;
        try
        {
            await LoadReasoningAsync();
            if (_disposed || generation != _modelPickerGeneration || !ReferenceEquals(_reasoningFlyout, flyout)
                || ActiveSubagent is not null || model != _settings.Current.SelectedModel || role != ModelRole) return;
            PopulateReasoningFlyout(flyout, _reasoning);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) ShowError(exception.Message); }
    }

    private void ShowReasoningFlyout(Flyout flyout)
    {
        _reasoningFlyout = flyout;
        _composerPickerOwner = ModelRole + ":" + _settings.Current.SelectedModel;
        flyout.Opened += (_, _) => RefreshModelPickerTrigger();
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_reasoningFlyout, flyout)) _reasoningFlyout = null;
            RefreshModelPickerTrigger();
        };
        flyout.ShowAt(ModelButton);
    }

    private void RefreshModelPickerTrigger()
    {
        if (_disposed) return;
        if (ActiveSubagent is not null || _composerPickerOwner != ModelRole + ":" + _settings.Current.SelectedModel)
        { _reasoningFlyout?.Hide(); _modelSelectionFlyout?.Hide(); }
        var open = _reasoningFlyout is not null || _modelSelectionFlyout is not null;
        ModelPickerActionLabel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ModelLabel.Visibility = ReasoningLabel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(ModelButton, "Aufwand auswählen: " + ModelLabel.Text
            + (ReasoningLabel.Text.Length > 0 ? ", " + ReasoningLabel.Text : ""));
        AutomationProperties.SetHelpText(ModelButton, "Modell, Denkaufwand und Kontextlimit auswählen");
    }

    private static Flyout ComposerPickerFlyout()
    {
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, NativeThemeBrushes.Resource("MissumLayerStrongBrush", 43)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(16)));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        presenter.Setters.Add(new Setter(Control.BorderBrushProperty, NativeThemeBrushes.Resource("MissumStrokeBrush", 62)));
        presenter.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 320d));
        return new Flyout { Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            AreOpenCloseAnimationsEnabled = true, FlyoutPresenterStyle = presenter };
    }

    private Flyout CreateReasoningFlyout(ComposerReasoningOptions? options)
    {
        var flyout = ComposerPickerFlyout();
        PopulateReasoningFlyout(flyout, options);
        return flyout;
    }

    private void PopulateReasoningFlyout(Flyout flyout, ComposerReasoningOptions? options)
    {
        var panel = new StackPanel { Width = 232, Spacing = 0 };
        var heading = new Grid { ColumnSpacing = 6, Margin = new(0, 0, 0, 6) };
        var caption = new TextBlock { Text = options?.Available == true ? EffortLabel(options.Selected) : "Automatisch",
            FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = NativeThemeBrushes.ReadableAccent, HorizontalAlignment = HorizontalAlignment.Center };
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 3 };
        modelRow.Children.Add(new TextBlock { Text = ModelLabel.Text, FontSize = 12, MaxWidth = 144,
            Foreground = NativeThemeBrushes.MutedText, TextTrimming = TextTrimming.CharacterEllipsis });
        modelRow.Children.Add(new FontIcon { Glyph = "\uE76C", FontSize = 8,
            Foreground = NativeThemeBrushes.MutedText, VerticalAlignment = VerticalAlignment.Center });
        var modelCaption = new StackPanel { Spacing = 0 };
        modelCaption.Children.Add(caption); modelCaption.Children.Add(modelRow);
        var chooseModel = new Button { Content = modelCaption, BorderThickness = new(0), CornerRadius = new(6),
            Background = NativeThemeBrushes.Resource("MissumHoverBrush", 64), Padding = new(8, 3, 8, 3),
            MinHeight = 40, MinWidth = 102, MaxWidth = 164, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(chooseModel, "Modell auswählen");
        ToolTipService.SetToolTip(chooseModel, ModelLabel.Text + " · Modell auswählen");
        heading.Children.Add(chooseModel);
        panel.Children.Add(heading);

        string? selected = options?.Selected;
        Task? pendingSave = null;
        Task SaveSelectionAsync() => pendingSave ??= SaveReasoningChoiceAsync(options, selected);
        chooseModel.Click += async (_, _) =>
        {
            var save = SaveSelectionAsync();
            flyout.Hide();
            await save;
            if (!_disposed && ActiveSubagent is null) await OpenModelSelectionAsync();
        };
        if (options?.Available == true && options.Levels.Count > 0)
        {
            string[] order = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "on"];
            var levels = options.Levels.OrderBy(level => { var index = Array.IndexOf(order, level); return index < 0 ? int.MaxValue : index; }).ToArray();
            var slider = new Slider { Minimum = 0, Maximum = Math.Max(1, levels.Length - 1), StepFrequency = 1,
                TickFrequency = 1, TickPlacement = TickPlacement.None, IsThumbToolTipEnabled = false,
                IsEnabled = levels.Length > 1, Value = Math.Max(0, Array.IndexOf(levels, selected)) };
            AutomationProperties.SetName(slider, "Reasoning-Aufwand");
            slider.ValueChanged += (_, args) =>
            {
                selected = levels[Math.Clamp((int)Math.Round(args.NewValue), 0, levels.Length - 1)];
                caption.Text = EffortLabel(selected);
                ToolTipService.SetToolTip(slider, caption.Text);
            };
            panel.Children.Add(new NativeComposerSlider(slider, levels.Length));
            flyout.Closed += async (_, _) => await SaveSelectionAsync();
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = options?.Detail ?? "Modellinformationen werden geladen …", FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Foreground = NativeThemeBrushes.MutedText });
        }
        AddContextLimitSlider(panel, flyout, options);
        flyout.Content = panel;
    }

    private async Task SaveReasoningChoiceAsync(ComposerReasoningOptions? options, string? selected)
    {
        if (_disposed || options is null || selected is null || selected == options.Selected
            || ActiveSubagent is not null || _settings.Current.SelectedModel != options.ModelId) return;
        try { await CommandAsync("reasoning.set", new { modelId = options.ModelId, role = options.Role, effort = selected }); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) ShowError(exception.Message); }
    }

    private async Task OpenModelSelectionAsync()
    {
        var role = ModelRole;
        var generation = ++_modelPickerGeneration;
        var flyout = CreateModelSelectionFlyout([]);
        _modelSelectionFlyout = flyout;
        _composerPickerOwner = role + ":" + _settings.Current.SelectedModel;
        flyout.Opened += (_, _) => RefreshModelPickerTrigger();
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_modelSelectionFlyout, flyout)) _modelSelectionFlyout = null;
            RefreshModelPickerTrigger();
        };
        flyout.ShowAt(ModelButton);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = await App.Current.GetService<MissumAiConnectionService>().CreateClientAsync(timeout.Token);
            var catalog = JsonSerializer.SerializeToElement(await client.GetModelStatusAsync(timeout.Token), JsonOptions);
            if (_disposed || generation != _modelPickerGeneration || !ReferenceEquals(_modelSelectionFlyout, flyout)
                || role != ModelRole || ActiveSubagent is not null) return;
            var models = Items(catalog, "models").Where(model => S(model, "role") is "general" or "coding" && S(model, "downloaded") == "True")
                .GroupBy(model => S(model, "id")).Select(group => group.FirstOrDefault(model => S(model, "role") == role)
                    is { ValueKind: JsonValueKind.Object } matching ? matching : group.First()).ToArray();
            PopulateModelSelectionFlyout(flyout, models, models.Length == 0 ? "Keine heruntergeladenen Modelle verfügbar." : null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_disposed && ReferenceEquals(_modelSelectionFlyout, flyout))
                PopulateModelSelectionFlyout(flyout, [], "Modellliste nicht verfügbar. " + (exception is OperationCanceledException ? "Bitte erneut öffnen." : exception.Message));
        }
    }

    private Flyout CreateModelSelectionFlyout(JsonElement[] models)
    {
        var flyout = ComposerPickerFlyout();
        PopulateModelSelectionFlyout(flyout, models);
        return flyout;
    }

    private void PopulateModelSelectionFlyout(Flyout flyout, JsonElement[] models, string? notice = null)
    {
        var panel = new StackPanel { Width = 232, Spacing = 0 };
        panel.Children.Add(new TextBlock { Text = "Modell auswählen", FontSize = 13, Foreground = NativeThemeBrushes.MutedText,
            Margin = new(0, 2, 0, 8) });
        var rows = new StackPanel { Spacing = 0 };
        foreach (var model in models)
        {
            var id = S(model, "id");
            var title = S(model, "displayName", id);
            var layout = new Grid { ColumnSpacing = 8 };
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
            layout.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = NativeThemeBrushes.Text,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (id == _settings.Current.SelectedModel)
            {
                var check = new FontIcon { Glyph = "\uE73E", FontSize = 13, Foreground = NativeThemeBrushes.Text,
                    VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 1); layout.Children.Add(check);
            }
            var button = new Button { Content = layout, Tag = id, Padding = new(0, 6, 0, 6), MinHeight = 29,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0), CornerRadius = new(5) };
            AutomationProperties.SetName(button, title);
            AutomationProperties.SetHelpText(button, id == _settings.Current.SelectedModel ? "Ausgewähltes Modell" : "Dieses Modell verwenden");
            ToolTipService.SetToolTip(button, title);
            button.Click += async (_, _) =>
            {
                flyout.Hide();
                await SelectComposerModelAsync(id);
            };
            rows.Children.Add(button);
        }
        if (models.Length == 0)
            rows.Children.Add(new TextBlock { Text = notice ?? "Modelle werden geladen …", FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Foreground = NativeThemeBrushes.MutedText, Margin = new(0, 4, 0, 6) });
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 340, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch });
        flyout.Content = panel;
    }

    private async Task SelectComposerModelAsync(string modelId)
    {
        if (_disposed || ActiveSubagent is not null || modelId.Length == 0 || _settings.Current.SelectedModel == modelId) return;
        try
        {
            if (_settings.Current.SelectedModel != modelId)
                await _settings.UpdateAsync(settings => settings with { SelectedModel = modelId, SelectedCodingModel = modelId });
            _reasoningModel = "";
            await App.Current.GetService<MissumAiAssistantService>().RequestLiveModelSelectionAsync(_lifetime.Token);
            await RefreshForExternalActivationAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) ShowError(exception.Message); }
    }
}
