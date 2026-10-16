using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _scienceViewSignature;
    private readonly Dictionary<Guid, NativePublicationView> _publicationViews = [];
    private readonly Dictionary<string, FrameworkElement> _scienceEmptyBodies = new(StringComparer.Ordinal);
    private Grid? _scienceRoot, _scienceBody;
    private InfoBar? _scienceNotice;
    private bool _scienceRendering, _scienceRenderAgain;
    private Func<string, ScientificPresentationSnapshot?>? _sciencePresentationSmokeProvider;

    private void RenderResearchView()
    {
        if (_disposed || _activeResearchSessionId != _session || _mode != "claudescience") return;
        if (_scienceRendering) { _scienceRenderAgain = true; return; }
        _scienceRendering = true;
        try { RenderResearchViewCore(); }
        finally
        {
            _scienceRendering = false;
            if (_scienceRenderAgain) { _scienceRenderAgain = false; _scienceViewSignature = null; }
        }
    }

    private void RenderResearchViewCore()
    {
        var owner = _session;
        var state = ResearchState(owner);
        var presentation = state.SelectedProjectId is { } id ? SciencePresentationSnapshot(id) : null;
        var contentKey = presentation?.Publication is { } publication ? PublicationContentKey(publication) : null;
        var signature = $"{owner}|{state.SelectedProjectId}|{contentKey}|{state.Error}|{presentation?.PublicationError}|{state.Loaded}";
        if (signature == _scienceViewSignature && ReferenceEquals(ResearchHost.Content, _scienceRoot))
        { UpdatePublicationActivity(); return; }
        EnsureScienceVisualTree();
        var error = state.Error.Length > 0 ? state.Error : presentation?.PublicationError;
        _scienceNotice!.Message = error ?? "";
        _scienceNotice.IsOpen = !string.IsNullOrWhiteSpace(error);
        _scienceNotice.Visibility = _scienceNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement body;
        if (presentation?.Publication is { } pdf)
        {
            if (!_publicationViews.TryGetValue(owner, out var viewer)) _publicationViews[owner] = viewer = new NativePublicationView();
            if (viewer.Parent is null) _scienceBody!.Children.Add(viewer);
            body = viewer;
            _ = LoadPublicationAsync(viewer, pdf, owner, state.SelectedProjectId!);
        }
        else
        {
            var key = owner + "|" + (state.SelectedProjectId is null ? "no-project" : "publication-pending");
            if (!_scienceEmptyBodies.TryGetValue(key, out body!))
            {
                var content = new StackPanel { Spacing = 10, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(new TextBlock { Text = state.SelectedProjectId is null
                    ? "Öffne ein Projekt und beginne im Chat." : "Noch keine Publikation vorhanden.", FontSize = 20,
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                content.Children.Add(new TextBlock { Text = "Die Publikation wird direkt im Projekt bearbeitet. Hier erscheint ihre aktuelle PDF-Ansicht.",
                    FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                body = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 0, 16) };
                _scienceEmptyBodies.Add(key, body); _scienceBody!.Children.Add(body);
            }
        }
        // The PDF browser keeps one visual parent across project and tab changes.
        foreach (var child in _scienceBody!.Children.OfType<FrameworkElement>())
            child.Visibility = ReferenceEquals(child, body) ? Visibility.Visible : Visibility.Collapsed;
        _scienceViewSignature = signature;
        UpdatePublicationActivity();
    }

    private ScientificPresentationSnapshot? SciencePresentationSnapshot(string projectId) =>
        _sciencePresentationSmokeProvider is { } smoke ? smoke(projectId)
            : App.Current.GetService<ScientificPresentationCoordinator>().GetSnapshot(projectId);

    private void EnsureScienceVisualTree()
    {
        if (_scienceRoot is null)
        {
            _scienceRoot = new Grid();
            _scienceRoot.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _scienceRoot.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            _scienceNotice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Warning, Visibility = Visibility.Collapsed };
            NativeNotice.Attach(_scienceNotice);
            _scienceRoot.Children.Add(_scienceNotice);
            _scienceBody = new Grid(); Grid.SetRow(_scienceBody, 1); _scienceRoot.Children.Add(_scienceBody);
        }
        if (!ReferenceEquals(ResearchHost.Content, _scienceRoot)) ResearchHost.Content = _scienceRoot;
    }

    private void UpdatePublicationActivity()
    {
        var visible = !_disposed && _mode == "claudescience" && _activeResearchSessionId == _session
            && ResearchHost.Visibility == Visibility.Visible && ReferenceEquals(ResearchHost.Content, _scienceRoot);
        foreach (var (owner, viewer) in _publicationViews)
            viewer.SetActive(visible && owner == _session && viewer.Parent == _scienceBody && viewer.Visibility == Visibility.Visible);
    }

    private static string PublicationContentKey(ScientificPublicationArtifact pdf) =>
        $"{pdf.PdfPath}|{pdf.Revision}|{pdf.ContentHash}|{pdf.UpdatedAt.UtcTicks}";

    private async Task LoadPublicationAsync(NativePublicationView view, ScientificPublicationArtifact pdf, Guid owner, string projectId)
    {
        try { await view.LoadAsync(pdf.PdfPath, projectId, PublicationContentKey(pdf), _lifetime.Token); }
        catch (OperationCanceledException) { /* A newer PDF supersedes an in-flight load. */ }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
            && exception.HResult != unchecked((int)0x8007000E))
        {
            if (ResearchState(owner).SelectedProjectId == projectId
                && SciencePresentationSnapshot(projectId)?.Publication is { } current
                && PublicationContentKey(current) == PublicationContentKey(pdf))
                ShowResearchError(owner, "Das PDF konnte nicht angezeigt werden: " + exception.Message);
        }
    }

    private void DisposeScienceViews()
    {
        foreach (var viewer in _publicationViews.Values) viewer.Dispose();
        _publicationViews.Clear();
        _scienceEmptyBodies.Clear();
    }
}
