using Missum.App.ViewModels;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Missum.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly ILogger<SettingsPage> _logger;
    private bool _synchronizing;
    private bool _sharedSettingsSubscribed;

    public SettingsPage()
    {
        InitializeComponent();
        NativeNotice.Attach(StatusBar);
        ViewModel = App.Current.GetService<SettingsViewModel>();
        _logger = App.Current.GetService<ILogger<SettingsPage>>();
    }

    public SettingsViewModel ViewModel { get; }

    public IReadOnlyList<SettingsAccentColorOption> AccentColors { get; } =
    [
        new("Grau", "#B0B0B0", Windows.UI.Color.FromArgb(255, 176, 176, 176)),
        new("Lila", "#A970FF", Windows.UI.Color.FromArgb(255, 169, 112, 255)),
        new("Violett", "#7C5CFC", Windows.UI.Color.FromArgb(255, 124, 92, 252)),
        new("Blau", "#4C8DFF", Windows.UI.Color.FromArgb(255, 76, 141, 255)),
        new("Türkis", "#25B7A6", Windows.UI.Color.FromArgb(255, 37, 183, 166)),
        new("Grün", "#8FBD45", Windows.UI.Color.FromArgb(255, 143, 189, 69)),
        new("Orange", "#F4B860", Windows.UI.Color.FromArgb(255, 244, 184, 96)),
        new("Pink", "#D95BA8", Windows.UI.Color.FromArgb(255, 217, 91, 168)),
        new("Cyberpunk 2077", NativeIconPalette.CyberpunkAccent, NativeIconPalette.CyberpunkAccentColor),
    ];

    public IReadOnlyList<SettingsAccentColorOption> BackgroundColors { get; } =
    [
        new("Missum", "#181818", Windows.UI.Color.FromArgb(255, 24, 24, 24)),
        new("Standard", "#6B6872", Windows.UI.Color.FromArgb(255, 107, 104, 114)),
        new("Grau", "#858A94", Windows.UI.Color.FromArgb(255, 133, 138, 148)),
        new("Dunkel", "#34313B", Windows.UI.Color.FromArgb(255, 52, 49, 59)),
        new("Schwarz", "#000000", Windows.UI.Color.FromArgb(255, 0, 0, 0)),
        new("Cyberpunk 2077", "#261D45", Windows.UI.Color.FromArgb(255, 38, 29, 69)),
        new("Lila", "#A970FF", Windows.UI.Color.FromArgb(255, 169, 112, 255)),
        new("Violett", "#7C5CFC", Windows.UI.Color.FromArgb(255, 124, 92, 252)),
        new("Blau", "#4C8DFF", Windows.UI.Color.FromArgb(255, 76, 141, 255)),
        new("Türkis", "#25B7A6", Windows.UI.Color.FromArgb(255, 37, 183, 166)),
        new("Grün", "#8FBD45", Windows.UI.Color.FromArgb(255, 143, 189, 69)),
        new("Orange", "#F4B860", Windows.UI.Color.FromArgb(255, 244, 184, 96)),
        new("Pink", "#D95BA8", Windows.UI.Color.FromArgb(255, 217, 91, 168)),
    ];

    private async void OnBackToAssistant(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is { } mainWindow)
        {
            await mainWindow.OpenAssistantSessionAsync(refreshSession: false);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            await ViewModel.InitializeAsync();
            SynchronizeControls();
            if (!_sharedSettingsSubscribed)
            {
                App.Current.GetService<AssistantSettingsService>().Changed += OnSharedSettingsChanged;
                _sharedSettingsSubscribed = true;
            }
            await ViewModel.RefreshModelsAsync();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_sharedSettingsSubscribed) return;
        App.Current.GetService<AssistantSettingsService>().Changed -= OnSharedSettingsChanged;
        _sharedSettingsSubscribed = false;
    }

    private void OnSharedSettingsChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!_sharedSettingsSubscribed) return;
        if (ViewModel.HasUnsavedChanges)
        {
            ShowStatus("Einstellungen wurden auf einem anderen Gerät geändert. Dein Entwurf bleibt erhalten; beim Speichern wird ein Konflikt gemeldet.", InfoBarSeverity.Informational);
            return;
        }
        await RunActionAsync(async () =>
        {
            await ViewModel.InitializeAsync();
            SynchronizeControls();
        });
    });

    private void SynchronizeControls()
    {
        _synchronizing = true;
        try
        {
            SelectByTag(CaptionLanguageBox, ViewModel.LiveCaptionLanguage);
            SelectByTag(ThemeBox, ViewModel.Theme.ToString());
            SelectByTag(LanguageBox, ViewModel.Language);
            AccentColorList.SelectedItem = AccentColors.FirstOrDefault(color =>
                string.Equals(color.Value, ViewModel.AccentColor, StringComparison.OrdinalIgnoreCase));
            BackgroundColorList.SelectedItem = BackgroundColors.FirstOrDefault(color =>
                string.Equals(color.Value, ViewModel.BackgroundColor, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            await ViewModel.SaveAsync();
            ShowStatus("Einstellungen gespeichert.", InfoBarSeverity.Success);
        });
    }

    private async void OnSelectModelRoot(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            if (await picker.PickSingleFolderAsync() is { } folder)
                ViewModel.NativeModelRoot = folder.Path;
        });
    }

    private async void OnRefreshModels(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            var status = await ViewModel.RefreshModelsAsync();

            ShowStatus(
                ViewModel.ConnectionStatus,
                status?.IsReady == false
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success);
        });
    }

    private void OnCaptionLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && (CaptionLanguageBox.SelectedItem as ComboBoxItem)?.Tag is string value)
        {
            ViewModel.LiveCaptionLanguage = value;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing
            && (ThemeBox.SelectedItem as ComboBoxItem)?.Tag is string value
            && Enum.TryParse<AppTheme>(value, out var theme))
        {
            ViewModel.Theme = theme;
            App.Current.ApplyTheme(theme);
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && (LanguageBox.SelectedItem as ComboBoxItem)?.Tag is string value)
        {
            ViewModel.Language = value;
        }
    }

    private void OnAccentColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && AccentColorList.SelectedItem is SettingsAccentColorOption option)
        {
            ViewModel.AccentColor = option.Value;
            App.Current.ApplyAccentColor(option.Value);
        }
    }

    private void OnBackgroundColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && BackgroundColorList.SelectedItem is SettingsAccentColorOption option)
        {
            ViewModel.BackgroundColor = option.Value;
            App.Current.ApplyBackgroundColor(option.Value);
        }
    }

    private async void OnCreateBackup(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"Missum-Backup-{DateTime.Now:yyyy-MM-dd-HHmm}",
                DefaultFileExtension = ".missumbackup",
            };
            picker.FileTypeChoices.Add("Missum-Backup", new List<string> { ".missumbackup" });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var result = await ViewModel.CreateBackupAsync(file.Path);
            ShowStatus($"Backup erstellt · SHA-256 {result.Sha256[..12]}…", InfoBarSeverity.Success);
        });
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        var warning = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Backup wiederherstellen?",
            Content = "Der aktuelle Zustand wird zuerst gesichert und anschließend durch das Backup ersetzt. Missum muss danach neu gestartet werden.",
            PrimaryButtonText = "Wiederherstellen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".missumbackup");
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            ShowStatus("Wiederherstellung wartet gegebenenfalls auf den Abschluss aktiver Aufträge.", InfoBarSeverity.Informational);
            await App.Current.GetService<AssistantHost>().RestoreBackupFileAsync(file.Path);
            ShowStatus("Backup wiederhergestellt. Missum wird neu gestartet.", InfoBarSeverity.Success);
            _ = AppInstance.Restart("--restored-backup");
        });
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            StatusBar.IsOpen = false;
            await action();
        }
        catch (Exception exception)
        {
            AppLog.SettingsActionFailed(_logger, exception);
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase));
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Current.MainWindow
            ?? throw new InvalidOperationException("Das Hauptfenster ist nicht verfügbar.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
