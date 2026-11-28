using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Missum.App.Services.Webdesign;

public sealed record WebdesignServerState(
    string Workspace,
    string Status,
    string Kind,
    int Port,
    string LocalUrl,
    string? LanUrl,
    string Root,
    string StartPage,
    bool Lan,
    bool Empty,
    string? Command,
    string? Message);

public sealed record WebdesignPageChange(string Workspace, string RelativePath, string Kind, string? Url);

/// <summary>
/// Serves every Webdesign project on its own free port. One server per project folder: a second
/// session of the same project reuses it, two projects never share a port. The active project
/// stays up; others stop after ten idle minutes, all stop when Missum exits.
/// </summary>
public sealed partial class WebdesignServeService : IAsyncDisposable
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);
    private static readonly string[] IgnoredDirectories =
        [".git", ".missum", "node_modules", "dist", "build", ".next", ".nuxt", ".astro", ".svelte-kit", ".vite", ".cache", "bin", "obj", ".vs", ".idea"];
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<WebdesignServeService> _logger;
    private readonly Timer _idleTimer;
    private string? _activeWorkspace;
    private int _disposed;

    public WebdesignServeService(ILogger<WebdesignServeService> logger)
    {
        _logger = logger;
        Instance = this;
        _idleTimer = new Timer(_ => _ = StopIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>The app's single instance, for code that is not created by the service container.</summary>
    public static WebdesignServeService? Instance { get; private set; }

    public event Action<WebdesignServerState>? StateChanged;
    public event Action<WebdesignPageChange>? PageChanged;

    internal static string Normalize(string workspace) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));

    /// <summary>The project currently shown in the "Webseite" tab (Codex tool Webdesign), or null without the tab.</summary>
    public void SetActiveWorkspace(string? workspace)
    {
        _activeWorkspace = string.IsNullOrWhiteSpace(workspace) ? null : Normalize(workspace);
        if (_activeWorkspace is not null && _entries.TryGetValue(_activeWorkspace, out var entry)) entry.Touch();
    }

    public WebdesignServerState? Get(string workspace) =>
        _entries.TryGetValue(Normalize(workspace), out var entry) ? entry.State : null;

    public IReadOnlyList<string> Log(string workspace) =>
        _entries.TryGetValue(Normalize(workspace), out var entry) ? entry.Log() : [];

    /// <summary>Starts the project's server unless it already runs, and returns its state.</summary>
    public async Task<WebdesignServerState> EnsureAsync(string workspace, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var key = Normalize(workspace);
        if (!Directory.Exists(key)) throw new DirectoryNotFoundException("Der Projektordner existiert nicht.");
        var entry = _entries.GetOrAdd(key, static path => new Entry(path));
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.Touch();
            if (entry.IsRunning) return entry.State;
            await StartLockedAsync(entry, cancellationToken).ConfigureAwait(false);
            return entry.State;
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<WebdesignServerState> RestartAsync(string workspace, CancellationToken cancellationToken = default)
    {
        var key = Normalize(workspace);
        var entry = _entries.GetOrAdd(key, static path => new Entry(path));
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopLockedAsync(entry, "restarting").ConfigureAwait(false);
            await StartLockedAsync(entry, cancellationToken).ConfigureAwait(false);
            return entry.State;
        }
        finally { entry.Gate.Release(); }
    }

    public async Task StopAsync(string workspace)
    {
        if (!_entries.TryGetValue(Normalize(workspace), out var entry)) return;
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try { await StopLockedAsync(entry, "stopped").ConfigureAwait(false); }
        finally { entry.Gate.Release(); }
    }

    /// <summary>Changes the saved serve settings and restarts the server with them.</summary>
    public async Task<WebdesignServerState> ConfigureAsync(string workspace,
        Func<WebdesignProjectConfig, WebdesignProjectConfig> change, CancellationToken cancellationToken = default)
    {
        var key = Normalize(workspace);
        var updated = WebdesignProjectConfig.Normalize(change(WebdesignProjectConfig.Load(key)));
        updated.Save(key);
        return await RestartAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>URL of a project file on the running server, or null when it is not served.</summary>
    public string? UrlFor(string workspace, string relativePath)
    {
        if (Get(workspace) is not { Status: "ready" } state) return null;
        var served = ServedPath(state.Root, relativePath);
        return served is null ? null : state.LocalUrl.TrimEnd('/') + "/" + served;
    }

    /// <summary>
    /// Project-relative path → path on the server. index.html maps to its folder, files outside
    /// the served root or in hidden folders map to null.
    /// </summary>
    internal static string? ServedPath(string root, string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        var prefix = root is "." or "" ? "" : root.TrimEnd('/') + "/";
        if (prefix.Length > 0)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            path = path[prefix.Length..];
        }
        var parts = path.Split('/');
        if (parts.Any(part => part is "" or ".." || IgnoredDirectories.Contains(part, StringComparer.OrdinalIgnoreCase))) return null;
        if (parts[^1].Equals("index.html", StringComparison.OrdinalIgnoreCase))
            return string.Join('/', parts[..^1].Select(Uri.EscapeDataString)) + (parts.Length > 1 ? "/" : "");
        return string.Join('/', parts.Select(Uri.EscapeDataString));
    }

    internal static string ChangeKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "html",
        ".css" => "css",
        ".js" or ".mjs" or ".ts" or ".jsx" or ".tsx" or ".vue" or ".svelte" or ".astro" => "script",
        _ => "asset",
    };

    private async Task StartLockedAsync(Entry entry, CancellationToken cancellationToken)
    {
        var config = WebdesignProjectConfig.Load(entry.Workspace);
        if (!File.Exists(WebdesignProjectConfig.PathFor(entry.Workspace)))
        {
            try { config.Save(entry.Workspace); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { WebdesignLog.ConfigNotSaved(_logger, entry.Workspace, exception); }
        }
        var root = Path.GetFullPath(Path.Combine(entry.Workspace, config.Root));
        if (!root.StartsWith(entry.Workspace, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root)) root = entry.Workspace;
        var rootLabel = Path.GetRelativePath(entry.Workspace, root).Replace('\\', '/');
        var command = config.Kind == "static" ? null : config.Command ?? WebdesignDevServer.DetectCommand(entry.Workspace, out _);
        var taken = _entries.Values.Where(item => item != entry && item.IsRunning).Select(item => item.State.Port).ToHashSet();
        entry.Publish(entry.State with { Status = "starting", Message = command is null ? "Server startet …" : $"Projekt-Server startet: {command}" }, StateChanged);
        try
        {
            if (command is not null)
            {
                entry.DevServer = await WebdesignDevServer.StartAsync(entry.Workspace, command, config.Lan, taken, cancellationToken).ConfigureAwait(false);
                entry.Port = entry.DevServer.Port;
            }
            else
            {
                entry.StaticServer = await WebdesignStaticServer.StartAsync(root, config.PreferredPort, config.Lan, taken, cancellationToken).ConfigureAwait(false);
                entry.Port = entry.StaticServer.Port;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WebdesignLog.StartFailed(_logger, entry.Workspace, exception);
            entry.Publish(entry.State with
            {
                Status = "failed", Kind = command is null ? "static" : "command", Command = command,
                Message = exception.Message, Root = rootLabel, StartPage = config.StartPage, Lan = config.Lan,
            }, StateChanged);
            return;
        }
        if (config.PreferredPort != entry.Port && command is null)
        {
            try { (config with { PreferredPort = entry.Port }).Save(entry.Workspace); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        entry.StartWatcher(root, IgnoredDirectories, change => OnFileChanged(entry, change));
        var lan = config.Lan ? WebdesignPorts.LanAddress() : null;
        var empty = !Directory.EnumerateFiles(root, "*.htm*", SearchOption.TopDirectoryOnly).Any();
        entry.Publish(new WebdesignServerState(entry.Workspace, "ready", command is null ? "static" : "command", entry.Port,
            $"http://127.0.0.1:{entry.Port.ToString(CultureInfo.InvariantCulture)}/",
            lan is null ? null : $"http://{lan}:{entry.Port.ToString(CultureInfo.InvariantCulture)}/",
            rootLabel, config.StartPage, config.Lan, empty, command,
            config.Lan && lan is null ? "Kein lokales Netzwerk gefunden; die Seite ist nur auf diesem PC erreichbar." : null), StateChanged);
        WebdesignLog.Started(_logger, entry.Workspace, entry.Port, null);
    }

    private void OnFileChanged(Entry entry, string relativeToRoot)
    {
        var kind = ChangeKind(relativeToRoot);
        entry.StaticServer?.Broadcast(relativeToRoot, kind);
        var projectRelative = entry.State.Root is "." or "" ? relativeToRoot : entry.State.Root.TrimEnd('/') + "/" + relativeToRoot;
        if (kind == "html" && entry.State.Empty) entry.Publish(entry.State with { Empty = false }, StateChanged);
        try { PageChanged?.Invoke(new(entry.Workspace, projectRelative, kind, UrlFor(entry.Workspace, projectRelative))); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { WebdesignLog.ObserverFailed(_logger, exception); }
    }

    private async Task StopLockedAsync(Entry entry, string status)
    {
        entry.StopWatcher();
        if (entry.StaticServer is { } server) { entry.StaticServer = null; await server.DisposeAsync().ConfigureAwait(false); }
        if (entry.DevServer is { } dev) { entry.DevServer = null; await dev.DisposeAsync().ConfigureAwait(false); }
        entry.Publish(entry.State with { Status = status, Message = null }, StateChanged);
    }

    private async Task StopIdleAsync()
    {
        foreach (var entry in _entries.Values)
        {
            if (!entry.IsRunning || string.Equals(entry.Workspace, _activeWorkspace, StringComparison.OrdinalIgnoreCase)
                || DateTimeOffset.UtcNow - entry.LastUsed < IdleLifetime) continue;
            if (!await entry.Gate.WaitAsync(0).ConfigureAwait(false)) continue;
            try { await StopLockedAsync(entry, "stopped").ConfigureAwait(false); }
            finally { entry.Gate.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        foreach (var entry in _entries.Values)
        {
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try { await StopLockedAsync(entry, "stopped").ConfigureAwait(false); }
            finally { entry.Gate.Release(); }
        }
    }

    private sealed class Entry(string workspace)
    {
        private FileSystemWatcher? _watcher;
        private CancellationTokenSource? _debounce;
        private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _pendingLock = new();

        public string Workspace { get; } = workspace;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public WebdesignStaticServer? StaticServer { get; set; }
        public WebdesignDevServer? DevServer { get; set; }
        public int Port { get; set; }
        public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;
        public WebdesignServerState State { get; private set; } =
            new(workspace, "stopped", "static", 0, "", null, ".", "index.html", false, true, null, null);

        public bool IsRunning => StaticServer is not null || DevServer is { HasExited: false };
        public void Touch() => LastUsed = DateTimeOffset.UtcNow;
        public IReadOnlyList<string> Log() => DevServer?.Log() ?? [];

        public void Publish(WebdesignServerState state, Action<WebdesignServerState>? observers)
        {
            State = state;
            try { observers?.Invoke(state); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }

        public void StartWatcher(string root, string[] ignored, Action<string> changed)
        {
            StopWatcher();
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            void Queue(string? fullPath)
            {
                if (fullPath is null) return;
                var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                var parts = relative.Split('/');
                if (relative.StartsWith("..", StringComparison.Ordinal)
                    || parts.Any(part => ignored.Contains(part, StringComparer.OrdinalIgnoreCase))
                    || parts[^1].StartsWith(".missum-coding-", StringComparison.OrdinalIgnoreCase)
                    || parts[^1].EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || parts[^1].EndsWith('~')
                    || Directory.Exists(fullPath)) return;
                CancellationTokenSource debounce;
                lock (_pendingLock)
                {
                    _pending.Add(relative);
                    _debounce?.Cancel();
                    _debounce = debounce = new CancellationTokenSource();
                }
                _ = Task.Delay(250, debounce.Token).ContinueWith(task =>
                {
                    if (task.IsCanceled) return;
                    string[] batch;
                    lock (_pendingLock) { batch = [.. _pending]; _pending.Clear(); }
                    foreach (var item in batch) changed(item);
                }, TaskScheduler.Default);
            }
            watcher.Changed += (_, args) => Queue(args.FullPath);
            watcher.Created += (_, args) => Queue(args.FullPath);
            watcher.Renamed += (_, args) => Queue(args.FullPath);
            watcher.Deleted += (_, args) => Queue(args.FullPath);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }

        public void StopWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
            lock (_pendingLock) { _debounce?.Cancel(); _pending.Clear(); }
        }
    }
}

internal static partial class WebdesignLog
{
    [LoggerMessage(EventId = 4100, Level = LogLevel.Information, Message = "Webdesign server for {Workspace} listens on port {Port}.")]
    public static partial void Started(ILogger logger, string workspace, int port, Exception? exception);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "Webdesign server for {Workspace} did not start.")]
    public static partial void StartFailed(ILogger logger, string workspace, Exception exception);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Debug, Message = "Webdesign settings for {Workspace} could not be saved.")]
    public static partial void ConfigNotSaved(ILogger logger, string workspace, Exception exception);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "A Webdesign change observer failed.")]
    public static partial void ObserverFailed(ILogger logger, Exception exception);
}
