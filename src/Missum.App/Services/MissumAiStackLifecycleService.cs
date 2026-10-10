using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.App.Services;

/// <summary>
/// Starts and stops the installed local Missum stack without needing repository
/// files beside the portable executable. Container labels, the gateway data mount
/// and its published port establish ownership; names alone never establish it.
/// </summary>
public sealed class MissumAiStackLifecycleService : IDisposable
{
    public const string ComposeProject = "missum-ai-stack";
    public const string ProfileLabel = "com.missum.runtime.profile";
    public const string StackDataRootLabel = "com.missum.runtime.stack-data-root";
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";
    private static readonly Regex ContainerIdPattern = new("^[a-fA-F0-9]{12,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly AssistantRuntimeProfile _profile;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<DockerCommandResult>> _run;
    private readonly Func<Uri, bool> _isLocalGateway;
    private readonly Func<bool> _disabled;
    private readonly Func<string, string?> _environment;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<CancellationToken, Task> _startDesktop;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextProbe;
    private Exception? _lastFailure;
    private int _stopping;
    private bool _stopped;

    public MissumAiStackLifecycleService(AssistantRuntimeProfile profile)
        : this(profile, RunDockerAsync, NativeModelRuntimeService.IsLocalGateway,
            static () => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY"))) { }

    internal MissumAiStackLifecycleService(AssistantRuntimeProfile profile,
        Func<IReadOnlyList<string>, CancellationToken, Task<DockerCommandResult>> run,
        Func<Uri, bool>? isLocalGateway = null, Func<bool>? disabled = null,
        Func<string, string?>? environment = null, Func<DateTimeOffset>? now = null,
        Func<CancellationToken, Task>? startDesktop = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _profile = profile;
        _run = run;
        _isLocalGateway = isLocalGateway ?? NativeModelRuntimeService.IsLocalGateway;
        _disabled = disabled ?? (() => false);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _startDesktop = startDesktop ?? StartDockerDesktopAsync;
        _delay = delay ?? Task.Delay;
    }

    public async Task EnsureStartedAsync(Uri gateway, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stopping) != 0 || !CanManage(gateway)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            if (_now() < _nextProbe)
            {
                if (_lastFailure is not null) throw new InvalidOperationException(_lastFailure.Message, _lastFailure);
                return;
            }
            var endpoint = await ResolveDockerEndpointAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDockerDaemonAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var containers = await ReadContainersAsync(endpoint, includeRunners: false, cancellationToken).ConfigureAwait(false);
            ValidateInstalledStack(containers, gateway, requireInstalled: true);
            // Worker models may take time to warm. Gateway readiness is checked
            // by the existing connection monitor, not replaced with a CLI delay.
            foreach (var group in containers.Where(static container => !container.Running)
                         .OrderBy(static container => ServiceOrder(container.Service))
                         .ThenBy(static container => container.Id, StringComparer.Ordinal)
                         .GroupBy(static container => ServiceOrder(container.Service)))
            {
                var result = await DockerAsync(endpoint, ["start", .. group.Select(static container => container.Id)], cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "Die Missum-Docker-Dienste konnten nicht gestartet werden");
            }
            _lastFailure = null;
            _nextProbe = _now().AddSeconds(5);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _lastFailure = exception;
            _nextProbe = _now().AddSeconds(10);
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Starts only the local gateway and proxy for stored artifacts, without warming AI workers.</summary>
    public async Task EnsureGatewayStartedAsync(Uri gateway, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stopping) != 0 || !CanManage(gateway)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            var endpoint = await ResolveDockerEndpointAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDockerDaemonAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var containers = await ReadContainersAsync(endpoint, includeRunners: false, cancellationToken).ConfigureAwait(false);
            ValidateInstalledStack(containers, gateway, requireInstalled: true);
            foreach (var container in containers.Where(static container => !container.Running
                         && container.Service is ("gateway" or "caddy"))
                         .OrderBy(static container => ServiceOrder(container.Service)))
            {
                var result = await DockerAsync(endpoint, ["start", container.Id], cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "Der Missum-Gateway konnte für die gespeicherte Datei nicht gestartet werden");
            }
            // This partial start must not populate the full-stack probe cache:
            // a later AI request still needs to inspect and start its workers.
            // The caller separately waits for gateway HTTP liveness.
        }
        finally { _gate.Release(); }
    }

