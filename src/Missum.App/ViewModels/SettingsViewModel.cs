using CommunityToolkit.Mvvm.ComponentModel;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Ai.Contracts;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Missum.App.ViewModels;

public sealed partial class SettingsViewModel(
    SettingsCoordinator settings,
    MissumAiConnectionService missumAi,
    IBackupService backups,
    ShellViewModel shell,
    ModelCapabilityRegistry modelCapabilities,
    AssistantSettingsService? sharedSettingsService = null,
    NativeModelRuntimeService? nativeRuntime = null) : ObservableObject
{
    private readonly AssistantSettingsService _sharedSettings = sharedSettingsService
        ?? new(settings, missumAi, backups);
    private long _preferencesRevision;
    private AppSettings _preferencesBaseline = new();

    /// <summary>External settings refreshes may replace the editor only when it has no local draft.</summary>
    public bool HasUnsavedChanges =>
        MissumAiServerUrl != _preferencesBaseline.MissumAiServerUrl
        || NativeModelRoot != ConfiguredModelRoot(_preferencesBaseline)
        || IsAutomaticSpeechEnabled != _preferencesBaseline.IsAutomaticSpeechEnabled
        || LiveCaptionLanguage != _preferencesBaseline.LiveCaptionLanguage
        || Theme != _preferencesBaseline.Theme
        || AccentColor != _preferencesBaseline.AccentColor
        || BackgroundColor != _preferencesBaseline.BackgroundColor
        || IsLiquidGlassEnabled != _preferencesBaseline.IsLiquidGlassEnabled
        || IsCrossSessionMemoryEnabled != _preferencesBaseline.IsCrossSessionMemoryEnabled
        || Language != _preferencesBaseline.Language;

    public ObservableCollection<LocalAiModel> Models { get; } = [];
    public ObservableCollection<LocalAiModel> CodingModels { get; } = [];

    [ObservableProperty]
    public partial string MissumAiServerUrl { get; set; } = MissumAiConnectionService.DefaultServerUrl;

    [ObservableProperty]
    public partial string NativeModelRoot { get; set; } = "";

    private static string ConfiguredModelRoot(AppSettings preferences) => preferences.LocalAiModelDirectory
        ?? NativeModelRuntimeService.ResolveModelRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [ObservableProperty]
    public partial bool IsAutomaticSpeechEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string LiveCaptionLanguage { get; set; } = "auto";

    [ObservableProperty]
    public partial string? SelectedModel { get; set; }

    [ObservableProperty]
    public partial LocalAiModel? SelectedGeneralModelItem { get; set; }

    [ObservableProperty]
    public partial string? SelectedCodingModel { get; set; }

    [ObservableProperty]
    public partial LocalAiModel? SelectedCodingModelItem { get; set; }

    [ObservableProperty]
    public partial string CodingModelStatus { get; set; } = "AI-Modelle werden beim Aktualisieren erkannt.";

    [ObservableProperty]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    [ObservableProperty]
    public partial string AccentColor { get; set; } = AppSettings.DefaultAccentColor;

    [ObservableProperty]
    public partial string BackgroundColor { get; set; } = AppSettings.DefaultBackgroundColor;

    [ObservableProperty]
    public partial bool IsLiquidGlassEnabled { get; set; }

    /// <summary>ChatGPT mode may search earlier chats across sessions and projects.</summary>
    [ObservableProperty]
    public partial bool IsCrossSessionMemoryEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string Language { get; set; } = "de-DE";

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "Nicht geprüft";

    [ObservableProperty]
    public partial bool IsServerReady { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void Initialize()
    {
        var current = settings.Current;
        _preferencesRevision = current.PreferencesRevision;
        _preferencesBaseline = current;
        IsAutomaticSpeechEnabled = current.IsAutomaticSpeechEnabled;
        MissumAiServerUrl = current.MissumAiServerUrl;
        NativeModelRoot = ConfiguredModelRoot(current);
        LiveCaptionLanguage = current.LiveCaptionLanguage;
        SelectedModel = current.SelectedModel ?? AppSettings.DefaultSelectedModel;
        SelectedGeneralModelItem = EnsureModelItem(Models, SelectedModel);
        SelectedCodingModel = current.SelectedCodingModel;
        SelectedCodingModelItem = EnsureModelItem(CodingModels, SelectedCodingModel);
        Theme = current.Theme;
        AccentColor = current.AccentColor;
        BackgroundColor = current.BackgroundColor;
        IsLiquidGlassEnabled = current.IsLiquidGlassEnabled;
        IsCrossSessionMemoryEnabled = current.IsCrossSessionMemoryEnabled;
        Language = current.Language;
        IsServerReady = shell.IsAiServerReady;
        ConnectionStatus = "Nicht geprüft";
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Initialize();
        return Task.CompletedTask;
    }

    public async Task<MissumAiConnectionStatus?> SaveAsync(CancellationToken cancellationToken = default)
    {
        await SavePreferencesAsync(cancellationToken);
        App.Current.ApplyTheme(Theme);
        App.Current.ApplyAccentColor(AccentColor);
        App.Current.ApplyBackgroundColor(BackgroundColor);
        var gateway = new Uri(MissumAiServerUrl);
        var localModelRoot = nativeRuntime is not null && settings.Current.LocalAiModelDirectory is not null
            && NativeModelRuntimeService.IsLocalGateway(gateway);
        var status = localModelRoot ? await missumAi.TestAsync(cancellationToken)
            : await _sharedSettings.TestConnectionAsync(MissumAiServerUrl, cancellationToken);
        ConnectionStatus = status.Message;
        IsServerReady = status.IsReady;
        shell.ApplyAiConnectionState(status.IsReachable, status.IsReady);
        if (localModelRoot)
        {
            if (missumAi.NativeRuntimeError is { } error)
                throw new InvalidOperationException($"Modellordner gespeichert. Initialisierung fehlgeschlagen: {error}");
            await nativeRuntime!.ConfirmConfiguredModelRootAsync(NativeModelRoot, cancellationToken);
            if (status.IsReachable) await RefreshModelsAsync(cancellationToken);
        }
        return status;
    }

    // Keep preference persistence independent of window updates and connection
    // probes so it can also be validated without launching the WinUI app.
    internal async Task SavePreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (NativeModelRoot != ConfiguredModelRoot(_preferencesBaseline))
        {
            NativeModelRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NativeModelRoot));
            await NativeModelRuntimeService.ValidateModelRootAsync(NativeModelRoot, cancellationToken);
        }
        await _sharedSettings.UpdatePreferencesAsync(CreatePreferencesPatch(), _preferencesRevision, cancellationToken);
        _preferencesRevision = settings.Current.PreferencesRevision;
        _preferencesBaseline = settings.Current;
        MissumAiServerUrl = settings.Current.MissumAiServerUrl;
        NativeModelRoot = ConfiguredModelRoot(settings.Current);
        SelectedModel = settings.Current.SelectedModel;
    }

    private AssistantSettingsPatch CreatePreferencesPatch() => new()
        {
            IsAutomaticSpeechEnabled = IsAutomaticSpeechEnabled,
            MissumAiServerUrl = MissumAiServerUrl,
            NativeModelRoot = NativeModelRoot == ConfiguredModelRoot(_preferencesBaseline) ? null : NativeModelRoot,
            LiveCaptionLanguage = string.IsNullOrWhiteSpace(LiveCaptionLanguage) ? "auto" : LiveCaptionLanguage.Trim(),
            Theme = Theme,
            AccentColor = AccentColor,
            BackgroundColor = BackgroundColor,
            IsLiquidGlassEnabled = IsLiquidGlassEnabled,
            IsCrossSessionMemoryEnabled = IsCrossSessionMemoryEnabled,
            Language = Language,
        };

    public async Task<MissumAiConnectionStatus?> RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        MissumAiConnectionStatus? status = null;
        IsBusy = true;
        try
        {
            // A ComboBox cannot resolve a saved ID while its dynamic catalog is
            // still empty. Keep the persisted IDs authoritative until the native runtime
            // has returned the matching model objects.
            var requestedGeneralModel = PreferCurrentSelection(
                SelectedModel,
                settings.Current.SelectedModel,
                AppSettings.DefaultSelectedModel);
            SelectedModel = requestedGeneralModel;
            using var client = await missumAi.CreateClientAsync(cancellationToken);
            // Load the Coding catalog even while another native model is being prepared.
            await RefreshCodingModelsAsync(client, cancellationToken);
            status = await missumAi.TestAsync(cancellationToken);
            if (status is null)
            {
                Models.Clear();
                return null;
            }
            IReadOnlyList<LocalAiModel> items;
            if (!status.IsReachable)
            {
                throw new InvalidOperationException(status.Message);
            }
            using var modelTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            modelTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            var modelStatus = await client.GetModelStatusAsync(modelTimeout.Token);
            if (!modelStatus.ProviderReachable)
            {
                throw new InvalidOperationException(
                    missumAi.NativeRuntimeError ?? modelStatus.ErrorMessage ?? "Der Modellkatalog des AI-Servers ist nicht erreichbar. Prüfe die Gatewayverbindung und die Serverlaufzeit.");
            }
            modelCapabilities.Update(modelStatus);
            items = modelStatus.Models
                .Where(model => model.Downloaded
                    && model.Role == "general")
                .Select(model => new LocalAiModel(
                    model.Id,
                    string.Format(CultureInfo.CurrentCulture, "{0} · {1:N0} Token", model.DisplayName ?? model.Id, model.ContextTokens),
                    model.ContextTokens,
                    model.SupportsTools,
                    model.SupportsVision,
                    model.ReasoningEfforts,
                    model.DefaultReasoningEffort))
                .ToArray();
            ConnectionStatus = status.Message;

            Models.Clear();
            foreach (var item in items)
            {
                Models.Add(item);
            }
            // The selection may have changed while the asynchronous catalog
            // request was running. Re-read the current properties and keep
            // them authoritative. A temporarily incomplete local model catalog
            // must not silently replace a persisted model with the first item.
            SelectedModel = PreferCurrentSelection(
                SelectedModel,
                settings.Current.SelectedModel,
                requestedGeneralModel);
            // Bind the ComboBoxes to the actual catalog objects. SelectedValue
            // can remain visually empty when its value was assigned before an
            // asynchronously populated ItemsSource existed.
            SelectedGeneralModelItem = EnsureModelItem(Models, SelectedModel);
            IsServerReady = status.IsReady;
            shell.ApplyAiConnectionState(true, status.IsReady);
            return status;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (status?.IsReachable == true)
            {
                ConnectionStatus = $"Verbunden · Modellstatus nicht verfügbar · {exception.Message}";
                shell.ApplyAiConnectionState(true, status.IsReady);
            }
            else
            {
                ConnectionStatus = string.IsNullOrWhiteSpace(exception.Message)
                    ? "Missum AI Server nicht erreichbar"
                    : exception.Message;
                IsServerReady = false;
                shell.ApplyAiConnectionState(false, false);
            }
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCodingModelsAsync(Missum.Ai.Client.MissumAiClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var catalog = await client.GetCodingModelsAsync(timeout.Token);
            var selected = SelectedCodingModel ?? settings.Current.SelectedCodingModel;
            CodingModels.Clear();
            foreach (var item in catalog.Models)
            {
                CodingModels.Add(new LocalAiModel(item.Id,
                    $"{item.DisplayName ?? item.Id} · {item.ContextTokens:N0} Token",
                    item.ContextTokens, item.SupportsTools, item.SupportsVision));
            }
            SelectedCodingModel = selected;
            SelectedCodingModelItem = EnsureModelItem(CodingModels, selected);
            CodingModelStatus = $"{catalog.Models.Count} Modelle"
                + (string.IsNullOrWhiteSpace(catalog.Message) ? string.Empty : $" · {catalog.Message}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            CodingModelStatus = $"Coding-Modellkatalog nicht erreichbar: {exception.Message}";
        }
    }

    internal static string PreferCurrentSelection(string? current, string? persisted, string fallback) =>
        !string.IsNullOrWhiteSpace(current)
            ? current.Trim()
            : !string.IsNullOrWhiteSpace(persisted)
                ? persisted.Trim()
                : fallback;

    private static LocalAiModel? FindModel(IEnumerable<LocalAiModel> models, string? id) =>
        models.FirstOrDefault(model =>
            string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static LocalAiModel? EnsureModelItem(ObservableCollection<LocalAiModel> models, string? id)
    {
        var normalized = id?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var existing = FindModel(models, normalized);
        if (existing is not null)
        {
            return existing;
        }

        // Display the persisted selection even before the asynchronous native
        // model catalog is reachable. A successful refresh replaces this
        // placeholder with the authoritative descriptor.
        var placeholder = new LocalAiModel(normalized, normalized);
        models.Insert(0, placeholder);
        return placeholder;
    }

    public Task<BackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        _sharedSettings.CreateBackupAsync(destinationPath, cancellationToken);

    public async Task RestoreBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await _sharedSettings.RestoreBackupAsync(backupPath, cancellationToken);
    }

    partial void OnSelectedGeneralModelItemChanged(LocalAiModel? value)
    {
        if (value is not null)
        {
            SelectedModel = value.Id;
        }
    }

    partial void OnSelectedCodingModelItemChanged(LocalAiModel? value)
    {
        if (value is not null) SelectedCodingModel = value.Id;
    }
}
