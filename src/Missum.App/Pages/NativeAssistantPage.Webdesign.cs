using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Missum.App.Controls;
using Missum.App.Services.Webdesign;

namespace Missum.App.Pages;

/// <summary>
/// Webdesign mode: Codex plus the fixed "Webseite" tab. The tab's control lives as long as the
/// page and follows the project of the open session; it reloads in the background and marks the
/// tab with a dot instead of pulling the user out of the chat.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private const string WebdesignMode = "webdesign";
    private WebdesignPreviewView? _websiteView;
    private Button? _websiteTabButton;
    private Ellipse? _websiteDot;
    private bool _websiteViewActive;
    private long _websiteTabSelectionVersion;
    private bool _websiteBeforeCaptured;
    // The website uses the full width: opening the tab hides the right sidebar until the chat is
    // shown again. The saved sidebar preference stays untouched.
    private bool _websiteHidesInspector = true;

    /// <summary>Tabs that use the full width: the website and the Science publication.</summary>
    private bool FullWidthViewActive => _websiteViewActive || (_activeResearchSessionId is { } research && research == _session);

    private WebdesignPreviewView WebsiteView()
    {
        if (_websiteView is not null) return _websiteView;
        var view = new WebdesignPreviewView(App.Current.GetService<WebdesignServeService>()) { Visibility = Visibility.Collapsed };
        view.PageActivity += () => { if (!_websiteViewActive) SetWebsiteDot(true); };
        view.ComposeRequested += text => RunUiCallback("Webdesign.Compose", () => ComposeFromWebsite(text));
        view.AttachmentRequested += async (name, png) =>
        {
            var owner = _session;
            if (owner == Guid.Empty) return;
            await using var stream = new MemoryStream(png, writable: false);
            await _coordinator.ImportAttachmentAsync(owner, name, "image/png", stream, _lifetime.Token);
            // The picture belongs to the next prompt: show it in the composer like a pasted image.
            await RefreshComposerAfterImportAsync(owner);
        };
        view.ShowRequested += () => RunUiCallback("Webdesign.Show", OpenWebsiteView);
        view.SubmitRequested += text => RunUiCallback("Webdesign.Submit", () => _ = SubmitFromWebsiteAsync(text));
        var parent = (Panel)ResearchHost.Parent;
        parent.Children.Add(view);
        Grid.SetRow(view, Grid.GetRow(ResearchHost));
        Grid.SetColumn(view, Grid.GetColumn(ResearchHost));
        _websiteView = view;
        return view;
    }

    /// <summary>Call after the snapshot of the active session was applied.</summary>
    private void SyncWebdesignSession()
    {
        if (_mode != WebdesignMode)
        {
            if (_websiteView is { } view)
            {
                if (_websiteViewActive) ShowChatView();
                WebdesignPreviewRegistry.Unregister(view);
                _ = view.DeactivateAsync();
            }
            RenderSessionTabs();
            return;
        }
        var website = WebsiteView();
        WebdesignPreviewRegistry.Register(website);
        var workspace = S(_snapshot, "codingWorkspacePath");
        if (workspace.Length == 0) workspace = S(_snapshot, "workspacePath");
        _ = website.SetWorkspaceAsync(workspace.Length == 0 ? null : workspace);
        RenderSessionTabs();
    }

    private void RenderWebsiteTab(int index)
    {
        if (_mode != WebdesignMode || _session == Guid.Empty)
        {
            if (_websiteTabButton is not null) SessionTabsPanel.Children.Remove(_websiteTabButton);
            return;
        }
        if (_websiteTabButton is null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(CreateTabIcon(""));
            content.Children.Add(new TextBlock { Text = "Webseite", FontSize = 13 });
            _websiteDot = new Ellipse
            {
                Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                Fill = ThemeBrush("MissumAccentBrush", 160),
            };
            content.Children.Add(_websiteDot);
            _websiteTabButton = TabButton();
            _websiteTabButton.Content = content;
            _websiteTabButton.Height = 32;
            _websiteTabButton.Padding = new Thickness(10, 0, 10, 0);
            _websiteTabButton.Click += (_, _) => QueueWebsiteTabSelection();
            AutomationProperties.SetName(_websiteTabButton, "Webseite des Projekts öffnen");
            ToolTipService.SetToolTip(_websiteTabButton, "Live-Ansicht der Webseite · Geräte, Hosting, Markieren");
        }
        var active = _websiteViewActive && _activeReviewRunId is null;
        ApplyTabAppearance(_websiteTabButton, active);
        SetTabEnabled(_websiteTabButton, !_sessionTabNavigationBusy);
        SetTabHelp(_websiteTabButton, active ? "Aktive Webseite" : "Webseite der geöffneten Sitzung");
        PositionTab(_websiteTabButton, index);
    }

    private void QueueWebsiteTabSelection()
    {
        var owner = _session;
        var version = ++_websiteTabSelectionVersion;
        // Like the publication tab: WebView2 and XAML changes wait until the click has unwound.
        DispatcherQueue.TryEnqueue(() => RunUiCallback("Webdesign.TabSelection", () =>
        {
            if (version != _websiteTabSelectionVersion || owner != _session || _mode != WebdesignMode) return;
            OpenWebsiteView();
        }));
    }

    private void OpenWebsiteView()
    {
        if (_disposed || _mode != WebdesignMode || _session == Guid.Empty) return;
        SaveConversationOffset();
        ShowChatView();
        BodyGrid.Visibility = Visibility.Collapsed;
        _websiteViewActive = true;
        _websiteHidesInspector = true;
        UpdateResponsiveLayout();
        WebsiteView().Visibility = Visibility.Visible;
        SetWebsiteDot(false);
        RenderSessionTabs();
    }

    private void HideWebsiteView()
    {
        _websiteTabSelectionVersion++;
        var wasActive = _websiteViewActive;
        _websiteViewActive = false;
        if (_websiteView is { } view) view.Visibility = Visibility.Collapsed;
        if (wasActive) UpdateResponsiveLayout();
    }

    private void SetWebsiteDot(bool visible)
    {
        if (_websiteDot is { } dot) dot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Smoke hook: receives direct wishes instead of sending them to the gateway.</summary>
    private Action<string>? _websiteSubmitOverride;

    /// <summary>
    /// A wish from the tab is sent immediately, with its picture already in the composer. While a
    /// run is active the same path steers it. A draft the user was typing stays in front.
    /// </summary>
    private async Task SubmitFromWebsiteAsync(string text)
    {
        if (_disposed || _session == Guid.Empty) return;
        if (_websiteSubmitOverride is { } capture) { capture(text); return; }
        Composer.Text = Composer.Text.Trim().Length == 0 ? text : Composer.Text.TrimEnd() + "\n\n" + text;
        await SendAsync();
    }

    /// <summary>Text from the tab (hosting files): into the composer, then to the chat.</summary>
    private void ComposeFromWebsite(string text)
    {
        if (_disposed || _session == Guid.Empty) return;
        ShowChatView();
        RenderSessionTabs();
        Composer.Text = Composer.Text.Length == 0 ? text : Composer.Text.TrimEnd() + "\n" + text;
        Composer.SelectionStart = Composer.Text.Length;
        Composer.Focus(FocusState.Programmatic);
    }

    /// <summary>Before/after: the page as it was when the prompt was sent …</summary>
    private void CaptureWebsiteBeforeRun()
    {
        if (_mode != WebdesignMode || _websiteView is not { } view) return;
        _websiteBeforeCaptured = true;
        _ = view.CaptureBeforeAsync();
    }

    /// <summary>… and when the run ended.</summary>
    private void CaptureWebsiteAfterRun(string type)
    {
        if (_mode != WebdesignMode || !_websiteBeforeCaptured || _websiteView is not { } view
            || type is not ("chat.completed" or "chat.cancelled" or "chat.failed")) return;
        _websiteBeforeCaptured = false;
        _ = view.CaptureAfterAsync();
    }
}
