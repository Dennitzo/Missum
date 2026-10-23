using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;
using Missum.Core.Coding;

namespace Missum.Tests;

public sealed class SciencePublicationToolLiveTests
{
    private static readonly string[] CheckArguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "Publikation-erzeugen.ps1", "-CheckOnly"];
    [Fact]
    [Trait("Category", "Live")]
    public async Task ShippedToolInstallsAndRendersFromAnotherWorkingDirectoryWithoutSdk()
    {
        var package = Environment.GetEnvironmentVariable("MISSUM_PUBLICATION_TOOL_PACKAGE");
        if (string.IsNullOrWhiteSpace(package)) return;
        var project = Path.Combine(Path.GetTempPath(), "missum-publication-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            await SciencePublicationScaffold.EnsureFromPackageAsync(ChatMode.ClaudeScience, project, package);
            var script = Path.Combine(project, "Publikation-erzeugen.ps1");
            Assert.True(File.Exists(script));
            Assert.True(File.Exists(Path.Combine(project, ".publication-renderer", "Missum.Publication.Cli.exe")));
            const string markdown = "# Publikationsprüfung\n\n## Modell\n\nDie Gleichung $E=mc^2$ und $$\\int_0^1 x^2\\,dx=\\frac13.$$\n\n## Literatur und Daten\n\nTest des mitgelieferten Werkzeugs.\n";
            var source = Path.Combine(project, "Publikation.md");
            await File.WriteAllTextAsync(source, markdown, new UTF8Encoding(false));
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            Assert.Equal(markdown, await File.ReadAllTextAsync(source));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(project, "publication-build.json")));
            Assert.Equal("success", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, report.RootElement.GetProperty("counts").GetProperty("mathRendered").GetInt32());
            Assert.Empty(report.RootElement.GetProperty("errors").EnumerateArray());
            Assert.True(new FileInfo(Path.Combine(project, "Publikation.pdf")).Length > 1024);
            var executor = new LocalCodingToolExecutor(project);
            var checkedByTool = await executor.ExecuteAsync("coding.command", JsonSerializer.SerializeToElement(new
            {
                executable = "powershell.exe", workingDirectory = ".", timeoutSeconds = 120,
                arguments = CheckArguments,
            }), timeout.Token);
            Assert.True(checkedByTool.GetProperty("success").GetBoolean(), checkedByTool.GetRawText());
            Assert.Equal(markdown, await File.ReadAllTextAsync(source));
        }
        finally { Directory.Delete(project, recursive: true); }
    }
}