    public void BeginShutdown() => Interlocked.Exchange(ref _stopping, 1);

    public async Task StopAsync(Uri gateway, CancellationToken cancellationToken = default)
    {
        BeginShutdown();
        if (!CanManage(gateway)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            var endpoint = await ResolveDockerEndpointAsync(cancellationToken).ConfigureAwait(false);
            var containers = await ReadContainersAsync(endpoint, includeRunners: true, cancellationToken).ConfigureAwait(false);
            ValidateInstalledStack(containers, gateway, requireInstalled: false);
            var running = containers.Where(static container => container.Running)
                .OrderByDescending(static container => ServiceOrder(container.Service))
                .ThenBy(static container => container.Id, StringComparer.Ordinal).ToArray();
            if (running.Length > 0)
            {
                var result = await DockerAsync(endpoint, ["stop", "--time", "15", .. running.Select(static container => container.Id)], cancellationToken).ConfigureAwait(false);
                // A --rm research container can finish between inspection and
                // stop. Judge the remaining running set rather than that race.
                var remaining = await ReadContainersAsync(endpoint, includeRunners: true, cancellationToken).ConfigureAwait(false);
                ValidateInstalledStack(remaining, gateway, requireInstalled: false);
                if (remaining.Any(static container => container.Running))
                {
                    EnsureSuccess(result, "Die Missum-Docker-Dienste konnten nicht vollständig beendet werden");
                    throw new InvalidOperationException("Nach dem Beenden laufen noch Missum-Docker-Dienste. Prüfe Docker Desktop und die Missum-Dienstprotokolle.");
                }
            }
            _stopped = true;
        }
        finally { _gate.Release(); }
    }

    private bool CanManage(Uri gateway) => !_disabled()
        && string.Equals(_profile.Name, "stable", StringComparison.Ordinal)
        && _isLocalGateway(gateway);

