using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Missum.App.Services;

/// <summary>
/// Creates an A4 book-layout PDF for a generated session document by using the
/// same Markdown, KaTeX and print styles as the assistant WebView.
/// </summary>
public sealed partial class DocumentPdfExporter(ILogger<DocumentPdfExporter> logger) : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".tex", ".json",
    };
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    public Task<string?> EnsureCurrentAsync(
        string sourcePath,
        bool sourceChanged,
        CancellationToken cancellationToken = default) =>
        EnsureCurrentAsync(sourcePath, sourceChanged, scientificPublication: false, cancellationToken);

    public async Task<string?> EnsureCurrentAsync(
        string sourcePath,
        bool sourceChanged,
        bool scientificPublication,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(source)) || !File.Exists(source))
        {
            return null;
        }

        var output = Path.ChangeExtension(source, ".pdf");
        if (!sourceChanged && File.Exists(output) && new FileInfo(output).Length >= 1024)
        {
            return output;
        }

        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sourceChanged && File.Exists(output) && new FileInfo(output).Length >= 1024)
            {
                return output;
            }

            var script = ApplicationAssets.ResolvePath("Assets", "Scripts", "export-document.ps1");
            var webAssets = ApplicationAssets.ResolvePath("Assets", "Web");
            if (!File.Exists(script) || !Directory.Exists(webAssets))
            {
                throw new FileNotFoundException("Die lokalen Missum-Ressourcen für den Dokument-PDF-Export fehlen.", script);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            string[] arguments =
            [
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-SourcePath", source,
                "-WebAssetsPath", webAssets,
                "-OutputPath", output,
            ];
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (scientificPublication) startInfo.ArgumentList.Add("-ScientificPublication");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Der Dokument-PDF-Export konnte nicht gestartet werden.");
            }

            // Drain both pipes even when cancellation terminates the renderer.
            var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await WaitForExportExitAsync(process, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Cancellation terminates the complete renderer tree before its streams
                // and the caller's temporary source files can be disposed.
                if (process.HasExited) await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
                throw;
            }

            var outputText = await standardOutput.ConfigureAwait(false);
            var errorText = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length < 1024)
            {
                var detail = ExportErrorDetail(errorText) ?? ExportErrorDetail(outputText) ?? "Unbekannter Exportfehler.";
                throw new InvalidOperationException($"Die Dokument-PDF konnte nicht erzeugt werden: {detail}");
            }

            LogDocumentPdfCreated(output);
            return output;
        }
        finally
        {
            _exportGate.Release();
        }
    }

    internal static string? ExportErrorDetail(string value)
    {
        // Redirected Windows PowerShell wraps its final FullyQualifiedErrorId;
        // the last line can be a lone period. Keep the actual first diagnostic
        // paragraph instead of reporting formatting metadata to the agent.
        const int maximumLength = 4096;
        var detail = new StringBuilder();
        foreach (var rawLine in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (detail.Length > 0) break;
                continue;
            }
            if (line.StartsWith("In ", StringComparison.Ordinal)
                || line.StartsWith("At ", StringComparison.Ordinal)
                || line.StartsWith('+')
                || line.StartsWith("CategoryInfo", StringComparison.Ordinal)
                || line.StartsWith("FullyQualifiedErrorId", StringComparison.Ordinal)) break;
            if (detail.Length > 0) detail.Append(' ');
            detail.Append(line.AsSpan(0, Math.Min(line.Length, maximumLength - detail.Length)));
            if (detail.Length >= maximumLength) break;
        }
        return detail.Length == 0 ? null : detail.ToString();
    }

    internal static async Task WaitForExportExitAsync(Process process, TimeSpan maximumDuration,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumDuration);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited)
            {
                // The renderer finished between the exit check and termination.
            }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Die PDF-Erzeugung hat das Zeitlimit überschritten.");
        }
    }

    public void Dispose() => _exportGate.Dispose();

    [LoggerMessage(EventId = 5820, Level = LogLevel.Information, Message = "Document PDF created at {Path}.")]
    private partial void LogDocumentPdfCreated(string path);
}
