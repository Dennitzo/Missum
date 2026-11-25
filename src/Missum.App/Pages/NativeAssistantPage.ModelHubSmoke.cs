using System.Text.Json;
using Microsoft.UI.Xaml;
using Missum.App.Services.ModelHub;

namespace Missum.App.Pages;

/// <summary>
/// MISSUM_SMOKE_UI_MODELHUB=1: switches to the Model Hub against the live Hugging Face API,
/// checks list, search, model card, quantizations with fit badges and the absent prompt window,
/// then downloads a small quantization into a temporary folder with pause and resume.
/// Writes native-modelhub-validation.json and PNG captures in light and dark.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private async Task VerifyModelHubSmokeAsync()
    {
        if (App.Current.MainWindow is { } window) window.AppWindow.Resize(SmokeWindowSize(1640, 1000));
        var checks = new Dictionary<string, object?>();
        var report = new Dictionary<string, object?> { ["checks"] = checks };
        await NavigateAsync("mode.switch", new { chatMode = ModelHubMode });
        try { await WaitForLongAsync(() => IsModelHubMode && _hubHost is { Visibility: Visibility.Visible }, 30, "Model Hub wurde nicht angezeigt"); }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Model Hub wurde nicht angezeigt: mode={_mode}, host={(_hubHost is null ? "null" : _hubHost.Visibility.ToString())}, body={BodyGrid.Visibility}, session={_session}, error={string.Join(" | ", _chatErrors.Values)}");
        }
        checks["noPromptWindow"] = BodyGrid.Visibility == Visibility.Collapsed;
        checks["noProjectsHeader"] = ProjectsHeader.Visibility == Visibility.Collapsed;

        // Newest unsloth GGUF models first.
        await WaitForLongAsync(() => _hubModels.Count >= 10, 60, "Modellliste kam nicht von Hugging Face");
        checks["onlyUnsloth"] = _hubModels.All(m => m.Id.StartsWith("unsloth/", StringComparison.OrdinalIgnoreCase));
        checks["newestFirst"] = _hubModels.Take(10).Zip(_hubModels.Skip(1).Take(9)).All(pair => pair.First.LastModified >= pair.Second.LastModified);
        report["firstModels"] = _hubModels.Take(5).Select(m => m.Id + " " + m.LastModified.ToString("O")).ToList();

        // The first model opens with its quantizations and card.
        await WaitForLongAsync(() => _hubDetails is not null && _hubSystem is not null, 60, "Modelldetails kamen nicht");
        await WaitForLongAsync(() => _hubReadme is { Rendered: true }, 40, "Beschreibung wurde nicht gerendert");
        checks["quantizationList"] = _hubQuantizations.Count > 0 && _hubQuantPanel?.Children.Count == _hubQuantizations.Count;
        checks["readmeHeight"] = _hubReadme!.ContentHeight > 200;
        // The card takes the page background and passes wheel turns to the page.
        var frame = (AssistantFrame.Background as Microsoft.UI.Xaml.Media.SolidColorBrush)!.Color;
        checks["readmeBackground"] = _hubReadme.RenderedBackground == $"#{frame.R:X2}{frame.G:X2}{frame.B:X2}";
        report["readmeBackground"] = _hubReadme.RenderedBackground;
        _hubScroll!.ChangeView(null, 0, null, true);
        await Task.Delay(300);
        var before = _hubScroll.VerticalOffset;
        await _hubReadme.WheelForSmokeAsync(400);
        await WaitForLongAsync(() => _hubScroll.VerticalOffset > before + 100, 10, "Mausrad über der Beschreibung scrollte nicht");
        checks["wheelScrollsPage"] = true;
        _hubScroll.ChangeView(null, 0, null, true);
        report["system"] = _hubSystem!.Summary;
        report["fits"] = _hubQuantizations.Select(q => $"{q.Label} {ModelHubFormat.Gigabytes(q.TotalBytes)} → {ModelFitEstimator.Estimate(q, _hubDetails!.Parameters, _hubSystem).Label}").ToList();
        checks["panelOnlyDownloads"] = _hubPanel is { Visibility: Visibility.Visible } && InspectorScroll.Visibility == Visibility.Collapsed && MeetingChatHost.Visibility == Visibility.Collapsed;
        await Task.Delay(600);
        report["dark"] = await CaptureWebdesignAsync("modelhub-1-dark.png");
        App.Current.ApplyTheme(Missum.Core.Models.AppTheme.Light);
        await Task.Delay(1500);
        report["light"] = await CaptureWebdesignAsync("modelhub-2-light.png");
        App.Current.ApplyTheme(Missum.Core.Models.AppTheme.Dark);
        await Task.Delay(800);

        // Search finds a small model; the folder is a temporary one the gateway does not scan.
        var folder = Path.Combine(App.Current.DataDirectory, "ModelHubSmokeDownloads");
        Directory.CreateDirectory(folder);
        await SetHubFolderAsync(folder);
        checks["folderShown"] = _hubFolderText?.Text == folder;
        checks["gatewayHintShown"] = _hubUseAsModelFolder?.Visibility == Visibility.Visible;
        _hubSearchBox!.Text = "SmolLM2-135M";
        await WaitForLongAsync(() => _hubSearch == "SmolLM2-135M" && !_hubListLoading && _hubModels.Count > 0, 40, "Suche lieferte nichts");
        checks["searchMatches"] = _hubModels.All(m => m.Id.Contains("SmolLM2-135M", StringComparison.OrdinalIgnoreCase));
        await SelectHubModelAsync(_hubModels[0].Id);
        await WaitForLongAsync(() => _hubDetails is not null && _hubQuantizations.Count > 0, 40, "Kleines Modell lud nicht");
        var smallest = _hubQuantizations.OrderBy(q => q.TotalBytes).First();
        report["download"] = _hubDetails!.Id + " " + smallest.Label + " " + ModelHubFormat.Gigabytes(smallest.TotalBytes);
        if (smallest.TotalBytes > 400_000_000) throw new InvalidOperationException("Kleinste Quantisierung ist zu groß für den Smoke: " + smallest.TotalBytes);

        await StartHubDownloadAsync(_hubDetails, smallest, null);
        var job = Downloads.Find(_hubDetails.Id, _hubDetails.Commit, smallest.Label)!;
        await WaitForLongAsync(() => job.DoneBytes > smallest.TotalBytes / 10 || job.State == ModelDownloadState.Completed, 120, "Download startete nicht");
        if (job.State != ModelDownloadState.Completed)
        {
            Downloads.Pause(job.Id);
            await WaitForLongAsync(() => job.State == ModelDownloadState.Paused, 20, "Pause griff nicht");
            checks["paused"] = true;
            report["pausedAt"] = HubJobLine(job);
            RenderModelHubPanel();
            await Task.Delay(400);
            report["pausedShot"] = await CaptureWebdesignAsync("modelhub-3-paused.png");
            Downloads.Resume(job.Id);
        }
        await WaitForLongAsync(() => job.State is ModelDownloadState.Completed or ModelDownloadState.Failed, 300, "Download wurde nicht fertig");
        checks["completed"] = job.State == ModelDownloadState.Completed;
        report["error"] = job.Error;
        checks["placedLikeHubCache"] = ModelDownloadService.IsInstalled(folder, _hubDetails.Id, _hubDetails.Commit, smallest)
            && File.Exists(Path.Combine(ModelHubCatalogId.RepositoryFolder(folder, _hubDetails.Id), "refs", "main"));
        checks["noPartLeft"] = !Directory.EnumerateFiles(ModelHubCatalogId.HubRoot(folder), "*.part", SearchOption.AllDirectories).Any();
        // The folder scan finds it; without a search it is listed under "Im Download-Ordner".
        await WaitForLongAsync(() => HubLocalModels.Any(local => local.Id == _hubDetails.Id), 20, "Download-Ordner wurde nicht erkannt");
        _hubSearchBox.Text = "";
        await WaitForLongAsync(() => _hubSearch.Length == 0 && !_hubListLoading && _hubModels.Count > 0, 40, "Liste kam nicht zurück");
        checks["localSection"] = ProjectsPanel.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t => t.Text == "Im Download-Ordner");
        report["catalogId"] = job.CatalogId;
        RenderHubQuantizations();
        // The downloaded quantization's button reads "Vorhanden" and does nothing.
        checks["presentButton"] = HubQuantizationStatus(_hubDetails, smallest, job).State == LocalQuantizationState.Current
            && Descendants(_hubQuantPanel!).OfType<Microsoft.UI.Xaml.Controls.Button>()
                .Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b) == smallest.Label + ": Vorhanden" && !b.IsEnabled);
        RenderModelHubPanel();
        await Task.Delay(600);
        report["done"] = await CaptureWebdesignAsync("modelhub-4-downloaded.png");

        // Real copies in the default folder (if any): which quantizations are present or need an update.
        await SetHubFolderAsync(ModelHubCatalogId.DefaultFolder);
        if (HubLocalModels.Count > 0 && HubLocalModels[0] is { } existing)
        {
            await SelectHubModelAsync(existing.Id);
            await WaitForLongAsync(() => _hubDetails?.Id == existing.Id && !_hubDetailLoading, 60, "Vorhandenes Modell lud nicht");
            report["existingModel"] = existing.Id + " @ " + _hubDetails!.Commit[..8] + " local " + string.Join(",", existing.Files.Select(f => Path.GetFileName(f.SnapshotFolder)[..8]).Distinct());
            report["existingStates"] = _hubQuantizations.Select(q => q.Label + " " + HubQuantizationStatus(_hubDetails, q).State)
                .Where(line => !line.EndsWith(" Missing", StringComparison.Ordinal)).ToList();
            await Task.Delay(800);
            report["existingShot"] = await CaptureWebdesignAsync("modelhub-5-existing.png");
        }

        // Back to ChatGPT: the prompt window and the normal sidebar return.
        await NavigateAsync("mode.switch", new { chatMode = "general" });
        await WaitForLongAsync(() => !IsModelHubMode && BodyGrid.Visibility == Visibility.Visible, 20, "Chat kam nicht zurück");
        checks["chatRestored"] = ProjectsHeader.Visibility == Visibility.Visible && _hubHost?.Visibility != Visibility.Visible;

        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-modelhub-validation.json"),
            JsonSerializer.Serialize(report, LiveReportJson));
        if (checks.Any(pair => pair.Value is false))
            throw new InvalidOperationException("Model Hub check failed: " + JsonSerializer.Serialize(checks));
    }

    private static async Task WaitForLongAsync(Func<bool> condition, int seconds, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure);
            await Task.Delay(150);
        }
    }
}
