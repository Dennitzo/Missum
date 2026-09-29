using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Missum.Core.Coding;

namespace Missum.Tests;

/// <summary>Exercises the actual git restore command against an isolated, committed repository.</summary>
public sealed class CodingUndoAcceptanceTests : IAsyncLifetime
{
    private const string Target = "target [one].txt";
    private const string Other = "other.txt";
    private const string Untracked = "keep-untracked.txt";
    private const string TargetBaseline = "committed target\n";
    private const string OtherBaseline = "committed other\n";
    private const string TargetStaged = "staged target\n";
    private const string OtherStaged = "staged other\n";
    private const string TargetWorking = "unstaged target\n";
    private const string OtherWorking = "unstaged other\n";
    private const string UntrackedContent = "untracked data must remain\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-undo-acceptance-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TargetedUndoRestoresOnlySelectedIndexAndWorkingFile()
    {
        var beforeHead = await GitAsync("rev-parse", "HEAD");
        Assert.Equal(TargetStaged, await GitAsync("show", ":" + Target));
        Assert.Equal(OtherStaged, await GitAsync("show", ":" + Other));
        Assert.Equal(TargetWorking, await File.ReadAllTextAsync(Path.Combine(_root, Target)));
        Assert.Equal(OtherWorking, await File.ReadAllTextAsync(Path.Combine(_root, Other)));

        var result = await new LocalCodingToolExecutor(_root).ExecuteAsync("coding.undo",
            JsonSerializer.SerializeToElement(new { path = Target }));

        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal(0, result.GetProperty("result").GetProperty("exitCode").GetInt32());
        Assert.Equal(TargetBaseline, await File.ReadAllTextAsync(Path.Combine(_root, Target)));
        Assert.Equal(TargetBaseline, await GitAsync("show", ":" + Target));
        Assert.Equal(string.Empty, await GitAsync("diff", "HEAD", "--", Target));
        Assert.Equal(OtherWorking, await File.ReadAllTextAsync(Path.Combine(_root, Other)));
        Assert.Equal(OtherStaged, await GitAsync("show", ":" + Other));
        Assert.NotEmpty(await GitAsync("diff", "--cached", "--", Other));
        Assert.NotEmpty(await GitAsync("diff", "--", Other));
        Assert.Equal(UntrackedContent, await File.ReadAllTextAsync(Path.Combine(_root, Untracked)));
        Assert.Equal(Untracked + "\n", await GitAsync("ls-files", "--others", "--exclude-standard"));
        Assert.Equal(beforeHead, await GitAsync("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task WholeWorkspaceUndoRestoresAllTrackedFilesButKeepsUntrackedBytes()
    {
        var beforeHead = await GitAsync("rev-parse", "HEAD");
        var originalUntracked = await File.ReadAllBytesAsync(Path.Combine(_root, Untracked));
        Assert.NotEmpty(await GitAsync("diff", "--cached"));
        Assert.NotEmpty(await GitAsync("diff"));

        var result = await new LocalCodingToolExecutor(_root).ExecuteAsync("coding.undo", JsonSerializer.SerializeToElement(new { path = "." }));

        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal(0, result.GetProperty("result").GetProperty("exitCode").GetInt32());
        Assert.Equal(TargetBaseline, await File.ReadAllTextAsync(Path.Combine(_root, Target)));
        Assert.Equal(OtherBaseline, await File.ReadAllTextAsync(Path.Combine(_root, Other)));
        Assert.Equal(TargetBaseline, await GitAsync("show", ":" + Target));
        Assert.Equal(OtherBaseline, await GitAsync("show", ":" + Other));
        Assert.Equal(string.Empty, await GitAsync("diff", "--cached"));
        Assert.Equal(string.Empty, await GitAsync("diff"));
        Assert.Equal(originalUntracked, await File.ReadAllBytesAsync(Path.Combine(_root, Untracked)));
        Assert.Equal("?? " + Untracked + "\n", await GitAsync("status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal(beforeHead, await GitAsync("rev-parse", "HEAD"));
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await GitAsync("init", "--quiet");
        // Repository-local settings also govern the real executor's git subprocess.
        await GitAsync("config", "--local", "core.autocrlf", "false");
        await GitAsync("config", "--local", "core.fsmonitor", "false");
        await GitAsync("config", "--local", "core.attributesFile", Path.Combine(_root, ".git", "empty-attributes"));
        await GitAsync("config", "--local", "core.hooksPath", Path.Combine(_root, ".git", "empty-hooks"));
        await WriteAsync(Target, TargetBaseline);
        await WriteAsync(Other, OtherBaseline);
        await GitAsync("add", "--", Target, Other);
        await GitAsync("-c", "user.name=Missum Acceptance", "-c", "user.email=missum-acceptance@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Isolated coding.undo baseline");
        await WriteAsync(Target, TargetStaged);
        await WriteAsync(Other, OtherStaged);
        await GitAsync("add", "--", Target, Other);
        await WriteAsync(Target, TargetWorking);
        await WriteAsync(Other, OtherWorking);
        await WriteAsync(Untracked, UntrackedContent);
    }

    private Task WriteAsync(string name, string content) => File.WriteAllTextAsync(Path.Combine(_root, name), content, new UTF8Encoding(false));

    private async Task<string> GitAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_root, ".git", "no-global-config");
        using var process = Process.Start(start) ?? throw new IOException("Git-Testprozess konnte nicht starten.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var text = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} exited {process.ExitCode}: {error}");
        return text.ReplaceLineEndings("\n");
    }

    public async Task DisposeAsync()
    {
        var root = Path.GetFullPath(_root);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        const string prefix = "missum-undo-acceptance-";
        var name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Unexpected coding.undo fixture directory.");
        var clock = Stopwatch.StartNew();
        while (Directory.Exists(root))
        {
            try
            {
                if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Linked fixture cleanup refused.");
                foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) when (clock.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(100); }
        }
    }
}
