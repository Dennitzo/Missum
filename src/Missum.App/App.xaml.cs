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
                services.AddSingleton<Missum.Core.Research.IResearchSandboxService, ResearchSandboxService>();
                services.AddSingleton<AssistantSessionActivationService>();
                services.AddSingleton<NativeModelRuntimeService>();
                services.AddSingleton<MissumAiConnectionService>();
                services.AddSingleton<ModelCapabilityRegistry>();
                services.AddSingleton<SystemAudioCaptionService>();
                services.AddSingleton<MicrophoneTranscriptionService>();
                services.AddSingleton<SystemAudioAnalysisCaptureService>();
                services.AddSingleton<DesktopScreenshotService>();
                services.AddSingleton<ScreenClipCaptureService>();
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
        }

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
        }

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
            await GetService<MissumAiAssistantService>().StopPersistedRunsAtStartupAsync();
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
    }

    private void SubscribeHighContrastChanges()
    {
        try
        {
            _accessibilitySettings = new AccessibilitySettings();
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
            _highContrastEventsSubscribed = true;
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
            _ = dispatcher.TryEnqueue(ApplyPaletteColors);
            return;
        }

        ApplyPaletteColors();
    }

    private async Task PrepareShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            var availabilityCancellation = Interlocked.Exchange(ref _aiAvailabilityCancellation, null);
            var availabilityMonitor = Interlocked.Exchange(ref _aiAvailabilityMonitor, null);
            availabilityCancellation?.Cancel();
            if (availabilityMonitor is not null)
            {
                try
                {
                    await availabilityMonitor;
                }
                catch (OperationCanceledException)
                {
                    // The availability monitor is expected to stop during shutdown.
                }
            }

            availabilityCancellation?.Dispose();
            var nativeRuntime = GetService<NativeModelRuntimeService>();
            nativeRuntime.BeginShutdown();
            // Missum shares the model runtime with Missum and other local clients.
            // Closing a window must not terminate that shared service.
            await GetService<MissumAiAssistantService>().CancelCurrentAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(4));
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

        ThemeChanged?.Invoke(this, EventArgs.Empty);
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


