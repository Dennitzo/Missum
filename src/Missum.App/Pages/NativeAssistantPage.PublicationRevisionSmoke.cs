using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyPublicationRevisionSmokeAsync(string fixture)
    {
        if (!IsMessageFooterSmoke) throw new InvalidOperationException("Publication revision smoke needs an isolated profile.");
        var previousOwner = _session;
        var previousMode = _mode;
        var previousProvider = _sciencePresentationSmokeProvider;
        var owner = Guid.NewGuid();
        var projectId = "publication-revision-" + owner.ToString("N");
        var path = Path.Combine(App.Current.DataDirectory, projectId + ".pdf");
        File.Copy(fixture, path);
        var publication = new ScientificPublicationArtifact(projectId, 1, path + ".md", path,
            true, DateTimeOffset.UtcNow, "revision-1");
        ScientificPresentationSnapshot presentation = new(1, publication, null);
        try
        {
            _sciencePresentationSmokeProvider = id => id == projectId ? presentation : previousProvider?.Invoke(id);
            _session = owner; _mode = "claudescience";
            var state = ResearchState(owner);
            state.Loaded = state.Available = true;
            state.SelectedProjectId = state.RequestedProjectId = projectId;
            state.Projects = [JsonSerializer.SerializeToElement(new { id = projectId, originalQuestion = "PDF-Fassungen" })];
            _researchLoading.Add(owner);
            QueueResearchTabSelection();
            await WaitForPublicationSmokeAsync(() => _publicationViews.TryGetValue(owner, out var view) && view.HasCompletedNavigation,
                "Die erste Publikationsfassung wurde nicht geladen.");
            var viewer = _publicationViews[owner];
            var parent = viewer.Parent;
            var initial = viewer.PdfSource;

            ShowChatView();
            await File.AppendAllTextAsync(path, "\n% Missum revised PDF fixture 2\n", _lifetime.Token);
            publication = publication with { Revision = 2, ContentHash = "revision-2", UpdatedAt = DateTimeOffset.UtcNow };
            presentation = presentation with { Version = 2, Publication = publication };
            QueueResearchTabSelection();
            await WaitForPublicationSmokeAsync(() => viewer.HasCompletedNavigation && viewer.NavigationCount == 2,
                "Beim Tabaufruf blieb die überschriebene PDF-Fassung veraltet.");
            if (viewer.PdfSource == initial) throw new InvalidOperationException("Die neue PDF-Fassung benutzt die alte Cacheadresse.");

            // A new coordinator revision at the same path must also invalidate
            // the page's render signature while the tab remains visible.
            publication = publication with { Revision = 3, ContentHash = "revision-3", UpdatedAt = DateTimeOffset.UtcNow };
            presentation = presentation with { Version = 3, Publication = publication };
            RenderResearchView();
            await WaitForPublicationSmokeAsync(() => viewer.HasCompletedNavigation && viewer.NavigationCount == 3,
                "Die Publikationssignatur ignoriert eine neuere Fassung am selben Dateipfad.");
            for (var index = 0; index < 6; index++) { RenderResearchView(); await Task.Delay(30, _lifetime.Token); }
            if (viewer.NavigationCount != 3) throw new InvalidOperationException("Unveränderte Publikationsabfragen laden die PDF ständig neu.");

            // Cover a file replacement before the next presentation snapshot.
            ShowChatView();
            await File.AppendAllTextAsync(path, "\n% Missum revised PDF fixture 4\n", _lifetime.Token);
            QueueResearchTabSelection();
            await WaitForPublicationSmokeAsync(() => viewer.HasCompletedNavigation && viewer.NavigationCount == 4,
                "Der Tabaufruf prüft die PDF-Datei bei unverändertem Snapshot nicht erneut.");
            if (viewer.Parent != parent || viewer.BrowserFaultCount != 0 || !viewer.IsInitialized)
                throw new InvalidOperationException("Die PDF-Aktualisierung hat die bestehende Browseransicht verworfen.");
            var rendered = false;
            var capturePath = Path.Combine(App.Current.DataDirectory, "native-publication-revision-preview.png");
            for (var attempt = 0; attempt < 20 && !rendered; attempt++)
            {
                // Edge's internal PDF DOM varies across WebView2 versions.
                // Inspect the actual pixels instead of depending on private tags.
                await Task.Delay(250, _lifetime.Token);
                await viewer.CapturePreviewAsync(capturePath);
                var image = await StorageFile.GetFileFromPathAsync(capturePath).AsTask(_lifetime.Token);
                using var stream = await image.OpenReadAsync().AsTask(_lifetime.Token);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(_lifetime.Token);
                var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
                    .AsTask(_lifetime.Token)).DetachPixelData();
                var colors = new HashSet<uint>();
                for (var pixel = 0; pixel + 3 < pixels.Length && colors.Count < 8; pixel += 4 * 131)
                    colors.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(pixel, 4)));
                rendered = colors.Count >= 8;
            }
            if (!rendered) throw new InvalidOperationException("Die aktuelle PDF-Navigation enthält noch keinen gezeichneten PDF-Viewer.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-publication-revision-validation.json"),
                JsonSerializer.Serialize(new { passed = true, samePathReload = true, visibleRevisionReload = true,
                    fileRevalidatedOnTabOpen = true, unchangedPollingRetainsViewer = true, renderedPdfViewer = rendered, viewer.NavigationCount,
                    viewer.BrowserFaultCount, sameParent = true }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            HideResearchView();
            if (_publicationViews.Remove(owner, out var viewer))
            { await viewer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); _scienceBody?.Children.Remove(viewer); }
            _researchLoading.Remove(owner); _researchSessions.Remove(owner);
            _sciencePresentationSmokeProvider = previousProvider;
            _session = previousOwner; _mode = previousMode; _scienceViewSignature = null;
            ShowChatView(); RenderSessionTabs();
        }
    }
    private async Task WaitForPublicationSmokeAsync(Func<bool> predicate, string error)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!predicate())
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(error);
            await Task.Delay(20, _lifetime.Token);
        }
    }
}
