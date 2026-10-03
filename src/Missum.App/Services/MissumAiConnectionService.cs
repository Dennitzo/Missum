using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Microsoft.Extensions.Logging;

namespace Missum.App.Services;

public sealed record MissumAiConnectionStatus(
    bool IsReachable,
    bool IsReady,
    string Message,
    CapabilitySnapshot? Capabilities = null,
    HealthSnapshot? Health = null);

public sealed class MissumAiConnectionService(
    SettingsCoordinator settings,
    ILogger<MissumAiConnectionService> logger,
    NativeModelRuntimeService? nativeRuntime = null,
    MissumAiStackLifecycleService? stackLifecycle = null) : IDisposable
{
    public const string DefaultServerUrl = "http://192.168.0.67:8080";
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly Action<ILogger, string, Exception?> ConnectionFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(5200, nameof(ConnectionFailed)),
        "Missum Docker gateway connection check failed ({FailureKind}).");
    private readonly string _clientId = $"missum-{Guid.NewGuid():N}";
    private Func<HttpMessageHandler>? _httpHandlerFactory;
    private TimeSpan _probeTimeout = DefaultProbeTimeout;
    private int _disposed;

    public string? NativeRuntimeError { get; private set; }

    internal MissumAiConnectionService(
        SettingsCoordinator settings,
        ILogger<MissumAiConnectionService> logger,
        Func<HttpMessageHandler> httpHandlerFactory,
        TimeSpan? probeTimeout = null,
        NativeModelRuntimeService? nativeRuntime = null)
        : this(settings, logger, nativeRuntime)
    {
        _httpHandlerFactory = httpHandlerFactory ?? throw new ArgumentNullException(nameof(httpHandlerFactory));
        if (probeTimeout is { } timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(probeTimeout));
            _probeTimeout = timeout;
        }
    }

    public Task<MissumAiClient> CreateClientAsync(CancellationToken cancellationToken = default) =>
        CreateClientAsync(baseAddressOverride: null, ensureProfileNativeRuntime: true, cancellationToken);

    internal async Task<MissumAiClient> CreateClientAsync(
        Uri? baseAddressOverride,
        bool ensureProfileNativeRuntime,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var addressText = baseAddressOverride?.AbsoluteUri ?? settings.Current.MissumAiServerUrl;
        if (!Uri.TryCreate(addressText.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Die Docker-Gatewayadresse ist ungültig.");
        }

        if (ensureProfileNativeRuntime && stackLifecycle is not null)
        {
            try { await stackLifecycle.EnsureStartedAsync(baseAddress, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ConnectionFailed(logger, "Docker startup", exception);
            }
        }
        if (ensureProfileNativeRuntime && nativeRuntime is not null)
        {
            try
            {
                await nativeRuntime.EnsureStartedAsync(baseAddress, cancellationToken).ConfigureAwait(false);
                NativeRuntimeError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Other gateway services remain usable even if a local language-model dependency is missing.
                NativeRuntimeError = exception.Message;
            }
        }

        HttpMessageHandler handler = _httpHandlerFactory?.Invoke()
            ?? new HttpClientHandler { UseProxy = false };
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Missum/1.0");
        return new MissumAiClient(httpClient, _clientId, ownsHttpClient: true);
    }

    public async Task<MissumAiConnectionStatus> TestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCancellation.CancelAfter(_probeTimeout);
            var probeToken = probeCancellation.Token;
            var live = await client.GetLiveHealthAsync(probeToken).ConfigureAwait(false);
            if (!string.Equals(live.ProtocolVersion, settings.Current.MissumAiProtocolVersion, StringComparison.Ordinal))
            {
                return new(false, false,
                    $"Protokollabweichung: Gateway {live.ProtocolVersion}, Missum {settings.Current.MissumAiProtocolVersion}.",
                    Health: live);
            }

            var capabilities = await client.GetCapabilitiesAsync(probeToken).ConfigureAwait(false);
            var health = await client.GetReadyHealthAsync(probeToken).ConfigureAwait(false);
            return health.Status switch
            {
                "ready" => new(true, true,
                    $"Verbunden · Modell bereit · {capabilities.ServerTools.Count} Servertools",
                    capabilities, health),
                "modelLoading" => new(true, true,
                    $"Verbunden · Modell wird geladen · {health.Reason}", capabilities, health),
                "modelNotLoaded" => new(true, true,
                    "Verbunden · Modell wird beim ersten AI-Lauf geladen", capabilities, health),
                _ => new(true, false,
                    $"Verbunden · Eingeschränkt · {NativeRuntimeError ?? health.Reason ?? "Dienstfehler"}", capabilities, health),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ConnectionFailed(logger, exception.GetType().Name, exception);
            return new(false, false, FriendlyConnectionError(exception));
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private static string FriendlyConnectionError(Exception exception) => exception switch
    {
        MissumAiApiException apiException => apiException.Problem?.Detail ?? apiException.Message,
        HttpRequestException => "Der Missum Docker-Gatewaystack ist nicht erreichbar.",
        TaskCanceledException => "Die Verbindung zum Docker-Gateway hat das Zeitlimit überschritten.",
        OperationCanceledException => "Die Verbindung zum Docker-Gateway hat das Zeitlimit überschritten.",
        _ => exception.Message,
    };

}
