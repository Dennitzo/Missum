using System.Collections.Concurrent;
using System.Text.Json;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed partial class ResearchSandboxService
{
    private const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectExecutions = new(StringComparer.OrdinalIgnoreCase);

    // -I retains isolated Python/environment behavior. Only the measured project
    // tree is added for sibling modules; host/user site paths remain absent.
    internal const string ReadTraceMarker = "[MISSUM_EXECUTION_INPUTS_V1]";
    internal const string PythonLauncher = """
        import os as _os, runpy as _runpy, sys as _sys, json as _json
        _p = _sys.argv[1]
        _sys.argv = _sys.argv[1:]
        _sys.path[:0] = list(dict.fromkeys([_os.path.dirname(_p), '/sandbox/work']))
        _reads, _writes = set(), set()
        def _relative(_path):
            if not isinstance(_path, (str, bytes, _os.PathLike)):
                return None
            _full = _os.path.abspath(_os.fsdecode(_path))
            if not _full.startswith('/sandbox/'):
                return None
            _part = _full[len('/sandbox/'):]
            if _part.split('/', 1)[0] not in ('work', 'inputs', 'env', 'artifacts', 'notebooks', 'manuscripts'):
                return None
            return _part
        def _audit(_event, _args):
            if _event != 'open' or not _args:
                return
            _relative_name = _relative(_args[0])
            if _relative_name is None:
                return
            _mode = _args[1] if len(_args) > 1 and isinstance(_args[1], str) else ''
            _flags = _args[2] if len(_args) > 2 and isinstance(_args[2], int) else 0
            _writing = any(_c in _mode for _c in 'wax+') or bool(_flags & (_os.O_WRONLY | _os.O_RDWR | _os.O_CREAT | _os.O_TRUNC | _os.O_APPEND))
            if _writing:
                _writes.add(_relative_name)
            if (not _writing or '+' in _mode or (_flags & 3) == _os.O_RDWR) and _os.path.isfile('/sandbox/' + _relative_name):
                _reads.add(_relative_name)
        _sys.addaudithook(_audit)
        try:
            _runpy.run_path(_p, run_name='__main__')
        finally:
            _sources = {_relative(_p)}
            for _module in list(_sys.modules.values()):
                _file = getattr(_module, '__dict__', {}).get('__file__')
                if _file:
                    _source = _relative(_file)
                    if _source is not None:
                        _sources.add(_source)
            _sources.discard(None)
            print('\n[MISSUM_EXECUTION_INPUTS_V1]' + _json.dumps({'readPaths': sorted(_reads), 'writtenPaths': sorted(_writes), 'sourcePaths': sorted(_sources)}, separators=(',', ':')), flush=True)
        """;
    internal static IReadOnlyList<string> CreatePythonInvocation(string script) =>
        ["python3", "-I", "-B", "-X", "pycache_prefix=/tmp/missum-bytecode", "-c", PythonLauncher, script];

    private sealed class ProjectExecutionLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal static async Task<IDisposable> AcquireProjectExecutionAsync(ResearchSandboxLayout layout, CancellationToken token = default)
        => await AcquireProjectExecutionAsync(layout.RootPath, token).ConfigureAwait(false);

    private static async Task<IDisposable> AcquireProjectExecutionAsync(string rootPath, CancellationToken token)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var gate = ProjectExecutions.GetOrAdd(key, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new ProjectExecutionLease(gate);
    }

    internal static async Task<long> ActiveProjectBytesAsync(ResearchSandboxLayout layout, CancellationToken token = default)
    {
        // Old archives are neither inputs nor mutable files of the current tree.
        // Their later user-authorized removal is independent of execution; a new
        // run must not reproduce the old archive's quota failure without copying.
        RejectReparsePoint(layout.RootPath);
        long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(layout.RootPath))
        {
            RejectReparsePoint(path);
            if (path.Equals(layout.SnapshotsPath, StringComparison.OrdinalIgnoreCase)) continue;
            bytes = checked(bytes + (Directory.Exists(path)
                ? await DirectoryBytesAsync(path, token).ConfigureAwait(false) : new FileInfo(path).Length));
        }
        return bytes;
    }

    private static async Task ValidateExecutionCapacityAsync(ResearchSandboxLayout layout, long maximumBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (await ActiveProjectBytesAsync(layout, token).ConfigureAwait(false) > maximumBytes)
            throw new IOException("Das 100-GiB-Kontingent des aktuellen Projektbaums ist ausgeschöpft. "
                + "Es wurden keine Dateikopien erstellt; entferne entbehrliche Ergebnisse oder wähle einen anderen Projektordner, bevor dieselbe Berechnung erneut gestartet wird.");
    }

    private static void EnsureFreeSpace(string root)
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
        // DriveInfo cannot measure SMB capacity; the active quota still applies.
        if (string.IsNullOrEmpty(driveRoot) || driveRoot.StartsWith("\\\\", StringComparison.Ordinal)) return;
        if (new DriveInfo(driveRoot).AvailableFreeSpace < MinimumFreeBytes)
            throw new IOException("Auf dem Projektlaufwerk sind weniger als 2 GiB frei. "
                + "Vor weiterer Berechnung zuerst Speicher freigeben oder einen anderen Projektordner wählen.");
    }

    internal static async Task<string> WaitForCapacityViolationAsync(ResearchSandboxLayout layout,
        long maximumBytes = MaximumProjectBytes, TimeSpan? pollingInterval = null, CancellationToken token = default)
    {
        var interval = pollingInterval ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero, nameof(pollingInterval));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await ValidateExecutionCapacityAsync(layout, maximumBytes, token).ConfigureAwait(false);
                EnsureFreeSpace(layout.RootPath);
            }
            catch (FileNotFoundException) when (Directory.Exists(layout.RootPath))
            {
                // Atomic output replacement can remove a directory entry while
                // its size is being measured. Retry on the next poll; source
                // integrity is independently validated after execution.
            }
            catch (DirectoryNotFoundException) when (Directory.Exists(layout.RootPath)) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "Die laufende Berechnung wurde wegen des Sandbox-Speicherlimits beendet. " + exception.Message;
            }
            await Task.Delay(interval, token).ConfigureAwait(false);
        }
    }

    private static bool IsExecutionSource(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".py" or ".pyi" or ".pyc" or ".pyd" or ".so" or ".dll";
    private static bool IsReadOnlyInput(string path) => path.StartsWith("inputs/", StringComparison.Ordinal)
        || path.StartsWith("env/", StringComparison.Ordinal) || path.StartsWith("manuscripts/", StringComparison.Ordinal);

    internal sealed record ExecutionReadTrace(IReadOnlySet<string> ReadPaths, IReadOnlySet<string> WrittenPaths, IReadOnlySet<string> SourcePaths);
    internal sealed record ParsedExecutionTrace(string StandardOutput, ExecutionReadTrace? Trace);

    internal static ParsedExecutionTrace ParseExecutionReadTrace(string standardOutput)
    {
        var marker = standardOutput.IndexOf(ReadTraceMarker, StringComparison.Ordinal);
        if (marker < 0) return new(standardOutput, null);
        if (standardOutput.IndexOf(ReadTraceMarker, marker + ReadTraceMarker.Length, StringComparison.Ordinal) >= 0
            || marker > 0 && standardOutput[marker - 1] != '\n')
            throw new IOException("Der Laufzeitbeleg der gelesenen Dateien ist mehrdeutig oder ungültig.");
        var end = standardOutput.IndexOf('\n', marker);
        if (end < 0) end = standardOutput.Length;
        try
        {
            using var document = JsonDocument.Parse(standardOutput[(marker + ReadTraceMarker.Length)..end]);
            var root = document.RootElement;
            var trace = new ExecutionReadTrace(ReadTracePaths(root, "readPaths"), ReadTracePaths(root, "writtenPaths"), ReadTracePaths(root, "sourcePaths"));
            return new(standardOutput.Remove(marker, end - marker).TrimEnd(), trace);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new IOException("Der Laufzeitbeleg der gelesenen Dateien ist ungültig.", exception);
        }
    }

    private static HashSet<string> ReadTracePaths(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 4096)
            throw new InvalidOperationException("Ein Laufzeit-Dateibeleg ist unvollständig oder zu groß.");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var path = item.GetString();
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
                throw new InvalidOperationException("Der Laufzeit-Dateibeleg enthält einen ungültigen Pfad.");
            var parts = path.Split('/');
            if (parts.Length < 2 || parts[0] is not ("work" or "inputs" or "env" or "artifacts" or "notebooks" or "manuscripts")
                || parts.Any(part => part.Length == 0 || part is "." or ".."))
                throw new InvalidOperationException("Der Laufzeit-Dateibeleg enthält einen Pfad außerhalb des aktuellen Projektbaums.");
            result.Add(path);
        }
        return result;
    }

    internal static ResearchExecutionSnapshot BindExecutionReadTrace(ResearchExecutionSnapshot snapshot, ExecutionReadTrace trace)
    {
        var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in trace.ReadPaths.Concat(trace.SourcePaths).Append(snapshot.ExecutedScriptPath).Distinct(StringComparer.Ordinal))
        {
            if (snapshot.InputHashes.TryGetValue(path, out var hash) || snapshot.BeforeOutputHashes.TryGetValue(path, out hash))
                used.Add(path, hash);
            else if (trace.SourcePaths.Contains(path) || IsExecutionSource(path) || !trace.WrittenPaths.Contains(path))
                throw new IOException("Eine tatsächlich gelesene Datei besitzt keinen vorab gemessenen Ausgangsstand: " + path);
        }
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in used)
            if (trace.SourcePaths.Contains(pair.Key) || IsExecutionSource(pair.Key) || pair.Key == snapshot.ExecutedScriptPath
                || trace.ReadPaths.Contains(pair.Key) && !trace.WrittenPaths.Contains(pair.Key))
                sources.Add(pair.Key, pair.Value);
        ValidateHashBudget(used);
        return snapshot with { InputHashes = used, SourceHashes = sources, ReadsMeasured = true, BaselineInputHashes = snapshot.InputHashes };
    }

    private static Task WriteExecutionStatusAsync(ResearchSandboxLayout layout, string runId, ResearchExecutionSnapshot snapshot,
        string phase, DateTimeOffset startedAt, DateTimeOffset? completedAt, int? exitCode, string? error,
        string standardOutput = "", string standardError = "") => File.WriteAllTextAsync(Path.Combine(layout.RunsPath, runId + ".json"),
        JsonSerializer.Serialize(new
        {
            schemaVersion = 3, layout.ProjectId, runId, phase, startedAt, completedAt, exitCode, error,
            arguments = snapshot.Arguments, workingDirectory = snapshot.WorkingDirectory,
            snapshot.ExecutedScriptPath, snapshot.ScriptSha256, snapshot.SnapshotId, snapshot.InputHashes,
            provenance = new { version = 1, mode = "current-project-tree", executionProfile = snapshot.ExecutionProfile.ToString(),
                excludedDirectoryPattern = ExecutionExcludedDirectoryPattern(snapshot.ExecutionProfile), sourceHashes = snapshot.SourceHashes,
                inputsSha256Before = snapshot.InputHashes }, standardOutput, standardError,
        }, JsonOptions), CancellationToken.None);
}
