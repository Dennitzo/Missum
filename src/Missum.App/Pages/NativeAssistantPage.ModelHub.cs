using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Missum.App.Controls;
using Missum.App.Services.ModelHub;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Missum.App.Pages;

/// <summary>
/// Model Hub: the newest unsloth GGUF models from Hugging Face. The left sidebar lists them
/// (date only) with a search; the main area shows the download folder, the chosen model's
/// quantizations with a fit indicator for this PC and its model card; the right sidebar shows
/// downloads and errors only. There is no prompt window in this mode.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private const string ModelHubMode = "modelhub";
    private const double ModelHubPanelWidth = 320;
    private bool IsModelHubMode => _mode == ModelHubMode;

    private readonly HuggingFaceHubClient _hub = new();
    private readonly List<HubModel> _hubModels = [];
    private readonly Dictionary<string, (HubModelDetails Details, string Readme)> _hubDetailsCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _hubCursor;
    private string _hubSearch = "";
    private bool _hubListLoading;
    private string? _hubListError;
    private CancellationTokenSource? _hubListCts;
    private CancellationTokenSource? _hubDetailCts;
    private DispatcherTimerLike? _hubSearchDebounce;
    private string? _hubSelectedId;
    private HubModelDetails? _hubDetails;
    private IReadOnlyList<HubQuantization> _hubQuantizations = [];
    private string? _hubDetailError;
    private bool _hubDetailLoading;
    private SystemResourceSnapshot? _hubSystem;
    private bool _hubPanelRequested = true;
    private bool _hubDownloadsHooked;
    private int _hubCompletedSeen = -1;

    private Grid? _hubHost;
    private ContentControl? _hubPanel;
    private TextBox? _hubSearchBox;
    private StackPanel? _hubBody;
    private StackPanel? _hubQuantPanel;
    private ModelHubReadmeView? _hubReadme;
    private TextBlock? _hubFolderText;
    private Border? _hubGatewayChip;
    private Button? _hubUseAsModelFolder;
    private TextBlock? _hubSystemText;
    private IReadOnlyList<LocalHubRepo> _hubLocal = [];
    private string? _hubLocalFolder;

    /// <summary>Reads which unsloth models the download folder already holds.</summary>
    internal async Task ScanHubFolderAsync()
    {
        var folder = HubFolder;
        _hubLocalFolder = folder;
        var found = await Task.Run(() => LocalHubModels.Scan(folder));
        if (folder != HubFolder) return;
        _hubLocal = found;
        if (!IsModelHubMode) return;
        RenderModelHubSidebar();
        RenderHubQuantizations();
    }

    private readonly Dictionary<string, IReadOnlyDictionary<string, HubFile>?> _hubCommitFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fetches the file lists of older commits that are in the folder (at most four, cached).</summary>
    private async Task LoadHubLocalCommitsAsync(HubModelDetails details, CancellationToken cancellationToken)
    {
        var local = _hubLocal.FirstOrDefault(r => string.Equals(r.Id, details.Id, StringComparison.OrdinalIgnoreCase));
        if (local is null) return;
        var commits = local.Files.Select(f => Path.GetFileName(f.SnapshotFolder)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !string.Equals(c, details.Commit, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
        foreach (var commit in commits)
        {
            var key = details.Id + "@" + commit;
            if (_hubCommitFiles.ContainsKey(key)) continue;
            try
            {
                var files = await _hub.GetFilesAsync(details.Id, commit, cancellationToken);
                _hubCommitFiles[key] = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or ArgumentException or TaskCanceledException)
            {
                // Unknown at Hugging Face (copied by hand) or offline: equal sizes then count as unchanged.
                _hubCommitFiles[key] = null;
            }
        }
    }

    internal LocalQuantizationStatus HubQuantizationStatus(HubModelDetails details, HubQuantization quantization, ModelDownloadJob? job = null)
    {
        if (job?.State == ModelDownloadState.Completed || ModelDownloadService.IsInstalled(HubFolder, details.Id, details.Commit, quantization))
            return new(LocalQuantizationState.Current, ModelHubCatalogId.SnapshotFolder(HubFolder, details.Id, details.Commit), new Dictionary<string, string>());
        var local = _hubLocal.FirstOrDefault(r => string.Equals(r.Id, details.Id, StringComparison.OrdinalIgnoreCase));
        var known = new Dictionary<string, IReadOnlyDictionary<string, HubFile>?>(StringComparer.OrdinalIgnoreCase);
        if (local is not null)
            foreach (var commit in local.Files.Select(f => Path.GetFileName(f.SnapshotFolder)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (_hubCommitFiles.TryGetValue(details.Id + "@" + commit, out var files)) known[commit] = files;
        return ModelHubVersions.Compare(quantization, details.Commit, local, known);
    }

    private string? HubLocalFolderOf(string repo, HubQuantization quantization) =>
        _hubLocal.FirstOrDefault(local => string.Equals(local.Id, repo, StringComparison.OrdinalIgnoreCase))?.FolderOf(quantization);

    internal IReadOnlyList<LocalHubRepo> HubLocalModels => _hubLocal;
    private ScrollViewer? _hubScroll;
    private string? _hubReadmeColors;

    private (Windows.UI.Color Background, Windows.UI.Color Text) HubReadmeColors()
    {
        var background = (AssistantFrame.Background as SolidColorBrush)?.Color ?? ThemeBrush("MissumWindowBrush", 255).Color;
        return (Color.FromArgb(255, background.R, background.G, background.B), ThemeBrush("MissumTextBrush", 240).Color);
    }

    private async Task ShowHubReadmeAsync(string repo, string commit, string readme)
    {
        if (_hubReadme is null) return;
        var (background, text) = HubReadmeColors();
        _hubReadmeColors = ColorKey(background, text);
        await _hubReadme.ShowAsync(repo, commit, readme.Length == 0 ? "*Für dieses Modell gibt es keine Beschreibung.*" : readme, background, text);
    }

    private static string ColorKey(Color background, Color text) =>
        string.Create(CultureInfo.InvariantCulture, $"{background.R},{background.G},{background.B}|{text.R},{text.G},{text.B}");

    private void RefreshHubReadmeColors()
    {
        if (!IsModelHubMode || _hubDetails is not { } shown || !_hubDetailsCache.TryGetValue(shown.Id, out var card)) return;
        var (background, text) = HubReadmeColors();
        if (_hubReadmeColors == ColorKey(background, text)) return;
        _ = ShowHubReadmeAsync(shown.Id, shown.Commit, card.Readme);
    }

    private static ModelDownloadService Downloads => App.Current.GetService<ModelDownloadService>();

    /// <summary>The download folder: the Model Hub's own setting, else the AI model folder.</summary>
    private string HubFolder =>
        _settings.Current.ModelHubDownloadDirectory is { Length: > 0 } folder ? folder
        : _settings.Current.LocalAiModelDirectory is { Length: > 0 } models ? models
        : ModelHubCatalogId.DefaultFolder;

    /// <summary>Called after every coordinator snapshot, next to the other mode syncs.</summary>
    private void SyncModelHubSession()
    {
        if (!IsModelHubMode)
        {
            if (_hubHost is { Visibility: Visibility.Visible }) HideModelHubView();
            return;
        }
        EnsureModelHubViews();
        if (BodyGrid.Visibility == Visibility.Visible || _hubHost!.Visibility != Visibility.Visible) ShowModelHubView();
        if (_hubModels.Count == 0 && !_hubListLoading && _hubListError is null) _ = LoadHubModelsAsync(reset: true);
        if (_hubSystem is null) _ = LoadHubSystemAsync();
        if (_hubLocalFolder != HubFolder) _ = ScanHubFolderAsync();
        UpdateResponsiveLayout();
        RenderSessionTabs();
    }

    private void EnsureModelHubViews()
    {
        if (_hubHost is not null) return;
        _hubHost = new Grid { Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_hubHost, ModelHubTitle);
        _hubHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _hubHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _hubHost.Children.Add(ModelHubHeader());
        _hubBody = new StackPanel { Spacing = 18, Padding = new Thickness(32, 22, 32, 40), HorizontalAlignment = HorizontalAlignment.Stretch };
        // Never wider than the column: the rows and the card wrap instead of running under the right sidebar.
        var scroll = _hubScroll = new ScrollViewer
        {
            Content = _hubBody,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetRow(scroll, 1);
        _hubHost.Children.Add(scroll);
        var parent = (Panel)MeetingsHost.Parent;
        parent.Children.Add(_hubHost);
        Grid.SetRow(_hubHost, Grid.GetRow(MeetingsHost));
        Grid.SetColumn(_hubHost, Grid.GetColumn(MeetingsHost));

        // Theme and background colour (Settings › Darstellung) restyle the model card.
        App.Current.ThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() => RunMeetingCallback("ModelHub.Theme", RefreshHubReadmeColors));
        _hubHost.ActualThemeChanged += (_, _) => RunMeetingCallback("ModelHub.Theme", RefreshHubReadmeColors);

        _hubPanel = new ContentControl { Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false };
        AutomationProperties.SetName(_hubPanel, "Downloads");
        ((Panel)MeetingChatHost.Parent).Children.Add(_hubPanel);

        if (!_hubDownloadsHooked)
        {
            _hubDownloadsHooked = true;
            Downloads.Changed += () => DispatcherQueue.TryEnqueue(() => RunMeetingCallback("ModelHub.Downloads", () =>
            {
                if (!IsModelHubMode) return;
                var completed = Downloads.Jobs.Count(j => j.State == ModelDownloadState.Completed);
                if (completed != _hubCompletedSeen) { _hubCompletedSeen = completed; _ = ScanHubFolderAsync(); }
                // Progress ticks only update the existing bars and lines; rebuilding the cards every
                // 300 ms restarted each ProgressBar's entrance animation and made the panel flicker.
                if (!UpdateModelHubPanelInPlace()) RenderModelHubPanel();
                if (!UpdateHubQuantizationsInPlace()) RenderHubQuantizations();
            }));
        }
        RenderModelHubBody();
        RenderModelHubPanel();
    }

    private void ShowModelHubView()
    {
        HideWorkingPlanView();
        HidePlanModeTab();
        HideToolView();
        HideSubagentOverview();
        _activeSourcesSession = null; _sourcesHost.Visibility = Visibility.Collapsed;
        HideResearchView();
        HideWebsiteView();
        SaveReviewScrollOffset();
        _activeReviewRunId = null;
        ReviewScroll.Visibility = Visibility.Collapsed;
        // No prompt window: the composer lives in BodyGrid.
        BodyGrid.Visibility = Visibility.Collapsed;
        MeetingsHost.Visibility = Visibility.Collapsed;
        _hubHost!.Visibility = Visibility.Visible;
        UpdateHubFolderHeader();
    }

    private void HideModelHubView()
    {
        if (_hubHost is not null) _hubHost.Visibility = Visibility.Collapsed;
        if (_hubPanel is not null) _hubPanel.Visibility = Visibility.Collapsed;
        InspectorScroll.Visibility = Visibility.Visible;
        ProjectsHeader.Visibility = Visibility.Visible;
        ShowChatView();
    }

    // ── Header: download folder, gateway status, this PC ─────────────────

    private Border ModelHubHeader()
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = "Download-Ordner", FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 160), VerticalAlignment = VerticalAlignment.Center });
        // The current path is the most visible thing in the header.
        var path = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        path.Children.Add(new FontIcon { Glyph = "", FontSize = 15, Foreground = ThemeBrush("MissumTextBrush", 230) });
        _hubFolderText = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true };
        path.Children.Add(_hubFolderText);
        var pathBorder = new Border
        {
            Child = path,
            Padding = new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(10),
            Background = ThemeBrush("MissumInputBrush", 40),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 70),
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(pathBorder, "Aktueller Download-Ordner");
        Grid.SetColumn(pathBorder, 1);
        grid.Children.Add(pathBorder);
        var change = new Button { Content = "Ändern …", CornerRadius = new CornerRadius(10) };
        change.Click += async (_, _) => await ChooseHubFolderAsync();
        Grid.SetColumn(change, 2);
        grid.Children.Add(change);
        var open = new Button { Content = "Öffnen", CornerRadius = new CornerRadius(10) };
        open.Click += (_, _) => OpenFolder(HubFolder);
        Grid.SetColumn(open, 3);
        grid.Children.Add(open);

        var status = new Grid { ColumnSpacing = 10 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hubGatewayChip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 9, 4) };
        status.Children.Add(_hubGatewayChip);
        _hubUseAsModelFolder = new Button { Content = "Als AI-Modellordner verwenden", CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4), FontSize = 12 };
        _hubUseAsModelFolder.Click += async (_, _) => await UseHubFolderForGatewayAsync();
        Grid.SetColumn(_hubUseAsModelFolder, 1);
        status.Children.Add(_hubUseAsModelFolder);
        _hubSystemText = new TextBlock { FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 160), VerticalAlignment = VerticalAlignment.Center, Text = "System wird erkannt …", TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(_hubSystemText, 2);
        status.Children.Add(_hubSystemText);
        Grid.SetRow(status, 1);
        Grid.SetColumn(status, 1);
        Grid.SetColumnSpan(status, 3);
        grid.Children.Add(status);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(32, 14, 32, 14),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private void UpdateHubFolderHeader()
    {
        if (_hubFolderText is null || _hubGatewayChip is null || _hubUseAsModelFolder is null) return;
        var folder = HubFolder;
        _hubFolderText.Text = folder;
        ToolTipService.SetToolTip(_hubFolderText, "Modelle landen in " + ModelHubCatalogId.HubRoot(folder) + "\\models--unsloth--…");
        var sees = ModelHubCatalogId.GatewaySees(folder, _settings.Current.LocalAiModelDirectory);
        var color = sees ? Color.FromArgb(255, 0x2F, 0xC4, 0x6B) : Color.FromArgb(255, 0xE0, 0xA1, 0x2B);
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chip.Children.Add(new FontIcon { Glyph = sees ? "" : "", FontSize = 11, Foreground = new SolidColorBrush(color) });
        chip.Children.Add(new TextBlock { Text = sees ? "Vom Gateway erkannt" : "Nicht im AI-Modellordner – Missum findet Downloads hier nicht", FontSize = 12, Foreground = new SolidColorBrush(color) });
        _hubGatewayChip.Child = chip;
        _hubGatewayChip.Background = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
        _hubUseAsModelFolder.Visibility = sees ? Visibility.Collapsed : Visibility.Visible;
        if (_hubSystemText is not null && _hubSystem is not null)
        {
            _hubSystemText.Text = "Dieser PC: " + _hubSystem.Summary;
            ToolTipService.SetToolTip(_hubSystemText, string.Join('\n', _hubSystem.Gpus.Select(g => $"GPU {g.Index}: {g.Name} · {ModelHubFormat.Gigabytes(g.TotalBytes)}")) + "\nRAM: " + ModelHubFormat.Gigabytes(_hubSystem.RamTotalBytes));
        }
    }

    private async Task ChooseHubFolderAsync()
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await SetHubFolderAsync(folder.Path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
    }

    internal async Task SetHubFolderAsync(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Der Ordner existiert nicht.");
        var full = Path.GetFullPath(folder);
        await _settings.UpdateAsync(current => current with { ModelHubDownloadDirectory = full });
        UpdateHubFolderHeader();
        await ScanHubFolderAsync();
    }

    /// <summary>Makes the download folder the gateway's model folder; the runtime switches once it is idle.</summary>
    private async Task UseHubFolderForGatewayAsync()
    {
        var folder = HubFolder;
        try
        {
            await _settings.UpdateAsync(current => current with { LocalAiModelDirectory = folder });
            UpdateHubFolderHeader();
            ShowModelHubNotice("AI-Modellordner geändert. Der lokale Modellserver verwendet ihn, sobald er im Leerlauf ist.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _ = exception; }
    }

    private async Task LoadHubSystemAsync()
    {
        try
        {
            _hubSystem = await SystemResources.ReadAsync(_lifetime.Token);
            UpdateHubFolderHeader();
            RenderHubQuantizations();
        }
        catch (OperationCanceledException) { }
    }

    // ── Left sidebar: search and newest models ───────────────────────────

    private void RenderModelHubSidebar()
    {
        ProjectsHeader.Visibility = Visibility.Collapsed;
        ProjectsPanel.Children.Clear();
        if (_hubSearchBox is null)
        {
            _hubSearchBox = new TextBox { PlaceholderText = "Unsloth-Modelle suchen", CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 8, 0, 4) };
            AutomationProperties.SetName(_hubSearchBox, "Unsloth-Modelle suchen");
            _hubSearchBox.TextChanged += (_, _) =>
            {
                _hubSearchDebounce ??= new DispatcherTimerLike(DispatcherQueue, TimeSpan.FromMilliseconds(350), () =>
                {
                    var text = _hubSearchBox.Text.Trim();
                    if (text == _hubSearch) return;
                    _hubSearch = text;
                    _ = LoadHubModelsAsync(reset: true);
                });
                _hubSearchDebounce.Restart();
            };
        }
        ProjectsPanel.Children.Add(_hubSearchBox);

        var header = new Grid { Margin = new Thickness(0, 6, 0, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        header.Children.Add(new TextBlock
        {
            Text = _hubSearch.Length > 0 ? "Suchergebnisse" : "Neueste Unsloth-Modelle",
            FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
        });
        var refresh = new Button { Style = (Style)Resources["RoundIconButtonStyle"], Content = new FontIcon { Glyph = "", FontSize = 12 } };
        ToolTipService.SetToolTip(refresh, "Aktualisieren");
        AutomationProperties.SetName(refresh, "Modellliste aktualisieren");
        refresh.Click += (_, _) => _ = LoadHubModelsAsync(reset: true);
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        ProjectsPanel.Children.Add(header);

        if (_hubListError is not null && _hubModels.Count == 0)
            ProjectsPanel.Children.Add(new TextBlock { Text = _hubListError, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(10, 4, 4, 4), Foreground = ThemeBrush("MissumDangerBrush", 200) });
        else if (_hubModels.Count == 0)
            ProjectsPanel.Children.Add(new TextBlock { Text = _hubListLoading ? "Lade Modelle …" : "Keine Modelle gefunden.", FontSize = 12, Margin = new Thickness(10, 4, 4, 4), Foreground = ThemeBrush("MissumMutedTextBrush", 150) });

        var now = DateTimeOffset.UtcNow;
        var jobs = Downloads.Jobs;
        // Models already in the download folder come first (not while searching).
        if (_hubSearch.Length == 0 && _hubLocal.Count > 0)
        {
            var localHeader = new TextBlock
            {
                Text = "Im Download-Ordner", FontSize = 13, Margin = new Thickness(10, 6, 0, 2),
                Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            };
            ProjectsPanel.Children.Insert(ProjectsPanel.Children.IndexOf(header), localHeader);
            var at = ProjectsPanel.Children.IndexOf(header);
            foreach (var local in _hubLocal)
                ProjectsPanel.Children.Insert(at++, HubModelRow(new HubModel(local.Id, local.LastWrite, null, false), now, jobs, localDate: true));
        }
        foreach (var model in _hubModels) ProjectsPanel.Children.Add(HubModelRow(model, now, jobs));
        if (_hubCursor is not null)
        {
            var more = new Button { Content = _hubListLoading ? "Lade …" : "Weitere laden", HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 4, 0, 8), IsEnabled = !_hubListLoading };
            more.Click += (_, _) => _ = LoadHubModelsAsync(reset: false);
            ProjectsPanel.Children.Add(more);
        }
    }

    private Button HubModelRow(HubModel model, DateTimeOffset now, IReadOnlyList<ModelDownloadJob> jobs, bool localDate = false)
    {
        var selected = string.Equals(model.Id, _hubSelectedId, StringComparison.OrdinalIgnoreCase);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(HubModelIcon(model.Name));
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = model.Name, FontSize = 13, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        var date = new TextBlock { Text = (localDate ? "geladen " : "") + ModelHubFormat.Ago(model.LastModified, now), FontSize = 11, Foreground = ThemeBrush("MissumMutedTextBrush", 150) };
        ToolTipService.SetToolTip(date, (localDate ? "Im Ordner seit " : "Aktualisiert am ") + model.LastModified.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("de-DE")));
        text.Children.Add(date);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        // Status only, no statistics: a dot while downloading or once something is installed.
        var active = jobs.Any(j => j.Repo == model.Id && j.IsActive);
        var done = jobs.Any(j => j.Repo == model.Id && j.State == ModelDownloadState.Completed)
            || _hubLocal.Any(local => string.Equals(local.Id, model.Id, StringComparison.OrdinalIgnoreCase));
        if (active || done)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(active ? Color.FromArgb(255, 0x4C, 0x9A, 0xFF) : Color.FromArgb(255, 0x2F, 0xC4, 0x6B)),
            };
            ToolTipService.SetToolTip(dot, active ? "Wird heruntergeladen" : "Im Download-Ordner vorhanden");
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }
        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = selected ? ThemeBrush("MissumLayerStrongBrush", 50) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        AutomationProperties.SetName(button, model.Name);
        button.Click += (_, _) => _ = SelectHubModelAsync(model.Id);
        return button;
    }

    private static Border HubModelIcon(string name)
    {
        var initials = string.Concat(name.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries).Where(p => char.IsLetter(p[0])).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        // A stable colour per model (string.GetHashCode changes with every start).
        var hue = name.ToUpperInvariant().Aggregate(17u, (hash, c) => unchecked(hash * 31 + c)) % 360;
        var color = FromHue(hue);
        return new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
            Child = new TextBlock { Text = initials.Length == 0 ? "U" : initials, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(color) },
        };
    }

    private static Color FromHue(uint hue)
    {
        double h = hue / 60.0, c = .62, x = c * (1 - Math.Abs(h % 2 - 1)), m = .32;
        var (r, g, b) = h switch { < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x), < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x) };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    internal async Task LoadHubModelsAsync(bool reset)
    {
        _hubListCts?.Cancel();
        var cts = _hubListCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _hubListLoading = true;
        _hubListError = null;
        if (reset) { _hubModels.Clear(); _hubCursor = null; }
        if (IsModelHubMode) RenderModelHubSidebar();
        try
        {
            var page = await _hub.ListAsync(_hubSearch, reset ? null : _hubCursor, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var model in page.Models)
                if (!_hubModels.Any(m => m.Id == model.Id)) _hubModels.Add(model);
            _hubCursor = page.NextCursor;
            if (_hubSelectedId is null && _hubModels.Count > 0 && _hubSearch.Length == 0) _ = SelectHubModelAsync(_hubModels[0].Id);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _hubListError = "Hugging Face ist nicht erreichbar: " + exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_hubListCts, cts)) _hubListLoading = false;
        }
        if (IsModelHubMode) RenderModelHubSidebar();
    }

    // ── Main window: title, quantizations, model card ────────────────────

    internal async Task SelectHubModelAsync(string repo)
    {
        _hubDetailCts?.Cancel();
        var cts = _hubDetailCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _hubSelectedId = repo;
        _hubDetailError = null;
        _hubDetailLoading = true;
        _hubDetails = null;
        _hubQuantizations = [];
        RenderModelHubSidebar();
        RenderModelHubBody();
        try
        {
            if (!_hubDetailsCache.TryGetValue(repo, out var cached))
            {
                var details = await _hub.GetDetailsAsync(repo, cts.Token);
                var readme = await _hub.GetReadmeAsync(repo, details.Commit, cts.Token);
                cached = (details, readme);
                _hubDetailsCache[repo] = cached;
            }
            if (cts.IsCancellationRequested) return;
            _hubDetails = cached.Details;
            _hubQuantizations = ModelHubQuantizations.Group(cached.Details.Files);
            // Older local snapshots: their file list on Hugging Face tells whether a quantization changed since.
            await LoadHubLocalCommitsAsync(cached.Details, cts.Token);
            if (cts.IsCancellationRequested) return;
            _hubDetailLoading = false;
            RenderModelHubBody();
            if (_hubReadme is not null)
                await ShowHubReadmeAsync(repo, cached.Details.Commit, cached.Readme);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or TaskCanceledException)
        {
            _hubDetailLoading = false;
            _hubDetailError = exception.Message;
            RenderModelHubBody();
        }
    }

    private void RenderModelHubBody()
    {
        if (_hubBody is null) return;
        _hubBody.Children.Clear();
        if (_hubSelectedId is null)
        {
            _hubBody.Children.Add(new TextBlock { Text = "Wähle links ein Modell.", Foreground = ThemeBrush("MissumMutedTextBrush", 160), Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }
        var name = new HubModel(_hubSelectedId, default, null, false).Name;
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(new TextBlock { Text = name + "-GGUF", FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var meta = _hubDetails is { } details
            ? "unsloth · aktualisiert " + ModelHubFormat.Ago(details.LastModified, DateTimeOffset.UtcNow) + (details.BaseModel is { } based ? " · Basis " + based : "")
            : "unsloth";
        title.Children.Add(new TextBlock { Text = meta, FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 160), TextWrapping = TextWrapping.Wrap });
        _hubBody.Children.Add(title);

        if (_hubDetailError is not null)
        {
            _hubBody.Children.Add(new TextBlock { Text = _hubDetailError, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MissumDangerBrush", 200) });
            return;
        }
        if (_hubDetailLoading)
        {
            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            loading.Children.Add(new TextBlock { Text = "Lade Modellinformationen …", Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
            _hubBody.Children.Add(loading);
            return;
        }

        _hubBody.Children.Add(new TextBlock { Text = "GGUF-Quantisierungen", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        _hubQuantPanel = new StackPanel { Spacing = 6 };
        _hubBody.Children.Add(_hubQuantPanel);
        RenderHubQuantizations();

        _hubBody.Children.Add(new TextBlock { Text = "Beschreibung", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        if (_hubReadme is null)
        {
            _hubReadme = new ModelHubReadmeView();
            _hubReadme.WheelScrolled += delta => RunMeetingCallback("ModelHub.Wheel", () =>
            {
                if (_hubScroll is { } scroll) scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + delta, 0, scroll.ScrollableHeight), null, disableAnimation: true);
            });
        }
        if (_hubReadme.Parent is Panel old) old.Children.Remove(_hubReadme);
        _hubBody.Children.Add(_hubReadme);
    }

    private void RenderHubQuantizations()
    {
        if (_hubQuantPanel is null || _hubDetails is null) return;
        _hubQuantPanel.Children.Clear();
        _hubQuantStateViews.Clear();
        _hubQuantSignature = HubQuantSignature();
        if (_hubQuantizations.Count == 0)
        {
            _hubQuantPanel.Children.Add(new TextBlock { Text = "Dieses Repository enthält keine ladbaren GGUF-Dateien.", Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
            return;
        }
        foreach (var quantization in _hubQuantizations) _hubQuantPanel.Children.Add(HubQuantizationRow(_hubDetails, quantization));
    }

    private Border HubQuantizationRow(HubModelDetails details, HubQuantization quantization)
    {
        var job = Downloads.Find(details.Id, details.Commit, quantization.Label);
        var status = HubQuantizationStatus(details, quantization, job);
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = quantization.Label, FontSize = 14, FontWeight = FontWeights.SemiBold });
        var files = quantization.Parts.Count > 1 ? $"{quantization.Parts.Count} Teile" : "1 Datei";
        if (quantization.Companions.Any(c => ModelHubQuantizations.IsProjector(c.Path))) files += " + Vision";
        if (quantization.Companions.FirstOrDefault(c => ModelHubQuantizations.IsDraft(c.Path)) is { } draft)
            files += " + MTP " + ModelHubQuantizations.Label(Path.GetFileNameWithoutExtension(draft.Path[(draft.Path.LastIndexOf('/') + 1)..]), "");
        label.Children.Add(new TextBlock { Text = files, FontSize = 11, Foreground = ThemeBrush("MissumMutedTextBrush", 150) });
        grid.Children.Add(label);
        var size = new TextBlock { Text = ModelHubFormat.Gigabytes(quantization.TotalBytes), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(size, 1);
        grid.Children.Add(size);

        var fit = _hubSystem is null ? null : ModelFitEstimator.Estimate(quantization, details.Parameters, _hubSystem);
        var badge = HubFitBadge(fit);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var action = HubQuantizationAction(details, quantization, job, status, fit);
        Grid.SetColumn(action, 3);
        grid.Children.Add(action);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(14, 10, 12, 10),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("MissumLayerBrush", 32),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(1),
        };
    }

    private static readonly Color FitGreen = Color.FromArgb(255, 0x2F, 0xC4, 0x6B);
    private static readonly Color FitYellow = Color.FromArgb(255, 0xE0, 0xA1, 0x2B);
    private static readonly Color FitRed = Color.FromArgb(255, 0xE5, 0x53, 0x4B);

    private static Border HubFitBadge(ModelFit? fit)
    {
        var color = fit?.Level switch
        {
            ModelFitLevel.SingleGpu or ModelFitLevel.MultiGpu => FitGreen,
            ModelFitLevel.Offload => FitYellow,
            ModelFitLevel.Insufficient => FitRed,
            _ => Color.FromArgb(255, 0x88, 0x88, 0x8E),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = fit?.Label ?? "Prüfe System …", FontSize = 12, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        var border = new Border
        {
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(9, 4, 10, 5),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)),
        };
        if (fit is not null)
        {
            ToolTipService.SetToolTip(border, fit.Detail + "\nGeschätzt für " + ModelFitEstimator.DefaultContext.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " Token Kontext.");
            AutomationProperties.SetName(border, fit.Label + ". " + fit.Detail);
        }
        return border;
    }

    private FrameworkElement HubQuantizationAction(HubModelDetails details, HubQuantization quantization, ModelDownloadJob? job, LocalQuantizationStatus status, ModelFit? fit)
    {
        if (job is { IsActive: true } or { State: ModelDownloadState.Paused })
        {
            var stateText = new TextBlock { Text = HubQuantizationStateText(job), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush("MissumMutedTextBrush", 180) };
            _hubQuantStateViews[quantization.Label] = stateText;
            return stateText;
        }
        // One button, three states: present (greyed, no action), update, download.
        var (glyph, text, help) = status.State switch
        {
            LocalQuantizationState.Current => ("", "Vorhanden", "Diese Quantisierung liegt in der aktuellen Version im Download-Ordner."),
            LocalQuantizationState.Outdated => ("", "Aktualisieren",
                string.Equals(Path.GetFileName(status.Folder), details.Commit, StringComparison.OrdinalIgnoreCase)
                    ? "Die Kopie im Download-Ordner ist unvollständig; fehlende Dateien werden geladen."
                    : status.Reusable.Count > 0
                        ? "Auf Hugging Face gibt es eine neuere Version. Unveränderte Dateien werden übernommen, nur geänderte geladen."
                        : "Auf Hugging Face gibt es eine neuere Version dieser Quantisierung."),
            _ => ("", "Herunterladen", "In den Download-Ordner laden"),
        };
        var download = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = glyph, FontSize = 13 }, new TextBlock { Text = text } } },
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 5, 12, 6),
            IsEnabled = status.State != LocalQuantizationState.Current,
        };
        AutomationProperties.SetName(download, quantization.Label + ": " + text);
        ToolTipService.SetToolTip(download, help);
        if (status.State != LocalQuantizationState.Current)
            download.Click += async (_, _) => await StartHubDownloadAsync(details, quantization, fit, status.Reusable);
        return download;
    }

    internal async Task StartHubDownloadAsync(HubModelDetails details, HubQuantization quantization, ModelFit? fit, IReadOnlyDictionary<string, string>? reuse = null)
    {
        if (fit?.Level == ModelFitLevel.Insufficient)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Reicht der Speicher nicht?",
                Content = new TextBlock { Text = fit.Detail + "\n\nTrotzdem herunterladen?", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Trotzdem laden",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        // Enough room on the drive? New data, plus what queued downloads there still need, plus a reserve.
        var needed = ModelDownloadService.BytesToDownload(HubFolder, details.Id, details.Commit, quantization, reuse);
        var pending = Downloads.PendingBytesOnDrive(HubFolder);
        if (HubFreeSpace(HubFolder) is { } free && free < needed + pending + ModelDownloadService.FreeSpaceMargin)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Nicht genug Speicherplatz",
                Content = new TextBlock
                {
                    Text = $"{quantization.Label} braucht noch {ModelHubFormat.Gigabytes(needed)}"
                        + (pending > 0 ? $", laufende Downloads auf diesem Laufwerk weitere {ModelHubFormat.Gigabytes(pending)}" : "")
                        + $". Frei sind {ModelHubFormat.Gigabytes(free)} auf {Path.GetPathRoot(Path.GetFullPath(HubFolder))} (5 GB bleiben als Reserve frei)."
                        + "\n\nGib Speicher frei oder wähle oben einen anderen Download-Ordner.",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
            return;
        }
        try
        {
            Directory.CreateDirectory(HubFolder);
            Downloads.Enqueue(details, quantization, HubFolder, reuse);
            _hubPanelRequested = true;
            UpdateResponsiveLayout();
            RenderModelHubPanel();
            RenderHubQuantizations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { ShowError(exception.Message); }
    }

    /// <summary>Free bytes on the drive of the download folder, or null when it cannot be read.</summary>
    internal static long? HubFreeSpace(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ── Right sidebar: downloads and errors only ─────────────────────────

    // Live elements of the rendered download cards, keyed by job id, and the structure they show.
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Line)> _hubJobViews = new(StringComparer.Ordinal);
    private string? _hubPanelSignature;
    private readonly Dictionary<string, TextBlock> _hubQuantStateViews = new(StringComparer.OrdinalIgnoreCase);
    private string? _hubQuantSignature;

    /// <summary>What the panel's layout depends on: which jobs are shown, in which state, and the notice.</summary>
    private string HubPanelSignature() =>
        string.Join("|", Downloads.Jobs.Select(j => j.Id + ":" + (int)j.State + ":" + j.GatewayReady)) + "|" + _hubNotice;

    private bool UpdateModelHubPanelInPlace()
    {
        if (_hubPanel is null || _hubPanelSignature is null || _hubPanelSignature != HubPanelSignature()) return false;
        foreach (var job in Downloads.Jobs)
        {
            if (!_hubJobViews.TryGetValue(job.Id, out var view)) continue;
            if (!view.Bar.IsIndeterminate) view.Bar.Value = job.Progress;
            var line = HubJobLine(job);
            if (view.Line.Text != line) view.Line.Text = line;
        }
        return true;
    }

    private string HubQuantSignature() => _hubDetails is null ? "" :
        _hubDetails.Id + "@" + _hubDetails.Commit + "|" + string.Join("|", _hubQuantizations.Select(q =>
            q.Label + ":" + (Downloads.Find(_hubDetails.Id, _hubDetails.Commit, q.Label)?.State is { } state ? (int)state : -1)));

    private bool UpdateHubQuantizationsInPlace()
    {
        if (_hubDetails is null || _hubQuantSignature is null || _hubQuantSignature != HubQuantSignature()) return false;
        foreach (var (label, text) in _hubQuantStateViews)
            if (Downloads.Find(_hubDetails.Id, _hubDetails.Commit, label) is { } job && HubQuantizationStateText(job) is var value && text.Text != value)
                text.Text = value;
        return true;
    }

    private static string HubQuantizationStateText(ModelDownloadJob job) => job.State switch
    {
        ModelDownloadState.Queued => "Wartet",
        ModelDownloadState.Verifying => "Prüft …",
        ModelDownloadState.Paused => $"Pausiert · {job.Progress:P0}",
        _ => $"Lädt · {job.Progress:P0}",
    };

    private void RenderModelHubPanel()
    {
        if (_hubPanel is null) return;
        _hubJobViews.Clear();
        _hubPanelSignature = HubPanelSignature();
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = "Downloads", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) });
        var jobs = Downloads.Jobs;
        var visible = jobs.Where(j => j.State is not (ModelDownloadState.Failed or ModelDownloadState.Canceled))
            .OrderBy(j => j.State switch { ModelDownloadState.Running or ModelDownloadState.Verifying => 0, ModelDownloadState.Queued => 1, ModelDownloadState.Paused => 2, _ => 3 })
            .ThenByDescending(j => j.CreatedAt)
            .Take(12)
            .ToList();
        if (visible.Count == 0)
            stack.Children.Add(new TextBlock { Text = "Noch keine Downloads. Wähle links ein Modell und eine Quantisierung.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 150) });
        foreach (var job in visible) stack.Children.Add(HubJobCard(job));

        var failed = jobs.Where(j => j.State == ModelDownloadState.Failed).OrderByDescending(j => j.FailedAt).ToList();
        if (_hubNotice is not null)
            stack.Children.Add(new TextBlock { Text = _hubNotice, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 170) });
        if (failed.Count > 0)
        {
            stack.Children.Add(new TextBlock { Text = "Fehler", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0), Foreground = new SolidColorBrush(FitRed) });
            foreach (var job in failed) stack.Children.Add(HubErrorCard(job));
        }
        _hubPanel.Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
    }

    private string? _hubNotice;

    private void ShowModelHubNotice(string text)
    {
        _hubNotice = text;
        RenderModelHubPanel();
    }

    private Border HubJobCard(ModelDownloadJob job)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = job.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = job.Label, FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Value = job.Progress, IsIndeterminate = job.State == ModelDownloadState.Verifying };
        if (job.State != ModelDownloadState.Completed) stack.Children.Add(progress);
        // One fixed line: wrapping text of changing width made the card's height jump on every tick.
        var line = new TextBlock { Text = HubJobLine(job), FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrush("MissumMutedTextBrush", 175) };        stack.Children.Add(line);
        _hubJobViews[job.Id] = (progress, line);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        switch (job.State)
        {
            case ModelDownloadState.Running or ModelDownloadState.Queued or ModelDownloadState.Verifying:
                buttons.Children.Add(HubSmallButton("", "Pausieren", () => Downloads.Pause(job.Id)));
                buttons.Children.Add(HubSmallButton("", "Abbrechen", () => _ = ConfirmCancelAsync(job)));
                break;
            case ModelDownloadState.Paused:
                buttons.Children.Add(HubSmallButton("", "Fortsetzen", () => Downloads.Resume(job.Id)));
                buttons.Children.Add(HubSmallButton("", "Abbrechen", () => _ = ConfirmCancelAsync(job)));
                break;
            case ModelDownloadState.Completed:
                buttons.Children.Add(HubSmallButton("", "Ordner öffnen", () => OpenFolder(Path.GetDirectoryName(ModelHubCatalogId.FilePath(job.Folder, job.Repo, job.Commit, job.Files[^1].Path))!)));
                buttons.Children.Add(HubSmallButton("", "Entfernen", () => Downloads.Remove(job.Id)));
                break;
        }
        stack.Children.Add(buttons);
        return new Border
        {
            Child = stack,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("MissumLayerStrongBrush", 40),
        };
    }

    /// <summary>"42 % · 31,2 / 74 GB · 85 MB/s · noch 9 Min." and the end states.</summary>
    internal static string HubJobLine(ModelDownloadJob job)
    {
        var amount = $"{job.Progress:P0} · {ModelHubFormat.Gigabytes(job.DoneBytes)} / {ModelHubFormat.Gigabytes(job.TotalBytes)}";
        return job.State switch
        {
            ModelDownloadState.Running when job.BytesPerSecond > 0 =>
                amount + $" · {job.BytesPerSecond / 1e6:0} MB/s · noch {Remaining(job)}",
            ModelDownloadState.Running => amount,
            ModelDownloadState.Queued => amount + " · wartet",
            ModelDownloadState.Verifying => amount + " · Prüfsumme wird geprüft",
            ModelDownloadState.Paused => amount + " · pausiert",
            ModelDownloadState.Completed => ModelHubFormat.Gigabytes(job.TotalBytes) + " · " + (job.GatewayReady switch
            {
                true => "Bereit in Missum",
                false => "Fertig – Ordner ist nicht der AI-Modellordner",
                null => "Fertig – Missum erkennt das Modell gleich",
            }),
            _ => amount,
        };

        static string Remaining(ModelDownloadJob job)
        {
            var seconds = (job.TotalBytes - job.DoneBytes) / Math.Max(1, job.BytesPerSecond);
            return seconds < 90 ? $"{Math.Ceiling(seconds)} s" : seconds < 5400 ? $"{Math.Ceiling(seconds / 60)} Min." : $"{seconds / 3600:0.#} Std.";
        }
    }

    private static Border HubErrorCard(ModelDownloadJob job)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = job.Name + " · " + job.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = job.Error ?? "Unbekannter Fehler", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (job.FailedAt is { } at)
            stack.Children.Add(new TextBlock { Text = at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " Uhr", FontSize = 11, Foreground = ThemeBrush("MissumMutedTextBrush", 150) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(HubSmallButton("", "Erneut versuchen", () => Downloads.Resume(job.Id)));
        buttons.Children.Add(HubSmallButton("", "Entfernen", () => Downloads.Remove(job.Id)));
        stack.Children.Add(buttons);
        return new Border
        {
            Child = stack,
            Padding = new Thickness(12, 9, 12, 10),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(30, FitRed.R, FitRed.G, FitRed.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, FitRed.R, FitRed.G, FitRed.B)),
            BorderThickness = new Thickness(1),
        };
    }

    private static Button HubSmallButton(string glyph, string text, Action onClick)
    {
        var button = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new FontIcon { Glyph = glyph, FontSize = 11 }, new TextBlock { Text = text, FontSize = 12 } } },
            Padding = new Thickness(9, 4, 10, 5),
            CornerRadius = new CornerRadius(9),
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    private async Task ConfirmCancelAsync(ModelDownloadJob job)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Download abbrechen?",
            Content = new TextBlock { Text = $"{job.Name} {job.Label}: Die bisher geladenen {ModelHubFormat.Gigabytes(job.DoneBytes)} werden gelöscht. Mit „Pausieren“ bleiben sie erhalten.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Abbrechen und löschen",
            CloseButtonText = "Weiterladen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Downloads.Cancel(job.Id);
    }

    /// <summary>A restartable one-shot timer on the UI thread (search debounce).</summary>
    private sealed class DispatcherTimerLike
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherTimerLike(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan delay, Action tick)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = delay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => tick();
        }

        public void Restart() { _timer.Stop(); _timer.Start(); }
    }
}
