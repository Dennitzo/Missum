using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Missum.App.Services.AiStack;

public enum AiStackSetupStage { Checks, Prepare, Models, Build, Start, Done }

/// <summary>One step of the setup: a stage, a user-facing line and, where measurable, the fraction done.</summary>
public sealed record AiStackSetupProgress(AiStackSetupStage Stage, string Message, double? Fraction = null);

/// <summary>A requirement of the local AI stack and whether this PC meets it.</summary>
public sealed record AiStackRequirement(string Name, bool Met, string Detail, bool Blocking = true);

public sealed record AiStackRequirementReport(IReadOnlyList<AiStackRequirement> Items, int GpuCount, long FreeBytes)
{
    public bool CanInstall => Items.All(static item => item.Met || !item.Blocking);
}

/// <summary>What the setup leaves behind, so updates know which components to rebuild.</summary>
public sealed record AiStackSetupState(string ImageVersion, AiStackComponents Components, DateTimeOffset InstalledAt);

/// <summary>
/// Installs the local Missum AI stack without a repository checkout: the installer places the
/// stack sources (compose file, Dockerfiles, worker and gateway sources) under
/// <c>%LOCALAPPDATA%\Missum\AiStack\&lt;version&gt;</c>, and this service builds the images on this PC
/// (Docker pulls the pinned base images itself), downloads the pinned worker models and starts the
/// services under the same compose project as <c>windows/deploy-ai-stack.ps1</c>, so
/// <see cref="MissumAiStackLifecycleService"/> manages them afterwards exactly as before.
/// </summary>
public sealed class AiStackSetupService : IDisposable
{
    public const string StackRootVariable = "MISSUM_AI_STACK_ROOT";
    public static readonly IReadOnlyList<string> BaseServices = ["gateway", "caddy", "searxng", "media"];
    private const string ComposeRelativePath = @"deploy\missum-ai\compose.yaml";
    private const long GiB = 1L << 30;

    private readonly AssistantRuntimeProfile _profile;
    private readonly Func<string, string?> _environment;
    private readonly Func<string, IReadOnlyList<string>, Action<string>?, CancellationToken, Task<ProcessResult>> _run;
    private readonly Func<string, long> _freeBytes;

    public AiStackSetupService(AssistantRuntimeProfile profile)
        : this(profile, Environment.GetEnvironmentVariable, RunProcessAsync, FreeBytesOnDrive) { }

    internal AiStackSetupService(AssistantRuntimeProfile profile, Func<string, string?> environment,
        Func<string, IReadOnlyList<string>, Action<string>?, CancellationToken, Task<ProcessResult>> run, Func<string, long> freeBytes)
    {
        _profile = profile;
        _environment = environment;
        _run = run;
        _freeBytes = freeBytes;
    }

    /// <summary>Image tag of this app version (major.minor.build), shared by app, images and installer.</summary>
    public static string ImageVersion => typeof(AiStackSetupService).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    public string DataRoot => _profile.StackDataRoot;
    public string ModelRoot => Path.Combine(DataRoot, "Models");
    public string EnvironmentFile => Path.Combine(DataRoot, "stack.env");
    private string StateFile => Path.Combine(DataRoot, "setup-state.json");
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The stack sources: an explicit <c>MISSUM_AI_STACK_ROOT</c>, the installer's versioned folder,
    /// or - for developers - the repository the app was built from.
    /// </summary>
    public string? LocateStackRoot()
    {
        var candidates = new List<string?> { _environment(StackRootVariable), Path.Combine(LocalAppData, "Missum", "AiStack", ImageVersion) };
        foreach (var start in new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory })
            for (var directory = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                candidates.Add(directory.FullName);
        return candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(static candidate => Path.GetFullPath(candidate!))
            .FirstOrDefault(static root => File.Exists(Path.Combine(root, ComposeRelativePath)));
    }

    /// <summary>Disk space the chosen components need on the Docker/WSL drive (images, build cache and models).</summary>
    public static long RequiredBytes(AiStackComponents components) =>
        12 * GiB
        + (components.HasFlag(AiStackComponents.Speech) ? 26 * GiB : 0)
        + (components.HasFlag(AiStackComponents.Image) ? 12 * GiB : 0)
        + AiStackWorkerModels.TotalBytes(components);

