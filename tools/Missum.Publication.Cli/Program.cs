using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Missum.App.Services;

namespace Missum.Publication.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions ProgressJsonOptions = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("Missum.Publication.Cli --project <Verzeichnis> [--check-only]");
            Console.WriteLine("Liest Publikation.md, prüft Missum/KaTeX und erstellt atomar Publikation.pdf. Meldungen: JSONL; Bericht: publication-build.json.");
            return 0;
        }

        string project;
        bool checkOnly;
        try { (project, checkOnly) = ParseArguments(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, action) => { action.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try { return await RunAsync(project, checkOnly, cancellation.Token).ConfigureAwait(false); }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static (string Project, bool CheckOnly) ParseArguments(string[] args)
    {
        var project = Directory.GetCurrentDirectory();
        var checkOnly = false;
        var projectSet = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--check-only" && !checkOnly) checkOnly = true;
            else if (args[index] == "--project" && !projectSet && index + 1 < args.Length)
            {
                project = args[++index];
                projectSet = true;
            }
            else throw new ArgumentException("Aufruf: Missum.Publication.Cli --project <Verzeichnis> [--check-only]. Unbekanntes oder unvollständiges Argument: " + args[index]);
        }
        return (Path.GetFullPath(project), checkOnly);
    }

    private static async Task<int> RunAsync(string project, bool checkOnly, CancellationToken token)
    {
        var source = Path.Combine(project, "Publikation.md");
        var output = Path.Combine(project, "Publikation.pdf");
        var reportPath = Path.Combine(project, "publication-build.json");
        var logPath = Path.Combine(project, "publication-build.log");
        var report = new BuildReport(source, output, checkOnly);
        ProgressLog? log = null;
        FileStream? projectLock = null;
        string? staging = null;
        string? preparedPdf = null;
        var original = "";
        var step = "input";
        try
        {
            if (!Directory.Exists(project)) throw new DirectoryNotFoundException("Projektverzeichnis fehlt: " + project);
            try { projectLock = File.Open(Path.Combine(project, ".publication-build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) { throw new InvalidOperationException("In diesem Projekt läuft bereits eine Publikationserzeugung oder die Sperrdatei ist nicht zugänglich. Bitte den aktiven Vorgang beenden lassen.", exception); }
            var previous = await ReadPreviousSuccessAsync(reportPath, token).ConfigureAwait(false);
            report.LastSuccessfulRender = previous;
            log = new ProgressLog(logPath);
            log.Write(step, "Publikation wird im angegebenen Projektverzeichnis gesucht.", source);
            if (!File.Exists(source)) throw new ScientificPublicationContentException("Publikation.md fehlt im Projektverzeichnis: " + source);
            var originalBytes = await File.ReadAllBytesAsync(source, token).ConfigureAwait(false);
            original = Utf8.GetString(originalBytes);
            if (original.StartsWith('\uFEFF')) original = original[1..];
            if (string.IsNullOrWhiteSpace(original)) throw new ScientificPublicationContentException("Publikation.md ist leer.");

            step = "preparation";
            log.Write(step, "Manuskript und explizit verknüpfte Abbildungen werden mit den gemeinsamen Missum-Funktionen vorbereitet.");
            var prose = ScientificPublicationService.PreparePublicationProse(original);
            var images = await ScientificPublicationImages.PrepareDocumentAsync(prose, project, project, token).ConfigureAwait(false);
            var snapshots = await CaptureSourcesAsync(source, originalBytes, prose, project, images, token).ConfigureAwait(false);
            log.Write(step, "Quelltext unverändert erfasst; Abbildungen vorbereitet.", new { images = images.Images.Length, sourceFiles = images.SourceFiles.Count });

            step = "dependencies";
            var rendererRoot = AppContext.BaseDirectory;
            var exporter = Path.Combine(rendererRoot, "Assets", "Scripts", "export-document.ps1");
            var assets = Path.Combine(rendererRoot, "Assets", "DocumentRendering");
            RequireRenderingAssets(exporter, assets);
            var edge = ResolveEdge();
            var runtime = new RuntimeInfo(edge, FileVersionInfo.GetVersionInfo(edge).FileVersion ?? "unknown",
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
            report.Runtime = runtime;
            report.InputFingerprint = await ComputeFingerprintAsync(originalBytes, images, exporter, assets, runtime, token).ConfigureAwait(false);
            log.Write(step, "Gemeinsamer Missum-Renderer und Microsoft Edge gefunden.", report.Runtime);

            if (!checkOnly && previous?.InputFingerprint == report.InputFingerprint && File.Exists(output))
            {
                step = "cache";
                await using var locked = await LockedSources.OpenAsync(snapshots, token).ConfigureAwait(false);
                var existingHash = await HashFileAsync(output, token).ConfigureAwait(false);
                if (existingHash == previous.OutputSha256)
                {
                    report.Status = "up-to-date";
                    report.Counts = previous.Counts;
                    report.OutputSha256 = existingHash;
                    report.ExitCode = 0;
                    log.Write(step, "Unveränderte Eingaben und identische Renderer-Version: vorhandene PDF bleibt bytegleich erhalten.");
                    return 0;
                }
            }

            staging = Path.Combine(Path.GetTempPath(), "Missum-PublicationCli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var stagedSource = Path.Combine(staging, "Publikation.md");
            var stagedOutput = Path.Combine(staging, "Publikation.pdf");
            var diagnosticsPath = Path.Combine(staging, "diagnostics.json");
            await File.WriteAllTextAsync(stagedSource, images.Markdown, Utf8, token).ConfigureAwait(false);
            await images.WriteImagesAsync(staging, token).ConfigureAwait(false);

            step = "rendering";
            log.Write(step, checkOnly ? "KaTeX, Überschriften und Abbildungen werden geprüft; die PDF wird nicht ersetzt." : "KaTeX-Prüfung und PDF-Satz werden gestartet. Es gilt kein Zeitlimit.");
            var result = await RunRendererAsync(exporter, assets, stagedSource, stagedOutput, diagnosticsPath, checkOnly, log, token).ConfigureAwait(false);
            await ReadDiagnosticsAsync(diagnosticsPath, report, original, token).ConfigureAwait(false);
            if (report.Errors.Count > 0)
            {
                report.ExitCode = report.Errors.Any(error => error.Kind is "dependency" or "renderer" or "system") ? 3 : 2;
                log.Write(step, "Der gemeinsame Renderer hat Fehler gemeldet. Die bisherige PDF bleibt erhalten.", report.Errors, "error");
                return report.ExitCode;
            }
            if (result.ExitCode != 0)
                throw new InvalidOperationException("Missum-Renderer beendet mit Exit-Code " + result.ExitCode + ". " + result.ErrorOutput);
            if (!File.Exists(diagnosticsPath)) throw new InvalidDataException("Der Renderer hat keinen Diagnostikbericht erzeugt.");

            step = "source-verification";
            log.Write(step, "Originalmanuskript und verwendete Abbildungen werden vor dem Übernehmen erneut geprüft.");
            await using (var locked = await LockedSources.OpenAsync(snapshots, token).ConfigureAwait(false))
            {
                if (!checkOnly)
                {
                    step = "publishing";
                    await VerifyPdfAsync(stagedOutput, token).ConfigureAwait(false);
                    preparedPdf = Path.Combine(project, ".publication-build-" + Guid.NewGuid().ToString("N") + ".tmp.pdf");
                    File.Copy(stagedOutput, preparedPdf);
                    report.OutputSha256 = await HashFileAsync(preparedPdf, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    try { File.Move(preparedPdf, output, overwrite: true); }
                    catch (IOException exception) { throw new IOException("Publikation.pdf konnte nicht ersetzt werden, etwa weil ein PDF-Programm sie sperrt. Die bisherige PDF bleibt erhalten. Datei schließen und erneut ausführen: " + output, exception); }
                    preparedPdf = null;
                    report.LastSuccessfulRender = new SuccessfulRender(report.InputFingerprint!, report.OutputSha256!, report.Counts);
                    log.Write(step, "PDF erfolgreich erstellt und atomar übernommen.", new { path = output, bytes = new FileInfo(output).Length });
                }
            }
            report.Status = "success";
            report.ExitCode = 0;
            if (checkOnly) log.Write("completed", "Alle Prüfungen erfolgreich. Die vorhandene PDF wurde nicht verändert.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            report.ExitCode = 3;
            report.Errors.Add(CreateError("cancelled", "", "", "Vorgang abgebrochen; die bisherige PDF bleibt erhalten.", original));
            log?.Write(step, report.Errors[^1].Reason, null, "error");
            return report.ExitCode;
        }
        catch (SourceChangedException exception)
        {
            report.ExitCode = 4;
            report.Errors.Add(CreateError("source_changed", "", "", exception.Message, original));
            log?.Write(step, exception.Message, null, "error");
            return report.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception or ScientificPublicationContentException or JsonException)
        {
            report.ExitCode = exception is ScientificPublicationContentException ? 2 : 3;
            report.Errors.Add(CreateError(report.ExitCode == 2 ? "content" : "dependency", "", ContentErrorSource(exception.Message), exception.Message, original));
            if (log is not null) log.Write(step, exception.Message, null, "error");
            else Console.Error.WriteLine(exception.Message);
            return report.ExitCode;
        }
        finally
        {
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            if (report.Status != "success" && report.Status != "up-to-date") report.Status = "error";
            if (preparedPdf is not null && File.Exists(preparedPdf))
            {
                try { File.Delete(preparedPdf); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { log?.Write("cleanup", "Temporäre PDF konnte nicht entfernt werden: " + exception.Message, preparedPdf, "warning"); }
            }
            if (staging is not null && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { log?.Write("cleanup", "Temporäres Renderverzeichnis konnte nicht vollständig entfernt werden: " + exception.Message, staging, "warning"); }
            }
            try
            {
                if (projectLock is not null) await WriteReportAsync(reportPath, report).ConfigureAwait(false);
                log?.Write("result", "Publikationsvorgang beendet.", new { report.Status, report.ExitCode, reportPath });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Bericht konnte nicht gespeichert werden: " + exception.Message); }
            log?.Dispose();
            if (projectLock is not null) await projectLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> CaptureSourcesAsync(string source, byte[] originalBytes, string prose,
        string project, ScientificPublicationImages.PreparedImages prepared, CancellationToken token)
    {
        var paths = prepared.SourceFiles.Prepend(source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var streams = new List<FileStream>();
        try
        {
            foreach (var path in paths)
            {
                var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                snapshots[path] = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            }
            if (snapshots[source] != Convert.ToHexStringLower(SHA256.HashData(originalBytes)))
                throw new SourceChangedException("Publikation.md wurde während der Vorbereitung geändert. Bitte erneut ausführen.");
            var confirmation = await ScientificPublicationImages.PrepareDocumentAsync(prose, project, project, token).ConfigureAwait(false);
            if (!string.Equals(prepared.Markdown, confirmation.Markdown, StringComparison.Ordinal)
                || !prepared.Images.Select(image => image.Sha256).SequenceEqual(confirmation.Images.Select(image => image.Sha256)))
                throw new SourceChangedException("Eine Publikationsabbildung wurde während der Vorbereitung geändert. Bitte erneut ausführen.");
            return snapshots;
        }
        finally { foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false); }
    }

    private static void RequireRenderingAssets(string exporter, string assets)
    {
        string[] required = [exporter, Path.Combine(assets, "markdown.js"), Path.Combine(assets, "styles.css"),
            Path.Combine(assets, "vendor", "katex", "0.16.10", "katex.min.js"), Path.Combine(assets, "vendor", "katex", "0.16.10", "katex.min.css")];
        foreach (var path in required)
            if (!File.Exists(path)) throw new FileNotFoundException("Eine originale Missum-Rendererdatei fehlt. Renderer-Bundle vollständig bereitstellen: " + path, path);
        var fonts = Path.Combine(assets, "vendor", "katex", "0.16.10", "fonts");
        if (!Directory.Exists(fonts) || !Directory.EnumerateFiles(fonts).Any()) throw new DirectoryNotFoundException("Die lokalen KaTeX-Schriften fehlen: " + fonts);
    }

    private static string ResolveEdge()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("MISSUM_EDGE_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")];
        foreach (var path in candidates)
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Path.GetFullPath(path);
        throw new FileNotFoundException("Microsoft Edge fehlt. Bei abweichender Installation MISSUM_EDGE_PATH auf msedge.exe setzen.");
    }

    private static async Task<string> ComputeFingerprintAsync(byte[] original, ScientificPublicationImages.PreparedImages images,
        string exporter, string assets, RuntimeInfo runtime, CancellationToken token)
    {
        var rendererFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories).Append(exporter))
            rendererFiles[Path.GetRelativePath(AppContext.BaseDirectory, path).Replace('\\', '/')] = await HashFileAsync(path, token).ConfigureAwait(false);
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll");
        if (!File.Exists(assemblyPath)) assemblyPath = Environment.ProcessPath ?? throw new FileNotFoundException("CLI-Binärdatei für die Rendererkennung fehlt.");
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            manuscript = Convert.ToHexStringLower(SHA256.HashData(original)),
            images = images.Images.Select(image => image.Sha256).ToArray(),
            rendererFiles,
            cli = await HashFileAsync(assemblyPath, token).ConfigureAwait(false),
            runtime,
        }, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(serialized));
    }

    private static async Task<RendererResult> RunRendererAsync(string exporter, string assets, string source, string output,
        string diagnostics, bool checkOnly, ProgressLog log, CancellationToken token)
    {
        var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell)) powerShell = "powershell.exe";
        var start = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        string[] arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", exporter,
            "-SourcePath", source, "-WebAssetsPath", assets, "-OutputPath", output, "-ScientificPublication", "-DiagnosticsPath", diagnostics];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (checkOnly) start.ArgumentList.Add("-ValidateOnly");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Der Missum-Renderer konnte nicht gestartet werden.");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var stderr = new StringBuilder();
        var stdoutTask = PumpAsync(process.StandardOutput, "stdout", null, log);
        var stderrTask = PumpAsync(process.StandardError, "stderr", stderr, log);
        var exited = process.WaitForExitAsync(CancellationToken.None);
        var elapsed = Stopwatch.StartNew();
        while (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None)).ConfigureAwait(false) != exited)
            log.Write("rendering", "Renderer arbeitet weiter; Formelsatz und Abbildungen werden verarbeitet.",
                new { elapsedSeconds = (int)elapsed.Elapsed.TotalSeconds });
        await exited.ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new(process.ExitCode, stderr.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, string channel, StringBuilder? captured, ProgressLog log)
    {
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            captured?.AppendLine(line);
            log.Write("renderer", line, new { channel });
        }
    }

    private static async Task ReadDiagnosticsAsync(string path, BuildReport report, string original, CancellationToken token)
    {
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        if (document.RootElement.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
            report.Counts = counts.Deserialize<RenderCounts>(JsonOptions) ?? new();
        if (!document.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return;
        foreach (var error in errors.EnumerateArray())
        {
            var source = Text(error, "source");
            if (source.Length == 0) source = Text(error, "text");
            report.Errors.Add(CreateError(Text(error, "kind"), Text(error, "section"), source, Text(error, "reason"), original));
        }
    }

    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    private static BuildError CreateError(string kind, string section, string source, string reason, string original) =>
        new(kind, section, source, reason, FindOccurrences(original, source), FindOccurrences(original, section));

    private static string ContentErrorSource(string message)
    {
        const string prefix = "Die lokale Publikationsabbildung konnte nicht geladen werden: ";
        if (!message.StartsWith(prefix, StringComparison.Ordinal)) return "";
        var value = message[prefix.Length..];
        var end = value.IndexOf(". Nutze ", StringComparison.Ordinal);
        return end >= 0 ? value[..end] : value;
    }

    private static List<SourceLocation> FindOccurrences(string original, string needle)
    {
        var result = new List<SourceLocation>();
        if (string.IsNullOrEmpty(needle)) return result;
        original = original.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        needle = needle.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var offset = 0;
        while (offset <= original.Length - needle.Length)
        {
            var index = original.IndexOf(needle, offset, StringComparison.Ordinal);
            if (index < 0) break;
            var line = 1;
            var column = 1;
            for (var cursor = 0; cursor < index; cursor++)
            {
                if (original[cursor] == '\n') { line++; column = 1; }
                else if (original[cursor] != '\r') column++;
            }
            var endLine = line;
            for (var cursor = index; cursor < index + needle.Length; cursor++) if (original[cursor] == '\n') endLine++;
            result.Add(new(line, endLine, column, index, needle.Length));
            offset = index + 1;
        }
        return result;
    }

    private static async Task VerifyPdfAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var signature = new byte[5];
        await stream.ReadExactlyAsync(signature, token).ConfigureAwait(false);
        if (stream.Length < 1024 || !signature.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("Der Renderer hat keine gültige PDF erzeugt.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static async Task<SuccessfulRender?> ReadPreviousSuccessAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("lastSuccessfulRender", out var previous)
                ? previous.Deserialize<SuccessfulRender>(JsonOptions) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static async Task WriteReportAsync(string path, BuildReport report)
    {
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(report, JsonOptions), Utf8).ConfigureAwait(false);
            File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private sealed class LockedSources(List<FileStream> streams) : IAsyncDisposable
    {
        public static async Task<LockedSources> OpenAsync(IReadOnlyDictionary<string, string> snapshots, CancellationToken token)
        {
            var streams = new List<FileStream>();
            try
            {
                foreach (var (path, expected) in snapshots)
                {
                    FileStream stream;
                    try { stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
                    catch (IOException exception) { throw new SourceChangedException("Eine Eingabedatei wurde entfernt, ersetzt oder ist gerade in Bearbeitung: " + path, exception); }
                    streams.Add(stream);
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                    if (actual != expected) throw new SourceChangedException("Eine Eingabedatei wurde während des Renderns geändert: " + path + ". Die bisherige PDF bleibt erhalten. Bitte erneut ausführen.");
                }
                return new(streams);
            }
            catch
            {
                foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ProgressLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new();

        public ProgressLog(string path) => _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };

        public void Write(string step, string message, object? data = null, string level = "info")
        {
            var record = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, level, step, message, data }, ProgressJsonOptions);
            lock (_gate)
            {
                Console.WriteLine(record);
                _writer.WriteLine(record);
            }
        }

        public void Dispose() => _writer.Dispose();
    }

    private sealed class SourceChangedException(string message, Exception? inner = null) : Exception(message, inner);
    private sealed record RendererResult(int ExitCode, string ErrorOutput);
    private sealed record RuntimeInfo(string EdgePath, string EdgeVersion, string CliVersion);
    private sealed record SourceLocation(int Line, int EndLine, int Column, int Offset, int Length);
    private sealed record BuildError(string Kind, string Section, string Source, string Reason, List<SourceLocation> SourceLocations, List<SourceLocation> HeadingLocations);
    private sealed record RenderCounts(int MathRendered = 0, int MathInvalid = 0, int HeadingInvalid = 0, int FigureInvalid = 0);
    private sealed record SuccessfulRender(string InputFingerprint, string OutputSha256, RenderCounts Counts);
    private sealed class BuildReport(string source, string output, bool checkOnly)
    {
        public int SchemaVersion { get; } = 1;
        public string Source { get; } = source;
        public string Output { get; } = output;
        public bool CheckOnly { get; } = checkOnly;
        public string Status { get; set; } = "error";
        public int ExitCode { get; set; } = 3;
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset CompletedAtUtc { get; set; }
        public string? InputFingerprint { get; set; }
        public string? OutputSha256 { get; set; }
        public RuntimeInfo? Runtime { get; set; }
        public RenderCounts Counts { get; set; } = new();
        public List<BuildError> Errors { get; } = [];
        public SuccessfulRender? LastSuccessfulRender { get; set; }
    }
}
