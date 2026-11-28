using Missum.App.Services.AiStack;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

/// <summary>
/// "Lokaler KI-Stack": checks the requirements and runs the in-app setup (images are built on this PC,
/// worker models downloaded). The setup itself lives in <see cref="AiStackSetupService"/> and keeps
/// running when this page closes; the page only shows its state and progress.
/// </summary>
public sealed partial class SettingsPage
{
    private AiStackSetupService? _aiStack;
    private string? _lastAiStackLine;

    private AiStackSetupService AiStack => _aiStack ??= App.Current.GetService<AiStackSetupService>();

    private AiStackComponents SelectedAiStackComponents =>
        (AiStackSpeechBox.IsChecked == true ? AiStackComponents.Speech : AiStackComponents.None)
        | (AiStackImageBox.IsChecked == true ? AiStackComponents.Image : AiStackComponents.None);

    /// <summary>Called on load: shows the installed state, the requirements and a setup that may still be running.</summary>
    private async Task InitializeAiStackCardAsync()
    {
        AiStackSpeechBox.Content = $"Sprache: Transkription, Sprechererkennung, Sprachausgabe (etwa {AiStackSetupService.FormatBytes(AiStackSetupService.RequiredBytes(AiStackComponents.Speech) - AiStackSetupService.RequiredBytes(AiStackComponents.None))} zusätzlich)";
        AiStackImageBox.Content = $"Bilder erzeugen (etwa {AiStackSetupService.FormatBytes(AiStackSetupService.RequiredBytes(AiStackComponents.Image) - AiStackSetupService.RequiredBytes(AiStackComponents.None))} zusätzlich, CUDA-Build dauert länger)";
        var state = AiStack.ReadState();
        if (state is not null)
        {
            AiStackSpeechBox.IsChecked = state.Components.HasFlag(AiStackComponents.Speech);
            AiStackImageBox.IsChecked = state.Components.HasFlag(AiStackComponents.Image);
        }
        AiStack.ProgressChanged -= OnAiStackProgress;
        AiStack.ProgressChanged += OnAiStackProgress;
        if (AiStack.IsRunning)
        {
            ShowAiStackRun(AiStack.RecentLines, AiStack.LastProgress);
            _ = WatchAiStackRunAsync(AiStack.CurrentRun!);
            return;
        }
        await RefreshAiStackAsync();
    }

    private void DetachAiStackCard()
    {
        if (_aiStack is not null) _aiStack.ProgressChanged -= OnAiStackProgress;
    }

    private async Task RefreshAiStackAsync()
    {
        AiStackStatusText.Text = "Wird geprüft …";
        SetAiStackBusy(false, checking: true);
        try
        {
            var installed = await AiStack.IsInstalledAsync(CancellationToken.None);
            var state = AiStack.ReadState();
            AiStackStatusText.Text = installed
                ? "Eingerichtet" + (state is null ? "" : $" · Version {state.ImageVersion}")
                : "Nicht eingerichtet";
            AiStackInstallButton.Content = installed ? "Aktualisieren / Komponenten ergänzen" : "Einrichten";
            var report = await AiStack.CheckRequirementsAsync(SelectedAiStackComponents, CancellationToken.None);
            AiStackRequirementsPanel.Children.Clear();
            foreach (var item in report.Items)
                AiStackRequirementsPanel.Children.Add(RequirementRow(item));
            AiStackInstallButton.IsEnabled = report.CanInstall;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AiStackStatusText.Text = "Prüfung fehlgeschlagen";
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
        finally { SetAiStackBusy(AiStack.IsRunning, checking: false); }
    }

    private static Grid RequirementRow(AiStackRequirement item)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new(18) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var ok = item.Met;
        row.Children.Add(new FontIcon
        {
            Glyph = ok ? "" : item.Blocking ? "" : "", FontSize = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 3, 0, 0),
            Foreground = Controls.NativeIconPalette.BrushFor(ok ? "success" : "danger"),
        });
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = item.Name + ": ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = item.Detail });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private async void OnAiStackCheck(object sender, RoutedEventArgs e) => await RefreshAiStackAsync();

    private async void OnAiStackInstall(object sender, RoutedEventArgs e)
    {
        ShowAiStackRun([], null);
        await WatchAiStackRunAsync(AiStack.StartInstall(SelectedAiStackComponents));
    }

    private void OnAiStackCancel(object sender, RoutedEventArgs e)
    {
        AiStack.CancelInstall();
        AiStackCancelButton.IsEnabled = false;
    }

    private async Task WatchAiStackRunAsync(Task run)
    {
        SetAiStackBusy(true, checking: false);
        try
        {
            await run;
            ShowStatus("Der lokale KI-Stack ist eingerichtet und gestartet.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { ShowStatus("Die Einrichtung wurde abgebrochen. Ein erneuter Start setzt an derselben Stelle fort.", InfoBarSeverity.Warning); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (IsLoaded) await RefreshAiStackAsync();
        }
    }

    private void OnAiStackProgress(AiStackSetupProgress step) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsLoaded) return;
        AiStackStepText.Visibility = Visibility.Visible;
        AiStackStepText.Text = step.Message;
        AiStackProgress.Visibility = Visibility.Visible;
        AiStackProgress.IsIndeterminate = step.Fraction is null;
        if (step.Fraction is { } fraction) AiStackProgress.Value = fraction;
        AppendAiStackLine(step.Message);
    });

    private void ShowAiStackRun(IReadOnlyList<string> lines, AiStackSetupProgress? last)
    {
        AiStackLogBox.Visibility = Visibility.Visible;
        AiStackLogBox.Text = string.Join("\n", lines);
        _lastAiStackLine = lines.Count > 0 ? lines[^1] : null;
        AiStackProgress.Visibility = Visibility.Visible;
        AiStackProgress.IsIndeterminate = last?.Fraction is null;
        AiStackStepText.Visibility = Visibility.Visible;
        AiStackStepText.Text = last?.Message ?? "Einrichtung startet …";
    }

    private void AppendAiStackLine(string line)
    {
        if (line == _lastAiStackLine) return;
        _lastAiStackLine = line;
        // Keep the box small: the full output is in the setup log file.
        var text = AiStackLogBox.Text.Length > 60_000 ? AiStackLogBox.Text[^40_000..] : AiStackLogBox.Text;
        AiStackLogBox.Text = text.Length == 0 ? line : text + "\n" + line;
        AiStackLogBox.SelectionStart = AiStackLogBox.Text.Length;
        if (FindScrollViewer(AiStackLogBox) is { } viewer) viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
    }

    private void SetAiStackBusy(bool running, bool checking)
    {
        AiStackInstallButton.IsEnabled = !running && !checking && AiStackInstallButton.IsEnabled;
        AiStackCheckButton.IsEnabled = !running && !checking;
        AiStackSpeechBox.IsEnabled = AiStackImageBox.IsEnabled = !running;
        AiStackCancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        AiStackCancelButton.IsEnabled = running;
        if (!running && !checking) AiStackProgress.IsIndeterminate = false;
    }

    /// <summary>Scrolls the settings page to the AI stack card (used when Missum starts for the first setup).</summary>
    internal void BringAiStackCardIntoView() => AiStackCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