    public AiStackSetupState? ReadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<AiStackSetupState>(File.ReadAllText(StateFile)) : null; }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when the installer placed this version's stack sources (not a developer checkout).</summary>
    public static bool HasInstalledBundle => File.Exists(Path.Combine(LocalAppData, "Missum", "AiStack", ImageVersion, ComposeRelativePath));

    /// <summary>True when the compose project already has its gateway and proxy containers (false without Docker).</summary>
    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await TryRunAsync("docker", ["ps", "--all", "--filter", "label=com.docker.compose.project=" + MissumAiStackLifecycleService.ComposeProject,
            "--format", "{{.Label \"com.docker.compose.service\"}}"], cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 }) return false;
        var services = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return services.Contains("gateway") && services.Contains("caddy");
    }

    public async Task<AiStackRequirementReport> CheckRequirementsAsync(AiStackComponents components, CancellationToken cancellationToken)
    {
        var items = new List<AiStackRequirement>();
        var root = LocateStackRoot();
        items.Add(new("Missum-KI-Dateien", root is not null,
            root ?? "Nicht gefunden. Installiere Missum mit dem Installer erneut."));

        var client = await TryRunAsync("docker", ["version", "--format", "{{.Client.Version}}"], cancellationToken).ConfigureAwait(false);
        items.Add(new("Docker Desktop", client is { ExitCode: 0 },
            client is { ExitCode: 0 } ? "Version " + client.Output.Trim() : "Nicht installiert. Installiere Docker Desktop (WSL2-Backend) und starte Missum neu."));
        if (client is { ExitCode: 0 })
        {
            var os = await TryRunAsync("docker", ["info", "--format", "{{.OSType}}"], cancellationToken).ConfigureAwait(false);
            var running = os is { ExitCode: 0 };
            items.Add(new("Docker-Engine", running && os!.Output.Trim() == "linux",
                !running ? "Docker Desktop läuft nicht. Starte Docker Desktop und prüfe erneut."
                : os!.Output.Trim() == "linux" ? "Läuft mit Linux-Containern" : "Docker nutzt Windows-Container; stelle in Docker Desktop auf Linux-Container um."));
        }

        var gpus = await TryRunAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], cancellationToken).ConfigureAwait(false);
        var gpuNames = gpus is { ExitCode: 0 }
            ? gpus.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        items.Add(new("NVIDIA-Grafikkarte", gpuNames.Length > 0,
            gpuNames.Length > 0 ? string.Join(", ", gpuNames) : "Keine NVIDIA-GPU mit aktuellem Treiber gefunden (nvidia-smi)."));

        var free = _freeBytes(LocalAppData);
        var required = RequiredBytes(components);
        items.Add(new("Freier Speicher", free >= required,
            $"{FormatBytes(free)} frei, benötigt etwa {FormatBytes(required)} (Laufwerk von Docker/WSL)."));
        return new(items, gpuNames.Length, free);
    }

    /// <summary>
    /// The complete setup: folders, <c>stack.env</c>, worker models, image build and start.
    /// Every step is idempotent, so an interrupted setup simply runs again.
    /// </summary>
    public async Task InstallAsync(AiStackComponents components, IProgress<AiStackSetupProgress> progress, CancellationToken cancellationToken)
    {
        var report = await CheckRequirementsAsync(components, cancellationToken).ConfigureAwait(false);
        var missing = report.Items.Where(static item => item.Blocking && !item.Met).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("Die KI-Einrichtung kann noch nicht starten: " + string.Join("; ", missing.Select(static item => item.Name + " – " + item.Detail)));
        var root = LocateStackRoot()!;
        var compose = Path.Combine(root, ComposeRelativePath);
        var logPath = Path.Combine(_profile.DataDirectory, "Logs", $"ai-stack-setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        void Line(AiStackSetupStage stage, string text, double? fraction = null)
        {
            lock (log) log.WriteLine($"{DateTime.Now:HH:mm:ss} [{stage}] {text}");
            progress.Report(new(stage, text, fraction));
        }

        Line(AiStackSetupStage.Prepare, "Bereite Datenordner und Konfiguration vor …");
        foreach (var folder in new[] { ModelRoot, Path.Combine(DataRoot, "data", "database"), Path.Combine(DataRoot, "data", "uploads"),
                     Path.Combine(DataRoot, "data", "artifacts", "worker"), Path.Combine(DataRoot, "data", "logs") })
            Directory.CreateDirectory(folder);
        var existing = ReadEnvironmentFile(EnvironmentFile);
        var values = BuildEnvironment(existing, DataRoot, ModelRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ImageVersion, report.GpuCount);
        WriteEnvironmentFile(EnvironmentFile, values);

        if (components != AiStackComponents.None)
        {
            Line(AiStackSetupStage.Models, $"Lade Worker-Modelle ({FormatBytes(AiStackWorkerModels.TotalBytes(components))}) …", 0);
            using var downloader = new AiStackWorkerModelDownloader();
            // Synchronous forwarding: a Progress<T> would post lines after the log is closed.
            await downloader.EnsureAsync(ModelRoot, Path.Combine(DataRoot, "Downloads"), components,
                new ForwardingProgress(step => Line(step.Stage, step.Message, step.Fraction)), cancellationToken).ConfigureAwait(false);
        }

        var services = ServicesFor(components);
        string[] composeBase = ["compose", "--project-name", MissumAiStackLifecycleService.ComposeProject, "--env-file", EnvironmentFile, "-f", compose];
        Line(AiStackSetupStage.Build, "Baue die Missum-Docker-Images. Beim ersten Mal lädt Docker die Basis-Images; das kann lange dauern …");
        await RunComposeAsync([.. composeBase, "--progress", "plain", "build", .. services], AiStackSetupStage.Build, Line, cancellationToken).ConfigureAwait(false);
        Line(AiStackSetupStage.Start, "Starte die Missum-Dienste …");
        await RunComposeAsync([.. composeBase, "up", "--detach", "--no-build", "--pull", "missing", .. services], AiStackSetupStage.Start, Line, cancellationToken).ConfigureAwait(false);

        var installed = (ReadState()?.Components ?? AiStackComponents.None) | components;
        File.WriteAllText(StateFile, JsonSerializer.Serialize(new AiStackSetupState(ImageVersion, installed, DateTimeOffset.UtcNow)));
        Line(AiStackSetupStage.Done, "Der lokale KI-Stack ist eingerichtet. Protokoll: " + logPath, 1);
    }

    private readonly Lock _runLock = new();
    private readonly LinkedList<string> _recentLines = new();
    private CancellationTokenSource? _runCancellation;

    /// <summary>The running (or last) setup; it continues while the settings page is closed.</summary>
    public Task? CurrentRun { get; private set; }
    public bool IsRunning => CurrentRun is { IsCompleted: false };
    public AiStackSetupProgress? LastProgress { get; private set; }
    public event Action<AiStackSetupProgress>? ProgressChanged;

    /// <summary>The newest output lines of the current or last setup, for a page that opens mid-run.</summary>
    public IReadOnlyList<string> RecentLines { get { lock (_runLock) return [.. _recentLines]; } }

    /// <summary>Starts the setup in the background; a second call while it runs returns the same task.</summary>
    public Task StartInstall(AiStackComponents components)
    {
        lock (_runLock)
        {
            if (CurrentRun is { IsCompleted: false } running) return running;
            _recentLines.Clear();
            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            var token = _runCancellation.Token;
            CurrentRun = Task.Run(() => InstallAsync(components, new ForwardingProgress(Publish), token), token);
            return CurrentRun;
        }
    }

    public void CancelInstall()
    {
        lock (_runLock) _runCancellation?.Cancel();
    }

    public void Dispose()
    {
        lock (_runLock)
        {
            _runCancellation?.Cancel();
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }

    private void Publish(AiStackSetupProgress step)
    {
        lock (_runLock)
        {
            LastProgress = step;
            // Download progress repeats its line; keep each line once.
            if (_recentLines.Last?.Value != step.Message) _recentLines.AddLast(step.Message);
            while (_recentLines.Count > 400) _recentLines.RemoveFirst();
        }
        ProgressChanged?.Invoke(step);
    }

    public static IReadOnlyList<string> ServicesFor(AiStackComponents components) =>
    [
        .. BaseServices,
        .. components.HasFlag(AiStackComponents.Speech) ? ["speech"] : Array.Empty<string>(),
        .. components.HasFlag(AiStackComponents.Image) ? ["image"] : Array.Empty<string>(),
    ];

    /// <summary>
    /// The compose variables (C# counterpart of <c>Write-MissumAiStackEnvironment</c> in windows/common.ps1).
    /// Existing values such as a LAN address are kept; a new installation is local-only. On a PC with
    /// one GPU the speech and image workers share GPU 0 instead of the two-GPU default.
    /// </summary>
    internal static SortedDictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string> existing,
        string dataRoot, string modelRoot, string userProfile, string imageVersion, int gpuCount)
    {
        static string Slash(string path) => Path.GetFullPath(path).Replace('\\', '/');
        var values = new SortedDictionary<string, string>(existing.ToDictionary(), StringComparer.Ordinal);
        var nativeModels = Slash(Path.Combine(userProfile, ".cache", "huggingface", "hub"));
        values["MISSUM_AI_DATA_ROOT"] = Slash(dataRoot);
        values["MISSUM_AI_MODEL_ROOT"] = Slash(modelRoot);
        values["MISSUM_AI_NATIVE_MODEL_ROOT"] = nativeModels;
        values["MISSUM_AI_CODING_MODEL_ROOT"] = nativeModels;
        values.TryAdd("MISSUM_AI_MODEL_RUNTIME_URL", "http://host.docker.internal:8081");
        var address = values.TryGetValue("MISSUM_AI_EXPECTED_LAN_IP", out var saved) && !string.IsNullOrWhiteSpace(saved) ? saved : "127.0.0.1";
        values["MISSUM_AI_EXPECTED_LAN_IP"] = address;
        values["MISSUM_AI_PUBLIC_URL"] = $"http://{address}:8080";
        values["MISSUM_AI_BIND_ADDRESS"] = address is "127.0.0.1" or "localhost" ? "127.0.0.1" : "0.0.0.0";
        values["MISSUM_AI_IMAGE_VERSION"] = imageVersion;
        if (gpuCount <= 1)
        {
            values["MISSUM_AI_SPEECH_CUDA_DEVICES"] = "0";
            values["MISSUM_AI_SUPERTONIC_CUDA_DEVICE"] = "0";
            values["MISSUM_AI_IMAGE_CUDA_DEVICES"] = "0";
        }
        return values;
    }

    internal static Dictionary<string, string> ReadEnvironmentFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || line.TrimStart().StartsWith('#')) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
        }
        return values;
    }

    private static void WriteEnvironmentFile(string path, IReadOnlyDictionary<string, string> values) =>
        File.WriteAllText(path, string.Concat(values.Select(static pair => pair.Key + "=" + pair.Value + "\n")), new UTF8Encoding(false));

    private async Task RunComposeAsync(IReadOnlyList<string> arguments, AiStackSetupStage stage,
        Action<AiStackSetupStage, string, double?> line, CancellationToken cancellationToken)
    {
        var result = await _run("docker", arguments, text => line(stage, text, null), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"docker compose ist mit Code {result.ExitCode} fehlgeschlagen. Details stehen im Einrichtungsprotokoll.");
    }

    private async Task<ProcessResult?> TryRunAsync(string file, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await _run(file, arguments, null, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal sealed record ProcessResult(int ExitCode, string Output);

    private sealed class ForwardingProgress(Action<AiStackSetupProgress> report) : IProgress<AiStackSetupProgress>
    {
        public void Report(AiStackSetupProgress value) => report(value);
    }

    /// <summary>Runs a tool without a window; each output line goes to <paramref name="onLine"/> as it arrives.</summary>
    private static async Task<ProcessResult> RunProcessAsync(string file, IReadOnlyList<string> arguments, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Receive(string? text)
        {
            if (text is null) return;
            lock (output) output.AppendLine(text);
            onLine?.Invoke(text);
        }
        process.OutputDataReceived += (_, args) => Receive(args.Data);
        process.ErrorDataReceived += (_, args) => Receive(args.Data);
        try
        {
            if (!process.Start()) throw new InvalidOperationException(file + " konnte nicht gestartet werden.");
        }
        catch (Win32Exception exception) { throw new InvalidOperationException(file + " ist nicht installiert.", exception); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit();
        lock (output) return new(process.ExitCode, output.ToString());
    }

    private static long FreeBytesOnDrive(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { return 0; }
    }

    internal static string FormatBytes(long bytes) =>
        (bytes / (double)GiB).ToString(bytes >= 100 * GiB ? "0" : "0.0", CultureInfo.GetCultureInfo("de-DE")) + " GB";
}
