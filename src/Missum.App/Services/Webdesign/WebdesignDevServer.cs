using System.Diagnostics;
using System.Text.Json;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// A project's own dev server (Vite, Next, Astro … or a configured command), run as a supervised
/// background process. coding.command cannot do this: its job object ends every child when the
/// tool call returns. The port is chosen by Windows and passed with a strict-port flag; when the
/// server still reports a collision, it is restarted on a new port.
/// </summary>
internal sealed class WebdesignDevServer : IAsyncDisposable
{
    private const int MaximumLogLines = 400;
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Queue<string> _log = new();
    private readonly Lock _logLock = new();
    private Process? _process;
    private WebdesignProcessJob? _job;

    private WebdesignDevServer(string command, string displayCommand)
    {
        Command = command;
        DisplayCommand = displayCommand;
    }

    public int Port { get; private set; }
    public string Command { get; }
    public string DisplayCommand { get; }
    public bool HasExited => _process is null || _process.HasExited;

    public IReadOnlyList<string> Log()
    {
        lock (_logLock) return _log.ToArray();
    }

    /// <summary>Detects a dev script in package.json and builds the command for it, or null.</summary>
    internal static string? DetectCommand(string workspace, out string framework)
    {
        framework = "";
        var packagePath = Path.Combine(workspace, "package.json");
        if (!File.Exists(packagePath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) return null;
            var script = scripts.TryGetProperty("dev", out _) ? "dev" : scripts.TryGetProperty("start", out _) ? "start" : null;
            if (script is null) return null;
            var definition = scripts.GetProperty(script).GetString() ?? "";
            framework = definition.Contains("vite", StringComparison.OrdinalIgnoreCase) ? "vite"
                : definition.Contains("next", StringComparison.OrdinalIgnoreCase) ? "next"
                : definition.Contains("astro", StringComparison.OrdinalIgnoreCase) ? "astro"
                : definition.Contains("nuxt", StringComparison.OrdinalIgnoreCase) ? "nuxt"
                : definition.Contains("svelte-kit", StringComparison.OrdinalIgnoreCase) ? "vite"
                : definition.Contains("parcel", StringComparison.OrdinalIgnoreCase) ? "parcel"
                : "node";
            // Frameworks differ in how they take port and host; unknown scripts receive PORT/HOST.
            return framework switch
            {
                "vite" => $"npm run {script} -- --port {{port}} --strictPort --host {{host}}",
                "next" => $"npm run {script} -- -p {{port}} -H {{host}}",
                "astro" => $"npm run {script} -- --port {{port}} --host {{host}}",
                "nuxt" => $"npm run {script} -- --port {{port}} --host {{host}}",
                "parcel" => $"npm run {script} -- --port {{port}} --host {{host}}",
                _ => $"npm run {script}",
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException) { return null; }
    }

    public static async Task<WebdesignDevServer> StartAsync(string workspace, string command, bool lan,
        IReadOnlySet<int> taken, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var port = WebdesignPorts.ReserveForChildProcess(taken);
            var host = lan ? "0.0.0.0" : "127.0.0.1";
            var resolved = command.Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{host}", host, StringComparison.Ordinal);
            var server = new WebdesignDevServer(resolved, command) { Port = port };
            try
            {
                server.Launch(workspace, port, host);
                if (await server.WaitUntilReadyAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false)) return server;
                last = new InvalidOperationException(server.FailureReason());
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { last = exception; }
            var collided = server.Log().Any(line => line.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase)
                || line.Contains("already in use", StringComparison.OrdinalIgnoreCase));
            await server.DisposeAsync().ConfigureAwait(false);
            if (!collided) break;
        }
        throw last ?? new InvalidOperationException("Der Projekt-Server ist nicht gestartet.");
    }

    private void Launch(string workspace, int port, string host)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(Command);
        start.Environment["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["HOST"] = host;
        start.Environment["BROWSER"] = "none";
        start.Environment["FORCE_COLOR"] = "0";
        start.Environment["NO_COLOR"] = "1";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, line) => Append(line.Data);
        process.ErrorDataReceived += (_, line) => Append(line.Data);
        if (!process.Start()) throw new InvalidOperationException("Der Projekt-Server konnte nicht gestartet werden.");
        _process = process;
        try { _job = WebdesignProcessJob.CreateAndAssign(process); }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Append($"> {Command}");
    }

    private async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited) return false;
            try
            {
                using var response = await Probe.GetAsync($"http://127.0.0.1:{Port}/", cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private string FailureReason()
    {
        var tail = string.Join(" | ", Log().TakeLast(4));
        if (Log().Any(line => line.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
                || line.Contains("wird nicht als interner", StringComparison.OrdinalIgnoreCase)))
            return "npm bzw. Node.js wurde nicht gefunden. Installiere Node.js (nodejs.org) oder stelle den Server in den Hosting-Optionen auf „statisch“. " + tail;
        if (Log().Any(line => line.Contains("Cannot find module", StringComparison.OrdinalIgnoreCase)
                || line.Contains("command not found", StringComparison.OrdinalIgnoreCase)))
            return "Abhängigkeiten fehlen; führe im Projekt einmal „npm install“ aus. " + tail;
        return HasExited
            ? $"Der Projekt-Server hat sich beendet (Exitcode {_process?.ExitCode}). {tail}"
            : $"Der Projekt-Server antwortet nicht auf Port {Port}. {tail}";
    }

    private void Append(string? line)
    {
        if (line is null) return;
        if (line.Length > 600) line = line[..600] + " …";
        lock (_logLock)
        {
            _log.Enqueue(line);
            while (_log.Count > MaximumLogLines) _log.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            process.Dispose();
        }
        Interlocked.Exchange(ref _job, null)?.Dispose();
    }
}
