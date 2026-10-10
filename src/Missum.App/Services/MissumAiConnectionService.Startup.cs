using Missum.Ai.Client;
using System.Net;
using System.Net.Sockets;

namespace Missum.App.Services;

public sealed partial class MissumAiConnectionService
{
    /// <summary>Original files need a live HTTP gateway, without starting a model or media worker.</summary>
    internal async Task<MissumAiClient> CreateArtifactClientAsync(CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(null, ensureProfileNativeRuntime: false, cancellationToken).ConfigureAwait(false);
        try
        {
            var address = client.BaseAddress ?? throw new InvalidOperationException("Die Gatewayadresse fehlt.");
            if (stackLifecycle is not null)
                await stackLifecycle.EnsureGatewayStartedAsync(address, cancellationToken).ConfigureAwait(false);
            await WaitForGatewayAsync(client, settings.Current.MissumAiProtocolVersion,
                NativeModelRuntimeService.IsLocalGateway(address), TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal Task WaitForGatewayAsync(MissumAiClient client, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var local = Uri.TryCreate(settings.Current.MissumAiServerUrl, UriKind.Absolute, out var address)
            && NativeModelRuntimeService.IsLocalGateway(address);
        return WaitForGatewayAsync(client, settings.Current.MissumAiProtocolVersion, local,
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1), cancellationToken);
    }

    internal static async Task WaitForGatewayAsync(
        MissumAiClient client,
        string expectedProtocol,
        bool retryLocalStartup,
        TimeSpan startupTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(startupTimeout);
        Exception? lastStartupError = null;
        try
        {
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                try
                {
                    // An unloaded/loading model must not block admission: the run itself loads it.
                    // Only the HTTP gateway needs to be alive before reading the saved run.
                    var live = await client.GetLiveHealthAsync(startup.Token).ConfigureAwait(false);
                    if (!string.Equals(live.ProtocolVersion, expectedProtocol, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Protokollabweichung: Gateway {live.ProtocolVersion}, Missum {expectedProtocol}.");
                    return;
                }
                catch (HttpRequestException exception) when (retryLocalStartup && IsGatewayStartupFailure(exception))
                {
                    lastStartupError = exception;
                }
                await Task.Delay(retryDelay, startup.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && startup.IsCancellationRequested)
        {
            // This bounds service startup only; there is no time limit on the resumed AI run.
            throw new TimeoutException(
                "Der Missum-Gateway wurde beim Start nicht rechtzeitig erreichbar. Bitte den Dienststatus prüfen.",
                lastStartupError);
        }
    }

    private static bool IsGatewayStartupFailure(HttpRequestException exception) =>
        exception.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
        || (exception.StatusCode is null
            && (exception.HttpRequestError == HttpRequestError.ConnectionError
                || exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }));
}
