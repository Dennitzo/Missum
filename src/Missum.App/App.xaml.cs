using Missum.App.Services;
using Missum.App.Services.Extensions;
using Missum.App.ViewModels;
using Missum.Ai.Contracts;
using Missum.Core.Chat;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Memory;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Extensions;
using Missum.Infrastructure.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using System.Collections.Concurrent;
using System.Globalization;
using Windows.UI.ViewManagement;

namespace Missum.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the Application lifetime; PrepareShutdownAsync cancels and disposes the monitor token.")]
public partial class App : Application
{
    private static readonly TimeSpan AiAvailabilityProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan AiDiagnosticProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] AccentBrushKeys =
    [
        "MissumAccentBrush",
        "MissumAccentSubtleBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabledBrush",
        "AccentTextFillColorPrimaryBrush",
        "ToggleSwitchFillOn",
        "ToggleSwitchFillOnPointerOver",
        "ToggleSwitchFillOnPressed",
        "ToggleSwitchFillOnDisabled",
        "ToggleSwitchStrokeOn",
        "ToggleSwitchStrokeOnPointerOver",
        "ToggleSwitchStrokeOnPressed",
        "ToggleSwitchStrokeOnDisabled",
        "NavigationViewSelectionIndicatorForeground",
    ];
    private static readonly string[] AccentTextBrushKeys =
    [
        "MissumAccentTextBrush",
        "TextOnAccentFillColorPrimaryBrush",
        "TextOnAccentFillColorSecondaryBrush",
        "TextOnAccentFillColorDisabledBrush",
        "ToggleSwitchKnobFillOn",
        "ToggleSwitchKnobFillOnPointerOver",
        "ToggleSwitchKnobFillOnPressed",
        "ToggleSwitchKnobFillOnDisabled",
    ];
    private readonly IHost _host;
    private readonly ConcurrentQueue<Guid?> _pendingActivationTargets = new();
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private AppInstance? _appInstance;
    private MainWindow? _window;
    private FrameworkElement? _themeRoot;
    private AccessibilitySettings? _accessibilitySettings;
    private bool _highContrastEventsSubscribed;
    private AppTheme _appliedTheme = AppTheme.System;
    private CancellationTokenSource? _aiAvailabilityCancellation;
    private Task? _aiAvailabilityMonitor;
    private string? _lastLoggedAiConnectionState;
    private int _activationReady;
    private int _shutdownStarted;
    private Uri? _lifecycleGateway;
    private AssistantResolvedAppearance? _resolvedAppearance;
    private UISettings? _appearanceSystemSettings;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        RuntimeProfile = AssistantRuntimeProfile.Resolve();
        DataDirectory = RuntimeProfile.DataDirectory;
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(RuntimeProfile);
                services.AddMissumInfrastructure(options => options.DataDirectory = DataDirectory);
                services.AddProjectMemory();
                services.AddSingleton<IAextSignatureVerifier>(
                    new DirectoryAextSignatureVerifier(Path.Combine(DataDirectory, "Extensions", "trusted-keys")));
                services.AddAssistantExtensionFoundation(new(Path.Combine(DataDirectory, "Extensions")));
                services.AddExtensionHostSupervisor();
                services.AddAssistantRunScheduling();
                services.AddSingleton<SettingsCoordinator>();
                services.AddSingleton<AssistantSettingsService>();
                services.AddSingleton<AssistantClientStateStore>();
                services.AddSingleton<AssistantHost>();
                services.AddSingleton<BrowserSpeechService>();
                services.AddSingleton<NativeChatPdfExportService>();
                services.AddSingleton<Missum.Core.Research.IResearchSandboxService, ResearchSandboxService>();
                services.AddSingleton<AssistantSessionActivationService>();
                services.AddSingleton<NativeModelRuntimeService>();
                services.AddSingleton<MissumAiStackLifecycleService>();
                services.AddSingleton<MissumAiConnectionService>();
                services.AddSingleton<ModelCapabilityRegistry>();
                services.AddSingleton<SystemAudioCaptionService>();
                services.AddSingleton<MicrophoneTranscriptionService>();
                services.AddSingleton<SystemAudioAnalysisCaptureService>();
                services.AddSingleton<DesktopScreenshotService>();
                services.AddSingleton<ScreenClipCaptureService>();
                services.AddSingleton<AssistantArtifactOriginalResolver>();
                services.AddSingleton<AssistantArtifactPreviewService>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<RecentActivityService>();
                services.AddSingleton<ProjectAssetActivityService>();
                services.AddSingleton<AssistantCoordinator>(static provider =>
                {
                    var missumAi = provider.GetRequiredService<MissumAiAssistantService>();
                    var microphone = provider.GetRequiredService<MicrophoneTranscriptionService>();
                    return new AssistantCoordinator(
                        provider.GetRequiredService<IChatRepository>(),
                        provider.GetRequiredService<IDocumentIngestor>(),
                        provider.GetRequiredService<IContextAssembler>(),
                        provider.GetRequiredService<IPromptTriggerRepository>(),
                        provider.GetRequiredService<IAssistantAttachmentRepository>(),
                        provider.GetRequiredService<IChatArtifactRepository>(),
                        provider.GetRequiredService<IConversationSnapshotRepository>(),
                        missumAi,
                        provider.GetRequiredService<SettingsCoordinator>(),
                        provider.GetRequiredService<RecentActivityService>(),
                        microphone,
                        provider.GetRequiredService<AssistantRuntimeProfile>(),
                        provider.GetRequiredService<IExtensionActionCatalog>(),
                        provider.GetRequiredService<IExtensionRuntimeService>(),
                        provider.GetRequiredService<IProjectMemoryStore>(),
                        provider.GetRequiredService<IAssistantRunScheduler>(),
                        provider.GetRequiredService<Missum.Core.Research.IScientificResearchRepository>(),
                        provider.GetRequiredService<Missum.Core.Research.IScientificResearchExportService>());
                });
                services.AddSingleton<LogsViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<DocumentContextPreparationService>();
                services.AddSingleton<SessionContextPreparationService>();
                services.AddSingleton<DocumentPdfExporter>();
                services.AddSingleton<ScientificPublicationService>();
                services.AddSingleton<Missum.Core.Research.IScientificPublicationProvider>(provider => provider.GetRequiredService<ScientificPublicationService>());
                services.AddSingleton<ScientificSimulationService>();
                services.AddSingleton<ScientificPresentationCoordinator>();
                services.AddSingleton<LocalDocumentToolService>();
                services.AddSingleton<LocalToolBroker>();
                services.AddSingleton<MissumAiAssistantService>();
            })
            .Build();
    }

    public static new App Current => (App)Application.Current;

    public MainWindow? MainWindow => _window;

    public string DataDirectory { get; }

    public AssistantRuntimeProfile RuntimeProfile { get; }

    public string AccentColor { get; private set; } = AppSettings.DefaultAccentColor;

    public string BackgroundColor { get; private set; } = AppSettings.DefaultBackgroundColor;

    public event EventHandler? ThemeChanged;

    internal bool IsHighContrastActive => IsHighContrastEnabled();

    public T GetService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    public void ApplyTheme(AppTheme theme)
    {
        _appliedTheme = theme;
        if ((_themeRoot ?? _window?.Content as FrameworkElement) is { } root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        ApplyPaletteColors();
    }

    public void ApplyAccentColor(string accentColor)
    {
        if (!TryParsePaletteColor(accentColor, out var color))
        {
            accentColor = AppSettings.DefaultAccentColor;
            _ = TryParsePaletteColor(accentColor, out color);
        }

        AccentColor = accentColor.ToUpperInvariant();
        if (!IsHighContrastEnabled())
        {
            SetBrushColors(AccentBrushKeys, color);
            SetBrushColors(AccentTextBrushKeys, ContrastForeground(color));
            UpdateReadableAccentBrush();
        }

        CacheResolvedAppearance();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyBackgroundColor(string backgroundColor)
    {
        if (!TryParsePaletteColor(backgroundColor, out var color))
        {
            backgroundColor = AppSettings.DefaultBackgroundColor;
            _ = TryParsePaletteColor(backgroundColor, out color);
        }

        BackgroundColor = backgroundColor.ToUpperInvariant();
        if (!IsHighContrastEnabled())
        {
            ApplyBackgroundSurfaceColors(color);
            UpdateReadableAccentBrush();
        }

        CacheResolvedAppearance();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
            _appInstance = AppInstance.FindOrRegisterForKey(GetInstanceKey());
            if (!_appInstance.IsCurrent)
            {
                await _appInstance.RedirectActivationToAsync(activationArguments);
                Environment.Exit(0);
                return;
            }

            _appInstance.Activated += OnAppInstanceActivated;
            TryRegisterSessionProtocol();
            await _host.StartAsync();
            var database = GetService<IMissumDatabase>();
            await database.InitializeAsync();
            await GetService<IProjectMemoryStore>().InitializeAsync();
            _ = await GetService<IChatRepository>().MarkStreamingMessagesInterruptedAsync();
            var settings = GetService<SettingsCoordinator>();
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => ApplyRuntimeProfile(current, RuntimeProfile));
            _lifecycleGateway = Uri.TryCreate(settings.Current.MissumAiServerUrl, UriKind.Absolute, out var gateway)
                && gateway.Scheme is "http" or "https" ? gateway : RuntimeProfile.GatewayUri;
            // Freeze and retire only the jobs found on launch, before the
            // visible window can accept a new prompt. Remote cancellation can
            // wait for the local services to become reachable.
            await GetService<MissumAiAssistantService>().PreparePersistedRunsAtStartupAsync();
            _ = await GetService<IChatRepository>().DeleteEmptyTerminalMessagesAsync();
            await ApplySessionActivationAsync(
                GetTargetSessionId(activationArguments),
                navigateToAssistant: false);

            GetService<RecentActivityService>().Restore();
            GetService<ProjectAssetActivityService>().Start();
            _window = GetService<MainWindow>();
            if (_window.Content is FrameworkElement themeRoot)
            {
                _themeRoot = themeRoot;
                _themeRoot.ActualThemeChanged += OnActualThemeChanged;
            }

            SubscribeHighContrastChanges();
            ApplyTheme(settings.Current.Theme);
            ApplyAccentColor(settings.Current.AccentColor);
            ApplyBackgroundColor(settings.Current.BackgroundColor);
            _window.Closed += OnWindowClosed;
            _window.BeforeCloseAsync = PrepareShutdownAsync;
            _window.Activate();
            var sharedSettings = GetService<AssistantSettingsService>();
            sharedSettings.ResolvedAppearanceProvider = () => Volatile.Read(ref _resolvedAppearance);
            sharedSettings.ResolvedThemeProvider = () => Volatile.Read(ref _resolvedAppearance)?.Theme ?? "dark";
            settings.Changed += OnSharedSettingsChanged;
            await GetService<AssistantHost>().StartAsync();
            Volatile.Write(ref _activationReady, 1);
            while (_pendingActivationTargets.TryDequeue(out var targetSessionId))
            {
                await ApplySessionActivationAsync(targetSessionId, navigateToAssistant: true);
            }
            StartAiAvailabilityMonitoring();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Missum startup failed: {exception}");
            throw;
        }
    }

    private void OnSharedSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        if (!args.PreferencesChanged) return;
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTheme(args.Current.Theme);
            ApplyAccentColor(args.Current.AccentColor);
            ApplyBackgroundColor(args.Current.BackgroundColor);
        });
    }

    private void StartAiAvailabilityMonitoring()
    {
        var shell = GetService<ShellViewModel>();
        shell.BeginAiAvailabilityCheck();
        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _aiAvailabilityCancellation = cancellation;
        _aiAvailabilityMonitor = MonitorLocalAiAvailabilityAsync(cancellation.Token);
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        var targetSessionId = GetTargetSessionId(args);
        var window = _window;
        if (Volatile.Read(ref _activationReady) == 0 || window is null)
        {
            _pendingActivationTargets.Enqueue(targetSessionId);
            return;
        }

        _ = window.DispatcherQueue.TryEnqueue(() =>
        {
            _ = ApplySessionActivationAsync(targetSessionId, navigateToAssistant: true);
        });
    }

    private async Task ApplySessionActivationAsync(Guid? targetSessionId, bool navigateToAssistant)
    {
        await _activationGate.WaitAsync().ConfigureAwait(true);
        try
        {
            AssistantSessionActivation? activation = null;
            if (targetSessionId is { } sessionId)
            {
                activation = await GetService<AssistantSessionActivationService>()
                    .ActivateAsync(sessionId)
                    .ConfigureAwait(true);
            }

            if (navigateToAssistant && activation is not null && _window is { } window)
            {
                await window.OpenAssistantSessionAsync(activation.SessionChanged).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.SessionActivationFailed(GetService<ILogger<App>>(), exception);
        }
        finally
        {
            _activationGate.Release();
            if (navigateToAssistant)
            {
                _window?.BringToForeground();
            }
        }
    }

    private Guid? GetTargetSessionId(AppActivationArguments? args)
    {
        if (args?.Kind != ExtendedActivationKind.Protocol
            || args.Data is not Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
        {
            return null;
        }

        return AssistantSessionUri.TryParse(protocolArgs.Uri, RuntimeProfile.Name, out var sessionId)
            ? sessionId
            : null;
    }

    private void TryRegisterSessionProtocol()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY"))
            || string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            executablePath = Path.GetFullPath(executablePath);
            ActivationRegistrationManager.RegisterForProtocolActivation(
                AssistantSessionUri.ProtocolScheme(RuntimeProfile.Name),
                executablePath + ",0",
                RuntimeProfile.ProductName,
                executablePath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.SessionProtocolRegistrationFailed(GetService<ILogger<App>>(), exception);
        }
    }

    private async Task MonitorLocalAiAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var gateway = _lifecycleGateway ?? RuntimeProfile.GatewayUri;
            await GetService<MissumAiStackLifecycleService>().EnsureStartedAsync(gateway, cancellationToken).ConfigureAwait(false);
            await GetService<NativeModelRuntimeService>().EnsureStartedAsync(gateway, cancellationToken).ConfigureAwait(false);
            await GetService<MissumAiAssistantService>().StopPersistedRunsAtStartupAsync(cancellationToken).ConfigureAwait(false);
            await RecordAiLifecycleAsync("start", "completed").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.LocalAiAvailabilityCheckFailed(GetService<ILogger<App>>(), exception);
            await RecordAiLifecycleAsync("start", "failed", exception).ConfigureAwait(false);
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            var hasActiveRuns = await RefreshLocalAiAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(
                    hasActiveRuns ? TimeSpan.FromMilliseconds(750) : TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> RefreshLocalAiAvailabilityAsync(CancellationToken cancellationToken)
    {
        var currentSettings = GetService<SettingsCoordinator>().Current;
        var connected = false;
        var serverReady = false;
        GpuStatusSnapshot? gpuStatus = null;
        ModelStatusSnapshot? modelStatus = null;
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus = null;
        try
        {
            using var availabilityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            availabilityCancellation.CancelAfter(AiAvailabilityProbeTimeout);
            var availabilityToken = availabilityCancellation.Token;
            using var client = await GetService<MissumAiConnectionService>()
                .CreateClientAsync(availabilityToken)
                .ConfigureAwait(false);
            var healthTask = client.GetReadyHealthAsync(availabilityToken);
            var capabilitiesTask = client.GetCapabilitiesAsync(availabilityToken);

            // Readiness may legitimately be degraded because one optional
            // model is still downloading. Capabilities is authenticated and
            // therefore proves that this client can use the gateway.
            // Observe both parallel requests even when the gateway goes offline;
            // otherwise the second fault escapes as UnobservedTaskException.
            await Task.WhenAll(healthTask, capabilitiesTask).ConfigureAwait(false);
            var health = await healthTask.ConfigureAwait(false);
            var capabilities = await capabilitiesTask.ConfigureAwait(false);
            GetService<ModelCapabilityRegistry>().Update(capabilities);
            connected = string.Equals(
                health.ProtocolVersion,
                currentSettings.MissumAiProtocolVersion,
                StringComparison.Ordinal)
                && string.Equals(
                    capabilities.ProtocolVersion,
                    currentSettings.MissumAiProtocolVersion,
                    StringComparison.Ordinal);
            serverReady = connected && health.Status is "ready" or "modelLoading" or "modelNotLoaded";
            if (connected)
            {
                // Docker start acknowledgement is earlier than HTTP readiness.
                // Retry only the captured startup jobs once the gateway answers.
                await GetService<MissumAiAssistantService>()
                    .StopPersistedRunsAtStartupAsync(availabilityToken)
                    .ConfigureAwait(false);
            }

            // Diagnostic endpoints enrich individual service chips but must
            // never downgrade an already authenticated gateway connection.
            using var diagnosticCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            diagnosticCancellation.CancelAfter(AiDiagnosticProbeTimeout);
            var diagnosticToken = diagnosticCancellation.Token;
            var gpuTask = AwaitOptionalStatusAsync(client.GetGpuStatusAsync(diagnosticToken), cancellationToken);
            var modelTask = AwaitOptionalStatusAsync(client.GetModelStatusAsync(diagnosticToken), cancellationToken);
            var serviceTask = AwaitOptionalStatusAsync(client.GetServiceStatusAsync(diagnosticToken), cancellationToken);
            await Task.WhenAll(gpuTask, modelTask, serviceTask).ConfigureAwait(false);
            gpuStatus = await gpuTask.ConfigureAwait(false);
            modelStatus = await modelTask.ConfigureAwait(false);
            serviceStatus = await serviceTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            AppLog.LocalAiAvailabilityCheckFailed(GetService<ILogger<App>>(), exception);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        SetLocalAiStatus(connected, serverReady, gpuStatus, modelStatus, serviceStatus);
        return gpuStatus?.ActiveWorkloads is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(gpuStatus?.ActiveLease);
    }

    private static async Task<T?> AwaitOptionalStatusAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void SetLocalAiStatus(
        bool connected,
        bool serverReady,
        GpuStatusSnapshot? gpuStatus,
        ModelStatusSnapshot? modelStatus,
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus)
    {
        var connectionState = !connected
            ? "Nicht erreichbar"
            : serverReady
                ? "Online"
                : "Online · Eingeschränkt";
        if (!string.Equals(
                Interlocked.Exchange(ref _lastLoggedAiConnectionState, connectionState),
                connectionState,
                StringComparison.Ordinal))
        {
            var logger = GetService<ILogger<App>>();
            if (logger.IsEnabled(LogLevel.Information))
            {
                AppLog.LocalAiConnectionStateChanged(logger, connectionState);
            }
        }

        var shell = GetService<ShellViewModel>();
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            shell.ApplyAiAvailabilitySnapshot(connected, serverReady, gpuStatus, modelStatus, serviceStatus);
            return;
        }

        _ = dispatcher.TryEnqueue(() =>
        {
            shell.ApplyAiAvailabilitySnapshot(connected, serverReady, gpuStatus, modelStatus, serviceStatus);
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_highContrastEventsSubscribed && _accessibilitySettings is not null)
        {
            _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
            _highContrastEventsSubscribed = false;
        }

        _accessibilitySettings = null;
        if (_appearanceSystemSettings is not null)
        {
            _appearanceSystemSettings.ColorValuesChanged -= OnSystemAppearanceChanged;
            _appearanceSystemSettings = null;
        }
        if (_themeRoot is not null)
        {
            _themeRoot.ActualThemeChanged -= OnActualThemeChanged;
            _themeRoot = null;
        }

        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.BeforeCloseAsync = null;
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_appliedTheme == AppTheme.System)
        {
            ApplyPaletteColors();
        }
        else if (GetService<SettingsCoordinator>().Current.Theme == AppTheme.System)
            CacheResolvedAppearance(systemAppearanceChanged: true);
    }

    private void SubscribeHighContrastChanges()
    {
        try
        {
            _accessibilitySettings = new AccessibilitySettings();
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
            _highContrastEventsSubscribed = true;
            _appearanceSystemSettings = new UISettings();
            _appearanceSystemSettings.ColorValuesChanged += OnSystemAppearanceChanged;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var logger = GetService<ILogger<App>>();
            if (logger.IsEnabled(LogLevel.Debug))
            {
                AppLog.SystemThemeEventUnavailable(
                    logger,
                    exception,
                    "AccessibilitySettings.HighContrastChanged");
            }
        }
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args)
    {
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            _ = dispatcher.TryEnqueue(() => { ApplyPaletteColors(); CacheResolvedAppearance(systemAppearanceChanged: true); });
            return;
        }

        ApplyPaletteColors();
        CacheResolvedAppearance(systemAppearanceChanged: true);
    }

    private void OnSystemAppearanceChanged(UISettings sender, object args)
    {
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            _ = dispatcher.TryEnqueue(() => CacheResolvedAppearance(systemAppearanceChanged: true));
        else if (dispatcher is not null) CacheResolvedAppearance(systemAppearanceChanged: true);
    }

    private async Task PrepareShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            var stack = GetService<MissumAiStackLifecycleService>();
            var nativeRuntime = GetService<NativeModelRuntimeService>();
            var gateway = _lifecycleGateway ?? RuntimeProfile.GatewayUri;
            stack.BeginShutdown();
            nativeRuntime.BeginShutdown();
            var availabilityCancellation = Interlocked.Exchange(ref _aiAvailabilityCancellation, null);
            var availabilityMonitor = Interlocked.Exchange(ref _aiAvailabilityMonitor, null);
            availabilityCancellation?.Cancel();
            if (availabilityMonitor is not null)
            {
                try
                {
                    await availabilityMonitor.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // The availability monitor is expected to stop during shutdown.
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    AppLog.ShutdownCleanupFailed(GetService<ILogger<App>>(), exception);
                }
            }

            availabilityCancellation?.Dispose();
            var assistant = GetService<MissumAiAssistantService>();
            // Capture the active server run before cancellation drains the queue
            // and detaches its stream. No queued prompt may start during shutdown.
            using var runDrain = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var activeRun = assistant.CancelCurrentAndWaitAsync(runDrain.Token);
            var queue = GetService<IAssistantRunScheduler>().DisposeAsync().AsTask();
            await RunShutdownStepAsync("runs", assistant.StopPersistedRunsForShutdownAsync, TimeSpan.FromSeconds(20));
            await RunShutdownStepAsync("run-drain", token => Task.WhenAll(activeRun, queue).WaitAsync(token), TimeSpan.FromSeconds(25));
            // A server acceptance can arrive while the first scan still sees a
            // queued local attempt. Drain first, then retire its saved identity.
            await RunShutdownStepAsync("final-runs", assistant.StopPersistedRunsForShutdownAsync, TimeSpan.FromSeconds(20));
            // Each cleanup stage is independent. An unavailable Docker daemon
            // must never prevent both native GPU runtimes from being released.
            await RunShutdownStepAsync("docker", token => stack.StopAsync(gateway, token), TimeSpan.FromSeconds(45));
            await RunShutdownStepAsync("models", token => nativeRuntime.StopOwnedAsync(gateway, token), TimeSpan.FromSeconds(45));
            await RunShutdownStepAsync("final-run-drain", token => queue.WaitAsync(token), TimeSpan.FromSeconds(10));
            await RunShutdownStepAsync("host", _ => _host.StopAsync(TimeSpan.FromSeconds(4)), TimeSpan.FromSeconds(6));
        }
        finally
        {
            _appInstance?.UnregisterKey();
            if (_host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        TryLogCritical(args.Exception, $"Unhandled XAML exception: {args.Message}");
    }

    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        TryLogCritical(args.ExceptionObject as Exception, $"Unhandled AppDomain exception; terminating={args.IsTerminating}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        TryLogCritical(args.Exception, "Unobserved task exception");
        args.SetObserved();
    }

    private void TryLogCritical(Exception? exception, string details)
    {
        try
        {
            var directory = Path.Combine(DataDirectory, "Diagnostics");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.UtcNow:O} {details}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception loggingError) when (loggingError is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(loggingError);
        }
        try
        {
            if (exception is not null)
            {
                details = $"{details} | {exception.GetType().FullName} "
                    + $"(0x{exception.HResult:X8}) | {exception.StackTrace}";
            }

            var logger = GetService<ILogger<App>>();
            if (logger.IsEnabled(LogLevel.Critical))
            {
                AppLog.UnhandledApplicationException(logger, exception, details);
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine($"{details}: {exception}");
        }
    }

    private string GetInstanceKey() => RuntimeProfile.InstanceKey;

    internal static AppSettings ApplyRuntimeProfile(AppSettings current, AssistantRuntimeProfile profile)
    {
        var explicitProfile = Environment.GetEnvironmentVariable("ASSISTANT_PROFILE");
        var explicitGateway = Environment.GetEnvironmentVariable("ASSISTANT_GATEWAY_URL");
        if (!profile.HasExplicitProfileSelection
            && string.IsNullOrWhiteSpace(explicitProfile)
            && string.IsNullOrWhiteSpace(explicitGateway)) return current;
        return current with { MissumAiServerUrl = profile.GatewayUri.AbsoluteUri.TrimEnd('/') };
    }

    private static IEnumerable<ResourceDictionary> EnumerateResourceDictionaries(ResourceDictionary root)
    {
        yield return root;
        foreach (var merged in root.MergedDictionaries)
        {
            foreach (var nested in EnumerateResourceDictionaries(merged))
            {
                yield return nested;
            }
        }
    }

    private void ApplyPaletteColors()
    {
        if (IsHighContrastEnabled())
        {
            ApplyHighContrastPalette(ResolveHighContrastPalette());
        }
        else
        {
            _ = TryParsePaletteColor(AccentColor, out var accent);
            _ = TryParsePaletteColor(BackgroundColor, out var background);
            SetBrushColors(AccentBrushKeys, accent);
            SetBrushColors(AccentTextBrushKeys, ContrastForeground(accent));
            ApplyBackgroundSurfaceColors(background);
        }

        UpdateReadableAccentBrush();
        CacheResolvedAppearance();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CacheResolvedAppearance(bool systemAppearanceChanged = false)
    {
        // Called only alongside native palette application on the UI thread.
        // LAN requests receive this immutable cache and never touch WinUI objects.
        var highContrast = IsHighContrastEnabled();
        var saved = GetService<SettingsCoordinator>().Current;
        if (!MatchesSavedAppearance(saved, _appliedTheme, AccentColor, BackgroundColor))
        {
            // Native SettingsPage.Apply* previews are local until preferences save.
            // System accessibility/theme changes still resolve the saved palette.
            if (!systemAppearanceChanged) return;
            var systemLight = RequestedTheme == ApplicationTheme.Light;
            if (saved.Theme == AppTheme.System && !highContrast)
            {
                try
                {
                    var systemBackground = new UISettings().GetColorValue(UIColorType.Background);
                    systemLight = systemBackground.R + systemBackground.G + systemBackground.B > 384;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(exception); }
            }
            PublishResolvedAppearance(CreateSavedAppearance(saved, systemLight, highContrast ? ResolveHighContrastPalette() : null));
            return;
        }
        var light = _appliedTheme == AppTheme.Light
            || (_appliedTheme == AppTheme.System && _themeRoot?.ActualTheme == ElementTheme.Light);
        string ReadBrush(string key, Windows.UI.Color fallback)
        {
            if (Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                return CssColor(brush.Color, brush.Opacity);
            return CssColor(fallback, 1);
        }
        var foreground = light ? Windows.UI.Color.FromArgb(228, 0, 0, 0) : Microsoft.UI.Colors.White;
        if (highContrast) foreground = ResolveHighContrastPalette().Foreground;
        var colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window"] = ReadBrush("MissumWindowBrush", Microsoft.UI.Colors.Black),
            ["layer"] = ReadBrush("MissumLayerBrush", Microsoft.UI.Colors.Black),
            ["layerStrong"] = ReadBrush("MissumLayerStrongBrush", Microsoft.UI.Colors.Black),
            ["input"] = ReadBrush("MissumInputBrush", Microsoft.UI.Colors.Black),
            ["hover"] = ReadBrush("MissumHoverBrush", Microsoft.UI.Colors.Black),
            ["pressed"] = ReadBrush("MissumPressedBrush", Microsoft.UI.Colors.Black),
            ["stroke"] = ReadBrush("MissumStrokeBrush", foreground),
            ["mutedText"] = ReadBrush("MissumMutedTextBrush", foreground),
            ["text"] = CssColor(foreground, 1),
            ["accent"] = ReadBrush("MissumAccentBrush", foreground),
            ["accentReadable"] = ReadBrush("MissumAccentReadableBrush", foreground),
            ["accentForeground"] = ReadBrush("MissumAccentTextBrush", Microsoft.UI.Colors.Black),
            ["accentSubtle"] = ReadBrush("MissumAccentSubtleBrush", foreground),
        };
        if ((_window?.Content as FrameworkElement)?.FindName("ContentFrame") is Microsoft.UI.Xaml.Controls.Frame frame
            && frame.Content is Microsoft.UI.Xaml.Controls.Control page && page.ActualTheme == (light ? ElementTheme.Light : ElementTheme.Dark)
            && page.Foreground is SolidColorBrush text)
            colors["text"] = CssColor(text.Color, text.Opacity);
        // The native title bar layers AccentSubtle over the window surface.
        colors["titlebar"] = ComposeCssColors(colors["accentSubtle"], colors["window"]);
        foreach (var name in new[] { "Web", "Research", "Image", "Audio", "Speech", "Pdf", "Document", "Plan", "Code", "Folder", "Navigation", "Link", "Add", "Danger", "Settings", "Subagent" })
            colors["icon" + name] = ReadBrush("MissumIcon" + name + "Brush", Controls.NativeIconPalette.ColorFor(name));
        var snapshot = new AssistantResolvedAppearance(highContrast ? "high-contrast" : light ? "light" : "dark",
            highContrast, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(colors));
        PublishResolvedAppearance(snapshot);
    }

    internal static bool MatchesSavedAppearance(AppSettings saved, AppTheme appliedTheme, string accent, string background) =>
        saved.Theme == appliedTheme && string.Equals(saved.AccentColor, accent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(saved.BackgroundColor, background, StringComparison.OrdinalIgnoreCase);

    internal static AssistantResolvedAppearance CreateSavedAppearance(AppSettings saved, bool systemLight, HighContrastPalette? highContrast)
    {
        var light = saved.Theme == AppTheme.Light || (saved.Theme == AppTheme.System && systemLight);
        _ = TryParsePaletteColor(saved.AccentColor, out var accent);
        _ = TryParsePaletteColor(saved.BackgroundColor, out var background);
        var foreground = light ? Windows.UI.Color.FromArgb(228, 0, 0, 0) : Microsoft.UI.Colors.White;
        string Mixed(uint baseRgb, double weight, byte alpha = 255) => CssColor(MixBackground(baseRgb, background, weight, alpha), 1);
        var colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window"] = Mixed(light ? 0xF3F0F5u : 0x202020u, light ? .07 : .08),
            ["layer"] = Mixed(light ? 0xFFFFFFu : 0x202020u, light ? .04 : .12, light ? (byte)248 : (byte)230),
            ["layerStrong"] = Mixed(light ? 0xFAF8FCu : 0x2B2B2Bu, light ? .08 : .18, light ? (byte)255 : (byte)242),
            ["input"] = Mixed(light ? 0xFFFFFFu : 0x333333u, light ? .09 : .20),
            ["hover"] = Mixed(light ? 0xF5F1F7u : 0x303030u, light ? .13 : .23),
            ["pressed"] = Mixed(light ? 0xEEE9F1u : 0x383838u, light ? .18 : .28),
            ["stroke"] = Mixed(light ? 0x302A38u : 0xFFFFFFu, light ? .22 : .28, light ? (byte)82 : (byte)66),
            ["mutedText"] = light ? "#51465D" : "#999999",
            ["text"] = CssColor(foreground, 1),
            ["accent"] = CssColor(accent, 1), ["accentForeground"] = CssColor(ContrastForeground(accent), 1),
            ["accentSubtle"] = CssColor(accent, .14),
        };
        if (highContrast is { } contrast)
        {
            foreach (var key in new[] { "window", "layer", "layerStrong", "input", "hover", "pressed" }) colors[key] = CssColor(contrast.Background, 1);
            foreach (var key in new[] { "stroke", "mutedText", "text" }) colors[key] = CssColor(contrast.Foreground, 1);
            colors["accent"] = CssColor(contrast.Accent, 1); colors["accentForeground"] = CssColor(contrast.AccentForeground, 1);
            colors["accentSubtle"] = CssColor(contrast.Accent, .14);
        }
        var surfaces = light ? LightTextSurfaces(background) : [Microsoft.UI.Colors.Black];
        colors["accentReadable"] = CssColor(highContrast is { } high
            ? ReadableAccentColor(high.Accent, [high.Background])
            : light ? ReadableAccentColor(accent, surfaces) : accent, 1);
        colors["titlebar"] = ComposeCssColors(colors["accentSubtle"], colors["window"]);
        foreach (var name in new[] { "Web", "Research", "Image", "Audio", "Speech", "Pdf", "Document", "Plan", "Code", "Folder", "Navigation", "Link", "Add", "Danger", "Settings", "Subagent" })
            colors["icon" + name] = CssColor(Controls.NativeIconPalette.ColorFor(name), 1);
        return new(highContrast is not null ? "high-contrast" : light ? "light" : "dark", highContrast is not null,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(colors));
    }

    private void PublishResolvedAppearance(AssistantResolvedAppearance snapshot)
    {
        Volatile.Write(ref _resolvedAppearance, snapshot);
        if (Volatile.Read(ref _activationReady) != 0) _ = RefreshBrowserAppearanceAsync();
    }

    private async Task RefreshBrowserAppearanceAsync()
    {
        try { await GetService<AssistantHost>().RefreshAppearanceAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(exception); }
    }

    private static string CssColor(Windows.UI.Color color, double opacity)
    {
        var alpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
        return alpha == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.R:X2}{color.G:X2}{color.B:X2}{alpha:X2}";
    }

    private static string ComposeCssColors(string foreground, string background)
    {
        var source = uint.Parse(foreground.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var target = uint.Parse(background.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var alpha = foreground.Length == 9 ? byte.Parse(foreground.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d : 1;
        byte Channel(int shift) => (byte)Math.Round(((source >> shift) & 255) * alpha + ((target >> shift) & 255) * (1 - alpha));
        return $"#{Channel(16):X2}{Channel(8):X2}{Channel(0):X2}";
    }

    private async Task RunShutdownStepAsync(string stage, Func<CancellationToken, Task> action, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await action(cancellation.Token).WaitAsync(cancellation.Token);
            await RecordAiLifecycleAsync(stage, "completed");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.ShutdownCleanupFailed(GetService<ILogger<App>>(), new InvalidOperationException("Missum beenden: " + stage, exception));
            await RecordAiLifecycleAsync(stage, "failed", exception);
        }
    }

    private async Task RecordAiLifecycleAsync(string stage, string result, Exception? exception = null)
    {
        try
        {
            var directory = Path.Combine(DataDirectory, "Diagnostics");
            Directory.CreateDirectory(directory);
            var entry = System.Text.Json.JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow, stage, result, error = exception?.Message,
            });
            await File.AppendAllTextAsync(Path.Combine(directory, "ai-lifecycle.jsonl"), entry + Environment.NewLine);
        }
        catch (Exception diagnosticException) when (diagnosticException is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(diagnosticException);
        }
    }

    private void ApplyHighContrastPalette(HighContrastPalette palette)
    {
        SetBrushColors(AccentBrushKeys, palette.Accent);
        SetBrushColors(AccentTextBrushKeys, palette.AccentForeground);
        SetBrushColor("MissumWindowBrush", palette.Background);
        SetBrushColor("MissumLayerBrush", palette.Background);
        SetBrushColor("MissumLayerStrongBrush", palette.Background);
        SetBrushColor("MissumInputBrush", palette.Background);
        SetBrushColor("MissumHoverBrush", palette.Background);
        SetBrushColor("MissumPressedBrush", palette.Background);
        SetBrushColor("MissumStrokeBrush", palette.Foreground);
        SetBrushColor("MissumMutedTextBrush", palette.Foreground);
        SetBrushColor("MissumTextBrush", palette.Foreground);
        ApplyCodePalette(light: false, highContrast: palette);
    }

    internal HighContrastPalette ResolveHighContrastPalette()
    {
        try
        {
            var settings = new UISettings();
            return CreateHighContrastPalette(
                settings.GetColorValue(UIColorType.Background),
                settings.GetColorValue(UIColorType.Foreground),
                settings.GetColorValue(UIColorType.Accent));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var logger = GetService<ILogger<App>>();
            if (logger.IsEnabled(LogLevel.Debug))
            {
                AppLog.SystemThemeEventUnavailable(
                    logger,
                    exception,
                    "UISettings high-contrast colors");
            }
            return CreateHighContrastPalette(
                Microsoft.UI.Colors.Black,
                Microsoft.UI.Colors.White,
                Microsoft.UI.Colors.Yellow);
        }
    }

    internal static HighContrastPalette CreateHighContrastPalette(
        Windows.UI.Color background,
        Windows.UI.Color foreground,
        Windows.UI.Color accent) =>
        new(background, foreground, accent, ContrastForeground(accent));

    private void ApplyBackgroundSurfaceColors(Windows.UI.Color background)
    {
        var isLight = _appliedTheme == AppTheme.Light
            || (_appliedTheme == AppTheme.System && _themeRoot?.ActualTheme == ElementTheme.Light);
        SetBrushColor("MissumTextBrush", isLight ? Windows.UI.Color.FromArgb(228, 0, 0, 0) : Microsoft.UI.Colors.White);
        SetBrushColor("MissumMutedTextBrush", isLight ? Windows.UI.Color.FromArgb(255, 0x51, 0x46, 0x5D) : Windows.UI.Color.FromArgb(255, 0x99, 0x99, 0x99));
        ApplyCodePalette(isLight);
        if (isLight)
        {
            SetBrushColor("MissumWindowBrush", MixBackground(0xF3F0F5, background, 0.07, 0xFF));
            SetBrushColor("MissumLayerBrush", MixBackground(0xFFFFFF, background, 0.04, 0xF8));
            SetBrushColor("MissumLayerStrongBrush", MixBackground(0xFAF8FC, background, 0.08, 0xFF));
            SetBrushColor("MissumInputBrush", MixBackground(0xFFFFFF, background, 0.09, 0xFF));
            SetBrushColor("MissumHoverBrush", MixBackground(0xF5F1F7, background, 0.13, 0xFF));
            SetBrushColor("MissumPressedBrush", MixBackground(0xEEE9F1, background, 0.18, 0xFF));
            SetBrushColor("MissumStrokeBrush", MixBackground(0x302A38, background, 0.22, 0x52));
            return;
        }

        SetBrushColor("MissumWindowBrush", MixBackground(0x202020, background, 0.08, 0xFF));
        SetBrushColor("MissumLayerBrush", MixBackground(0x202020, background, 0.12, 0xE6));
        SetBrushColor("MissumLayerStrongBrush", MixBackground(0x2B2B2B, background, 0.18, 0xF2));
        SetBrushColor("MissumInputBrush", MixBackground(0x333333, background, 0.20, 0xFF));
        SetBrushColor("MissumHoverBrush", MixBackground(0x303030, background, 0.23, 0xFF));
        SetBrushColor("MissumPressedBrush", MixBackground(0x383838, background, 0.28, 0xFF));
        SetBrushColor("MissumStrokeBrush", MixBackground(0xFFFFFF, background, 0.28, 0x42));
    }

    private void ApplyCodePalette(bool light, HighContrastPalette? highContrast = null)
    {
        SetBrushColor("MissumSuccessBrush", highContrast?.Foreground ?? (light ? Windows.UI.Color.FromArgb(255, 0x06, 0x65, 0x40) : Windows.UI.Color.FromArgb(255, 0x42, 0xD3, 0x92)));
        SetBrushColor("MissumWarningBrush", highContrast?.Foreground ?? (light ? Windows.UI.Color.FromArgb(255, 0x87, 0x48, 0) : Windows.UI.Color.FromArgb(255, 0xF4, 0xB8, 0x60)));
        SetBrushColor("MissumDangerBrush", highContrast?.Foreground ?? (light ? Windows.UI.Color.FromArgb(255, 0xB3, 0x1E, 0x3A) : Windows.UI.Color.FromArgb(255, 0xFF, 0x66, 0x7A)));
        foreach (var (name, dark, bright) in new (string, uint, uint)[] {
            ("Keyword", 0xC586C0, 0x800080), ("Type", 0x4EC9B0, 0x267F99), ("String", 0xCE9178, 0xA31515),
            ("Number", 0xB5CEA8, 0x006B43), ("Comment", 0x6A9955, 0x006400), ("Function", 0xDCDCAA, 0x795E26),
            ("Property", 0x9CDCFE, 0x001080), ("Tag", 0x569CD6, 0x800000) })
        {
            _ = SetColor(light ? bright : dark, out var color);
            SetBrushColor("MissumCode" + name + "Brush", highContrast?.Foreground ?? color);
        }
        _ = SetColor(light ? 0xE7F3EAu : 0x133325u, out var added);
        _ = SetColor(light ? 0xFCEAE9u : 0x3B1D1Au, out var removed);
        SetBrushColor("MissumDiffAddedBackgroundBrush", highContrast?.Background ?? added);
        SetBrushColor("MissumDiffRemovedBackgroundBrush", highContrast?.Background ?? removed);
    }

    private void UpdateReadableAccentBrush()
    {
        if (IsHighContrastEnabled())
        {
            var palette = ResolveHighContrastPalette();
            SetBrushColor("MissumAccentReadableBrush", ReadableAccentColor(palette.Accent, [palette.Background]));
            return;
        }
        _ = TryParsePaletteColor(AccentColor, out var accent);
        _ = TryParsePaletteColor(BackgroundColor, out var background);
        var light = _appliedTheme == AppTheme.Light || (_appliedTheme == AppTheme.System && _themeRoot?.ActualTheme == ElementTheme.Light);
        SetBrushColor("MissumAccentReadableBrush", light ? ReadableAccentColor(accent, LightTextSurfaces(background)) : accent);
    }

    private static Windows.UI.Color[] LightTextSurfaces(Windows.UI.Color background) =>
    [
        MixBackground(0xF3F0F5, background, .07, 255), MixBackground(0xFFFFFF, background, .04, 255),
        MixBackground(0xFAF8FC, background, .08, 255), MixBackground(0xFFFFFF, background, .09, 255),
        MixBackground(0xF5F1F7, background, .13, 255), MixBackground(0xEEE9F1, background, .18, 255),
    ];

    internal static Windows.UI.Color ReadableAccentColor(Windows.UI.Color accent, Windows.UI.Color[] surfaces)
    {
        if (surfaces.Length == 0 || surfaces.All(surface => TextContrastRatio(accent, surface) >= 4.5)) return accent;
        var black = Microsoft.UI.Colors.Black; var white = Microsoft.UI.Colors.White;
        var destination = surfaces.Min(surface => TextContrastRatio(black, surface)) >= surfaces.Min(surface => TextContrastRatio(white, surface)) ? black : white;
        double low = 0, high = 1;
        Windows.UI.Color Blend(double amount) => Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(accent.R + (destination.R - accent.R) * amount),
            (byte)Math.Round(accent.G + (destination.G - accent.G) * amount),
            (byte)Math.Round(accent.B + (destination.B - accent.B) * amount));
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var middle = (low + high) / 2;
            if (surfaces.All(surface => TextContrastRatio(Blend(middle), surface) >= 4.5)) high = middle;
            else low = middle;
        }
        return Blend(high);
    }

    internal static double TextContrastRatio(Windows.UI.Color foreground, Windows.UI.Color background)
    {
        static double Linear(double channel) => channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        static double Luminance(Windows.UI.Color color) => .2126 * Linear(color.R / 255d) + .7152 * Linear(color.G / 255d) + .0722 * Linear(color.B / 255d);
        var alpha = foreground.A / 255d;
        var composited = Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
        var first = Luminance(composited); var second = Luminance(background);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private void SetBrushColors(IEnumerable<string> keys, Windows.UI.Color color)
    {
        foreach (var key in keys)
        {
            SetBrushColor(key, color);
        }
    }

    private void SetBrushColor(string key, Windows.UI.Color color)
    {
        foreach (var dictionary in EnumerateResourceDictionaries(Resources))
        {
            if (dictionary.ContainsKey(key)
                && dictionary[key] is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }
    }

    private static Windows.UI.Color MixBackground(
        uint baseRgb,
        Windows.UI.Color background,
        double backgroundWeight,
        byte alpha)
    {
        var baseColor = Windows.UI.Color.FromArgb(
            alpha,
            (byte)(baseRgb >> 16),
            (byte)(baseRgb >> 8),
            (byte)baseRgb);
        var baseWeight = 1d - backgroundWeight;
        return Windows.UI.Color.FromArgb(
            alpha,
            (byte)Math.Round((baseColor.R * baseWeight) + (background.R * backgroundWeight)),
            (byte)Math.Round((baseColor.G * baseWeight) + (background.G * backgroundWeight)),
            (byte)Math.Round((baseColor.B * baseWeight) + (background.B * backgroundWeight)));
    }

    internal static Windows.UI.Color ContrastForeground(Windows.UI.Color background)
    {
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B);
        var contrastWithBlack = (luminance + 0.05) / 0.05;
        var contrastWithWhite = 1.05 / (luminance + 0.05);
        return contrastWithBlack >= contrastWithWhite ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    }

    private bool IsHighContrastEnabled()
    {
        try
        {
            return _accessibilitySettings?.HighContrast ?? new AccessibilitySettings().HighContrast;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParsePaletteColor(string? value, out Windows.UI.Color color)
    {
        color = default;
        return value is { Length: 7 }
            && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            && SetColor(rgb, out color);
    }

    private static bool SetColor(uint rgb, out Windows.UI.Color color)
    {
        color = Windows.UI.Color.FromArgb(
            255,
            (byte)(rgb >> 16),
            (byte)(rgb >> 8),
            (byte)rgb);
        return true;
    }

    internal readonly record struct HighContrastPalette(
        Windows.UI.Color Background,
        Windows.UI.Color Foreground,
        Windows.UI.Color Accent,
        Windows.UI.Color AccentForeground);
}


