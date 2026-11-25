using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Missum.App.Services.ModelHub;

public enum ModelDownloadState { Queued, Running, Paused, Verifying, Completed, Failed, Canceled }

/// <summary>One quantization being downloaded. Persisted, so a paused or interrupted job resumes after a restart.</summary>
public sealed class ModelDownloadJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Repo { get; init; } = "";
    public string Commit { get; init; } = "";
    public string Label { get; init; } = "";
    public string Folder { get; init; } = "";
    public List<HubFile> Files { get; init; } = [];
    public string CatalogId { get; init; } = "";
    /// <summary>Repository path → local file with identical content (an update reuses it instead of downloading).</summary>
    public Dictionary<string, string> Reuse { get; init; } = new(StringComparer.Ordinal);
    public ModelDownloadState State { get; set; } = ModelDownloadState.Queued;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FailedAt { get; set; }
    /// <summary>null while unknown, true once the gateway lists the model, false when its folder is not scanned.</summary>
    public bool? GatewayReady { get; set; }

    public long TotalBytes => Files.Sum(f => f.Size);
    [System.Text.Json.Serialization.JsonIgnore] public long DoneBytes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public double BytesPerSecond { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Name => new HubModel(Repo, default, null, false).Name;
    [System.Text.Json.Serialization.JsonIgnore] public double Progress => TotalBytes == 0 ? 0 : Math.Clamp(DoneBytes / (double)TotalBytes, 0, 1);
    [System.Text.Json.Serialization.JsonIgnore] public bool IsActive => State is ModelDownloadState.Queued or ModelDownloadState.Running or ModelDownloadState.Verifying;
}

/// <summary>
/// Downloads GGUF quantizations into the Hugging Face cache layout the native catalog scans:
/// &lt;folder&gt;\hub\models--unsloth--X\snapshots\&lt;commit&gt;\&lt;path&gt;. Each file is staged as
/// ".download\&lt;path&gt;.part" (never a *.gguf name, so the catalog ignores it), resumed with HTTP
/// Range requests, checked against its SHA-256 and moved into place; the -00001- part comes last
/// and refs/main is written at the very end. One job runs at a time.
/// </summary>
public sealed class ModelDownloadService : IAsyncDisposable
{
    public const long FreeSpaceMargin = 5L * 1000 * 1000 * 1000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly ILogger<ModelDownloadService> _logger;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>?>>? _gatewayModels;
    private readonly HttpClient _http;
    private readonly string _stateFile;
    private readonly List<ModelDownloadJob> _jobs = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private CancellationTokenSource? _current;
    private string? _currentId;
    private DateTimeOffset _lastNotify;

    public ModelDownloadService(ILogger<ModelDownloadService> logger, string dataDirectory,
        Func<string, CancellationToken, Task<IReadOnlyList<string>?>>? gatewayModels = null, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _gatewayModels = gatewayModels;
        _http = handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Missum", typeof(ModelDownloadService).Assembly.GetName().Version?.ToString(3) ?? "2.0"));
        _stateFile = Path.Combine(dataDirectory, "ModelHub", "downloads.json");
        Load();
        _worker = Task.Run(() => WorkAsync(_lifetime.Token));
    }

    /// <summary>Raised on state changes and, while downloading, a few times per second.</summary>
    public event Action? Changed;

    public IReadOnlyList<ModelDownloadJob> Jobs
    {
        get { lock (_gate) return _jobs.ToList(); }
    }

    /// <summary>Base address for downloads; tests point it at a local server.</summary>
    internal Func<string, string, string, Uri> ResolveUri { get; set; } = HuggingFaceHubClient.ResolveUri;

    public ModelDownloadJob? Find(string repo, string commit, string label)
    {
        lock (_gate)
            return _jobs.LastOrDefault(j => j.Repo == repo && j.Commit == commit && j.Label == label && j.State != ModelDownloadState.Canceled);
    }