    private async Task<string> ResolveDockerEndpointAsync(CancellationToken cancellationToken)
    {
        var requestedContext = _environment("DOCKER_CONTEXT");
        var endpoint = string.IsNullOrWhiteSpace(requestedContext) ? _environment("DOCKER_HOST") : null;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            string[] arguments = string.IsNullOrWhiteSpace(requestedContext)
                ? ["context", "inspect", "--format", "{{json .Endpoints.docker.Host}}"]
                : ["context", "inspect", requestedContext, "--format", "{{json .Endpoints.docker.Host}}"];
            var result = await _run(arguments, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "Docker ist nicht verfügbar; installiere oder starte Docker Desktop");
            try { endpoint = JsonSerializer.Deserialize<string>(result.StandardOutput.Trim()); }
            catch (JsonException exception) { throw new InvalidOperationException("Der lokale Docker-Endpunkt konnte nicht bestimmt werden.", exception); }
        }
        if (string.IsNullOrWhiteSpace(endpoint) || !IsLocalDockerEndpoint(endpoint))
            throw new InvalidOperationException("Missum steuert ausschließlich die lokale Docker-Engine. Wähle in Docker Desktop den lokalen Kontext.");
        return endpoint;
    }

    internal static bool IsLocalDockerEndpoint(string endpoint)
    {
        if (endpoint.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("unix:///", StringComparison.Ordinal)) return true;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme is "tcp" or "http" or "https" && uri.IsLoopback;
    }

    private async Task EnsureDockerDaemonAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await DockerAsync(endpoint, ["info", "--format", "{{.ServerVersion}}"], startup.Token).ConfigureAwait(false);
            if (result.ExitCode == 0) return;
            await _startDesktop(startup.Token).ConfigureAwait(false);
            do
            {
                await _delay(TimeSpan.FromSeconds(2), startup.Token).ConfigureAwait(false);
                result = await DockerAsync(endpoint, ["info", "--format", "{{.ServerVersion}}"], startup.Token).ConfigureAwait(false);
            } while (result.ExitCode != 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("Docker Desktop wurde gestartet, die lokale Engine ist nach 60 Sekunden aber noch nicht bereit. Prüfe Docker Desktop."); }
    }

    private static Task StartDockerDesktopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Die lokale Docker-Engine ist nicht erreichbar und Docker Desktop wurde nicht gefunden. Installiere oder starte Docker Desktop.");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        if (process is null) throw new InvalidOperationException("Docker Desktop konnte nicht gestartet werden.");
        return Task.CompletedTask;
    }

    private async Task<List<StackContainer>> ReadContainersAsync(string endpoint, bool includeRunners, CancellationToken cancellationToken)
    {
        var ids = await ReadContainerIdsAsync(endpoint, ["label=" + ComposeProjectLabel + "=" + ComposeProject], cancellationToken).ConfigureAwait(false);
        if (includeRunners)
            ids.UnionWith(await ReadContainerIdsAsync(endpoint,
                ["label=" + ProfileLabel + "=" + _profile.Name,
                 "label=" + StackDataRootLabel + "=" + Path.GetFullPath(_profile.StackDataRoot)], cancellationToken).ConfigureAwait(false));
        if (ids.Count == 0) return [];
        var result = await DockerAsync(endpoint, ["inspect", .. ids.Order(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
        // docker inspect still returns its valid objects when a transient --rm
        // container disappears in this interval. Keep those objects inspectable.
        if (result.ExitCode != 0 && !(includeRunners
            && (result.StandardError.Contains("No such object", StringComparison.OrdinalIgnoreCase)
                || result.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            && result.StandardOutput.TrimStart().StartsWith('[')))
            EnsureSuccess(result, "Die Missum-Docker-Dienste konnten nicht überprüft werden");
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var containers = new List<StackContainer>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("Id").GetString() ?? string.Empty;
                if (!ContainerIdPattern.IsMatch(id)) throw new InvalidDataException("Docker lieferte eine ungültige Containerkennung.");
                var labels = item.GetProperty("Config").GetProperty("Labels");
                var belongsToCompose = string.Equals(ReadString(labels, ComposeProjectLabel), ComposeProject, StringComparison.Ordinal);
                var belongsToRunner = string.Equals(ReadString(labels, ProfileLabel), _profile.Name, StringComparison.Ordinal)
                    && PathsMatch(ReadString(labels, StackDataRootLabel), _profile.StackDataRoot);
                if (!belongsToCompose && (!includeRunners || !belongsToRunner)) continue;
                containers.Add(new(id, belongsToCompose ? ReadString(labels, ComposeServiceLabel) ?? string.Empty : "research-runner",
                    item.GetProperty("State").GetProperty("Running").GetBoolean(), belongsToCompose, item.Clone()));
            }
            return containers;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidOperationException("Docker lieferte keine lesbaren Missum-Dienstinformationen.", exception); }
    }

    private async Task<HashSet<string>> ReadContainerIdsAsync(string endpoint, IReadOnlyList<string> filters, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "ps", "--all", "--format", "{{.ID}}" };
        foreach (var filter in filters) { arguments.Add("--filter"); arguments.Add(filter); }
        var result = await DockerAsync(endpoint, arguments, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "Die lokale Docker-Engine ist nicht erreichbar; starte Docker Desktop");
        var ids = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Any(id => !ContainerIdPattern.IsMatch(id))) throw new InvalidDataException("Docker lieferte eine ungültige Containerkennung.");
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private void ValidateInstalledStack(List<StackContainer> containers, Uri gateway, bool requireInstalled)
    {
        var compose = containers.Where(static container => container.IsCompose).ToArray();
        if (compose.Length == 0)
        {
            if (requireInstalled) throw new InvalidOperationException("Der lokale Missum-AI-Stack ist noch nicht installiert. Führe windows/deploy-ai-stack.ps1 aus; anschließend startet Missum die vorhandenen Dienste automatisch.");
            return;
        }
        var gateways = compose.Where(static container => container.Service == "gateway").ToArray();
        var proxies = compose.Where(static container => container.Service == "caddy").ToArray();
        if (gateways.Length != 1 || proxies.Length != 1)
            throw new InvalidOperationException("Der Missum-AI-Stack ist unvollständig oder mehrdeutig; Gateway und Proxy müssen jeweils genau einmal installiert sein.");
        var expectedData = Path.Combine(_profile.StackDataRoot, "data");
        if (!gateways[0].Details.GetProperty("Mounts").EnumerateArray().Any(mount =>
                ReadString(mount, "Type") == "bind" && ReadString(mount, "Destination") == "/data"
                && PathsMatch(ReadString(mount, "Source"), expectedData)))
            throw new InvalidOperationException("Die Docker-Dienste gehören zu einem anderen Missum-Datenverzeichnis und werden nicht verändert.");
        var bindings = proxies[0].Details.GetProperty("HostConfig").GetProperty("PortBindings");
        var port = gateway.Port.ToString(CultureInfo.InvariantCulture);
        if (!bindings.TryGetProperty("8080/tcp", out var ports) || ports.ValueKind != JsonValueKind.Array
            || !ports.EnumerateArray().Any(binding => ReadString(binding, "HostPort") == port))
            throw new InvalidOperationException("Der installierte Missum-Gateway-Port passt nicht zur gewählten Verbindung; die Docker-Dienste werden nicht verändert.");
    }

    internal static bool PathsMatch(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var normalized = actual.Replace('\\', '/').TrimEnd('/');
        // Docker Desktop may report the same Windows bind as its Linux host path.
        const string desktopPrefix = "/run/desktop/mnt/host/";
        if (normalized.StartsWith(desktopPrefix, StringComparison.Ordinal) && normalized.Length > desktopPrefix.Length + 1
            && char.IsAsciiLetter(normalized[desktopPrefix.Length]) && normalized[desktopPrefix.Length + 1] == '/')
            normalized = normalized[desktopPrefix.Length] + ":" + normalized[(desktopPrefix.Length + 1)..];
        return string.Equals(normalized, Path.GetFullPath(expected).Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int ServiceOrder(string service) => service switch { "caddy" => 2, "gateway" => 1, _ => 0 };
    private Task<DockerCommandResult> DockerAsync(string endpoint, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _run(["--host", endpoint, .. arguments], cancellationToken);

    private static void EnsureSuccess(DockerCommandResult result, string message)
    {
        if (result.ExitCode == 0) return;
        var detail = result.StandardError.Trim();
        if (detail.Length > 1200) detail = detail[..1200];
        throw new InvalidOperationException(message + (detail.Length == 0 ? "." : ": " + detail));
    }

    private static async Task<DockerCommandResult> RunDockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Docker konnte nicht gestartet werden.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new InvalidOperationException("Docker hat innerhalb von 60 Sekunden nicht geantwortet. Prüfe Docker Desktop.");
            }
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (Win32Exception exception)
        { throw new InvalidOperationException("Docker ist nicht verfügbar. Installiere Docker Desktop und den lokalen Missum-AI-Stack.", exception); }
    }

    public void Dispose() => _gate.Dispose();

    internal sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record StackContainer(string Id, string Service, bool Running, bool IsCompose, JsonElement Details);
}