    /// <summary>
    /// Bytes a new download still has to write to disk: files already in place, files reused from an
    /// older snapshot (hard link) and partial files that resume are not counted twice.
    /// </summary>
    public static long BytesToDownload(string folder, string repo, string commit, HubQuantization quantization, IReadOnlyDictionary<string, string>? reuse = null)
    {
        long bytes = 0;
        foreach (var file in quantization.AllFiles)
        {
            var target = ModelHubCatalogId.FilePath(folder, repo, commit, file.Path);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size) continue;
            if (reuse is not null && reuse.TryGetValue(file.Path, out var source) && File.Exists(source)) continue;
            var part = Path.Combine(ModelHubCatalogId.SnapshotFolder(folder, repo, commit), ".download", file.Path.Replace('/', Path.DirectorySeparatorChar) + ".part");
            bytes += file.Size - (File.Exists(part) ? Math.Min(file.Size, new FileInfo(part).Length) : 0);
        }
        return bytes;
    }

    /// <summary>What queued and running downloads on the same drive still need.</summary>
    public long PendingBytesOnDrive(string folder)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        lock (_gate)
            return _jobs.Where(j => j.IsActive || j.State == ModelDownloadState.Paused)
                .Where(j => string.Equals(Path.GetPathRoot(j.Folder), root, StringComparison.OrdinalIgnoreCase))
                .Sum(j => Math.Max(0, j.TotalBytes - j.DoneBytes));
    }

    /// <summary>True when every file of the quantization is already in place.</summary>
    public static bool IsInstalled(string folder, string repo, string commit, HubQuantization quantization) =>
        quantization.AllFiles.All(file =>
        {
            var path = ModelHubCatalogId.FilePath(folder, repo, commit, file.Path);
            return File.Exists(path) && new FileInfo(path).Length == file.Size;
        });

    public ModelDownloadJob Enqueue(HubModelDetails details, HubQuantization quantization, string folder, IReadOnlyDictionary<string, string>? reuse = null)
    {
        lock (_gate)
        {
            var existing = _jobs.LastOrDefault(j => j.Repo == details.Id && j.Commit == details.Commit && j.Label == quantization.Label
                && j.State is not (ModelDownloadState.Canceled or ModelDownloadState.Completed));
            if (existing is not null)
            {
                if (existing.State is ModelDownloadState.Paused or ModelDownloadState.Failed) { existing.State = ModelDownloadState.Queued; existing.Error = null; }
                Save();
                _signal.Release();
                Notify(force: true);
                return existing;
            }
            // Companions first, then the remaining parts, the -00001- part last: the catalog sees the model only when complete.
            var ordered = quantization.Companions.Concat(quantization.Parts.Skip(1)).Append(quantization.Primary).ToList();
            var job = new ModelDownloadJob
            {
                Repo = details.Id,
                Commit = details.Commit,
                Label = quantization.Label,
                Folder = Path.GetFullPath(folder),
                Files = ordered,
                CatalogId = ModelHubCatalogId.For(details.Id, details.Commit, quantization.Primary.Path),
                Reuse = reuse is null ? new(StringComparer.Ordinal) : new(reuse, StringComparer.Ordinal),
            };
            job.DoneBytes = MeasureDone(job);
            _jobs.Add(job);
            Save();
            _signal.Release();
            Notify(force: true);
            return job;
        }
    }

    public void Pause(string id)
    {
        lock (_gate)
        {
            if (Get(id) is not { IsActive: true } job) return;
            job.State = ModelDownloadState.Paused;
            if (_currentId == id) _current?.Cancel();
            Save();
        }
        Notify(force: true);
    }

    public void Resume(string id)
    {
        lock (_gate)
        {
            if (Get(id) is not { State: ModelDownloadState.Paused or ModelDownloadState.Failed } job) return;
            job.State = ModelDownloadState.Queued;
            job.Error = null;
            job.FailedAt = null;
            Save();
        }
        _signal.Release();
        Notify(force: true);
    }

    /// <summary>Stops the job and deletes its partial files; finished files of other jobs stay.</summary>
    public void Cancel(string id)
    {
        ModelDownloadJob? job;
        lock (_gate)
        {
            job = Get(id);
            if (job is null || job.State == ModelDownloadState.Completed) return;
            job.State = ModelDownloadState.Canceled;
            if (_currentId == id) _current?.Cancel();
            Save();
        }
        // The worker may still hold the part file for a moment; delete once it let go.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                lock (_gate) if (_currentId != id) break;
                await Task.Delay(100).ConfigureAwait(false);
            }
            DeleteStaging(job);
            Notify(force: true);
        });
        Notify(force: true);
    }

    /// <summary>Removes a finished, failed or canceled job from the list.</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            if (Get(id) is not { } job || job.IsActive || job.State == ModelDownloadState.Paused) return;
            _jobs.Remove(job);
            Save();
        }
        Notify(force: true);
    }

    private ModelDownloadJob? Get(string id) => _jobs.FirstOrDefault(j => j.Id == id);

    private async Task WorkAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            ModelDownloadJob? job;
            lock (_gate) job = _jobs.FirstOrDefault(j => j.State == ModelDownloadState.Queued);
            if (job is null)
            {
                try { await _signal.WaitAsync(lifetime).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            lock (_gate)
            {
                if (job.State != ModelDownloadState.Queued) continue;
                job.State = ModelDownloadState.Running;
                _current = current;
                _currentId = job.Id;
                Save();
            }
            Notify(force: true);
            try
            {
                await RunJobAsync(job, current.Token).ConfigureAwait(false);
                lock (_gate) { job.State = ModelDownloadState.Completed; job.BytesPerSecond = 0; Save(); }
                Notify(force: true);
                _ = ConfirmGatewayAsync(job, lifetime);
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
                // Paused, canceled or the app is closing: the part files stay for a later resume.
                lock (_gate)
                {
                    if (job.State is ModelDownloadState.Running or ModelDownloadState.Verifying)
                        job.State = lifetime.IsCancellationRequested ? ModelDownloadState.Queued : ModelDownloadState.Paused;
                    job.BytesPerSecond = 0;
                    Save();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ModelHubLog.DownloadFailed(_logger, exception, job.Repo, job.Label);
                lock (_gate)
                {
                    job.State = ModelDownloadState.Failed;
                    job.Error = Friendly(exception);
                    job.FailedAt = DateTimeOffset.UtcNow;
                    job.BytesPerSecond = 0;
                    Save();
                }
            }
            finally
            {
                lock (_gate) { _current = null; _currentId = null; }
                Notify(force: true);
            }
        }
    }

    private async Task RunJobAsync(ModelDownloadJob job, CancellationToken cancellationToken)
    {
        var snapshot = ModelHubCatalogId.SnapshotFolder(job.Folder, job.Repo, job.Commit);
        Directory.CreateDirectory(snapshot);
        job.DoneBytes = MeasureDone(job);
        var remaining = job.TotalBytes - job.DoneBytes;
        var drive = new DriveInfo(Path.GetPathRoot(snapshot)!);
        if (drive.IsReady && drive.AvailableFreeSpace < remaining + FreeSpaceMargin)
            throw new IOException($"Nicht genug Speicherplatz auf {drive.Name}: frei {ModelHubFormat.Gigabytes(drive.AvailableFreeSpace)}, benötigt {ModelHubFormat.Gigabytes(remaining + FreeSpaceMargin)} (inkl. 5 GB Reserve).");

        foreach (var file in job.Files)
        {
            var target = ModelHubCatalogId.FilePath(job.Folder, job.Repo, job.Commit, file.Path);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size) continue;
            if (TryReuse(job, file, target)) { job.DoneBytes = MeasureDone(job); Notify(force: true); continue; }
            var part = PartPath(job, file);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            for (var attempt = 0; ; attempt++)
            {
                await DownloadFileAsync(job, file, part, cancellationToken).ConfigureAwait(false);
                SetWorking(job, ModelDownloadState.Verifying);
                Notify(force: true);
                if (file.Sha256 is null || await HashMatchesAsync(part, file.Sha256, cancellationToken).ConfigureAwait(false)) break;
                // A damaged file starts over once; a second mismatch is an error.
                File.Delete(part);
                job.DoneBytes = MeasureDone(job);
                if (attempt >= 1) throw new InvalidDataException($"Prüfsumme von {file.Path} stimmt nicht – die Datei wurde verworfen.");
                SetWorking(job, ModelDownloadState.Running);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(part, target, overwrite: true);
            SetWorking(job, ModelDownloadState.Running);
        }
        // refs/main last: written like huggingface_hub, without BOM and line break.
        var refs = Path.Combine(ModelHubCatalogId.RepositoryFolder(job.Folder, job.Repo), "refs");
        Directory.CreateDirectory(refs);
        await File.WriteAllTextAsync(Path.Combine(refs, "main"), job.Commit, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        TryDeleteEmpty(Path.Combine(snapshot, ".download"));
        job.DoneBytes = job.TotalBytes;
    }

    /// <summary>Switches between downloading and verifying, but never undoes a pause or cancel.</summary>
    private void SetWorking(ModelDownloadJob job, ModelDownloadState state)
    {
        lock (_gate) if (job.State is ModelDownloadState.Running or ModelDownloadState.Verifying) job.State = state;
        CancelIfStopped(job);
    }

    private void CancelIfStopped(ModelDownloadJob job)
    {
        lock (_gate) if (job.State is ModelDownloadState.Paused or ModelDownloadState.Canceled && _currentId == job.Id) _current?.Cancel();
    }

    /// <summary>
    /// An unchanged file of an older snapshot becomes part of the new one without a download:
    /// a hard link on the same NTFS volume (no extra space), otherwise a copy.
    /// </summary>
    private static bool TryReuse(ModelDownloadJob job, HubFile file, string target)
    {
        if (!job.Reuse.TryGetValue(file.Path, out var source) || !File.Exists(source) || new FileInfo(source).Length != file.Size) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!CreateHardLink(target, source, IntPtr.Zero)) File.Copy(source, target, overwrite: false);
            return File.Exists(target) && new FileInfo(target).Length == file.Size;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private async Task DownloadFileAsync(ModelDownloadJob job, HubFile file, string part, CancellationToken cancellationToken)
    {
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (existing == file.Size && file.Size > 0) return;
        var others = MeasureDone(job) - Math.Min(existing, file.Size);
        if (existing > file.Size) { File.Delete(part); existing = 0; }
        // Always ask the resolve URL again: the signed CDN address it redirects to expires.
        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUri(job.Repo, job.Commit, file.Path));
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // The server ignored the range: start this file from the beginning.
            existing = 0;
        }
        else if (existing > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != existing))
            throw new HttpRequestException($"Der Server setzte {file.Path} nicht an Byte {existing} fort ({(int)response.StatusCode}).");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode == HttpStatusCode.NotFound
                ? $"{file.Path} gibt es auf Hugging Face nicht mehr."
                : $"Hugging Face antwortete beim Laden von {file.Path} mit {(int)response.StatusCode}.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[8 << 20];
        var written = existing;
        var window = System.Diagnostics.Stopwatch.StartNew();
        var windowBytes = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            windowBytes += read;
            job.DoneBytes = others + written;
            if (window.Elapsed > TimeSpan.FromSeconds(1))
            {
                job.BytesPerSecond = windowBytes / window.Elapsed.TotalSeconds;
                windowBytes = 0;
                window.Restart();
            }
            Notify();
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (written != file.Size) throw new IOException($"{file.Path} ist unvollständig ({written} von {file.Size} Bytes); der Download kann fortgesetzt werden.");
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bytes already on disk: finished files plus partial files.</summary>
    private static long MeasureDone(ModelDownloadJob job)
    {
        long done = 0;
        foreach (var file in job.Files)
        {
            var target = ModelHubCatalogId.FilePath(job.Folder, job.Repo, job.Commit, file.Path);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size) { done += file.Size; continue; }
            var part = PartPath(job, file);
            if (File.Exists(part)) done += Math.Min(file.Size, new FileInfo(part).Length);
        }
        return done;
    }

    internal static string PartPath(ModelDownloadJob job, HubFile file) =>
        Path.Combine(ModelHubCatalogId.SnapshotFolder(job.Folder, job.Repo, job.Commit), ".download", file.Path.Replace('/', Path.DirectorySeparatorChar) + ".part");

    private static void DeleteStaging(ModelDownloadJob job)
    {
        foreach (var file in job.Files)
        {
            try { File.Delete(PartPath(job, file)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        TryDeleteEmpty(Path.Combine(ModelHubCatalogId.SnapshotFolder(job.Folder, job.Repo, job.Commit), ".download"));
    }

    private static void TryDeleteEmpty(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var child in Directory.GetDirectories(folder)) TryDeleteEmpty(child);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>After a download, waits until the gateway lists the model (its catalog rescans every 10 s).</summary>
    private async Task ConfirmGatewayAsync(ModelDownloadJob job, CancellationToken cancellationToken)
    {
        if (_gatewayModels is null) return;
        for (var attempt = 0; attempt < 12 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                // null: the gateway does not scan this folder.
                var models = await _gatewayModels(job.Folder, cancellationToken).ConfigureAwait(false);
                if (models is null) { lock (_gate) job.GatewayReady = false; Notify(force: true); return; }
                if (models.Contains(job.CatalogId, StringComparer.OrdinalIgnoreCase))
                {
                    lock (_gate) { job.GatewayReady = true; Save(); }
                    Notify(force: true);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ModelHubLog.Minor(_logger, exception, "gateway model check"); return; }
        }
    }

    private static string Friendly(Exception exception) => exception switch
    {
        HttpRequestException http when http.Message.Length > 0 => http.Message,
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => "Der Datenträger ist voll.",
        IOException io => io.Message,
        UnauthorizedAccessException => "In den Download-Ordner kann nicht geschrieben werden.",
        _ => exception.Message,
    };

    private void Notify(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastNotify < TimeSpan.FromMilliseconds(300)) return;
        _lastNotify = now;
        try { Changed?.Invoke(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ModelHubLog.Minor(_logger, exception, "download listener"); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var jobs = JsonSerializer.Deserialize<List<ModelDownloadJob>>(File.ReadAllText(_stateFile), Json) ?? [];
            foreach (var job in jobs)
            {
                // A download interrupted by closing Missum continues; paused ones wait for the user.
                if (job.State is ModelDownloadState.Running or ModelDownloadState.Verifying) job.State = ModelDownloadState.Queued;
                job.DoneBytes = job.State == ModelDownloadState.Completed ? job.TotalBytes : MeasureDone(job);
                _jobs.Add(job);
            }
            if (_jobs.Any(j => j.State == ModelDownloadState.Queued)) _signal.Release();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            ModelHubLog.StateFailed(_logger, exception, "read");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var temporary = _stateFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_jobs, Json));
            File.Move(temporary, _stateFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ModelHubLog.StateFailed(_logger, exception, "save");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
        _http.Dispose();
        _lifetime.Dispose();
        _signal.Dispose();
    }
}
