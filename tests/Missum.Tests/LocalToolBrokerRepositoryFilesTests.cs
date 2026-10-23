using System.Diagnostics;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class LocalToolBrokerRepositoryFilesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-broker-repository-" + Guid.NewGuid().ToString("N"));
    private readonly LocalToolBroker _broker = new(null!, null!, null!, null!);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "source.txt"), "before\n");
    }

    [Theory]
    [InlineData("coding.write")]
    [InlineData("coding.edit")]
    public async Task SuccessfulFileMutationHealsMissingReadmeAndIgnore(string tool)
    {
        object arguments = tool == ClientToolNames.CodingWrite ? new { path = "source.txt", content = "after\n" }
            : new { path = "source.txt", oldText = "before", newText = "after" };
        var result = await ExecuteAsync(tool, ToolRiskClass.LocalMutation, arguments);

        Assert.Equal("completed", result.Status);
        Assert.True(result.Result.GetProperty("applied").GetBoolean());
        Assert.Equal("after\n", await File.ReadAllTextAsync(Path.Combine(_root, "source.txt")));
        Assert.True(result.Result.GetProperty("projectRepositoryFiles").GetProperty("readmeCreated").GetBoolean());
        Assert.True(result.Result.GetProperty("projectRepositoryFiles").GetProperty("readmeNeedsContent").GetBoolean());
        Assert.Contains("/.publication-renderer/", await File.ReadAllTextAsync(Path.Combine(_root, ".gitignore")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "README.md")));
    }

    [Fact]
    public async Task EditingIgnoreKeepsAuthoredRulesAndRestoresLocalRuntimeExclusions()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "authored.tmp\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# Authored description\n");

        var result = await ExecuteAsync(ClientToolNames.CodingEdit, ToolRiskClass.LocalMutation,
            new { path = ".gitignore", oldText = "authored.tmp", newText = "custom.tmp" });

        Assert.Equal("completed", result.Status);
        var ignore = await File.ReadAllTextAsync(Path.Combine(_root, ".gitignore"));
        Assert.StartsWith("custom.tmp\n", ignore, StringComparison.Ordinal);
        Assert.Contains("/Publikation-erzeugen.ps1", ignore, StringComparison.Ordinal);
        Assert.Equal("# Authored description\n", await File.ReadAllTextAsync(Path.Combine(_root, "README.md")));
    }

    [Theory]
    [InlineData("coding.read")]
    [InlineData("coding.list")]
    [InlineData("coding.search")]
    [InlineData("coding.gitDiff")]
    public async Task ReadonlyFileToolsNeverCreateRepositoryFiles(string tool)
    {
        object arguments = tool switch
        {
            ClientToolNames.CodingRead => new { path = "source.txt" },
            ClientToolNames.CodingSearch => new { query = "before" },
            _ => new { path = "." },
        };
        var result = await ExecuteAsync(tool, ToolRiskClass.ReadOnly, arguments);

        Assert.False(File.Exists(Path.Combine(_root, "README.md")));
        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.False(result.Result.TryGetProperty("projectRepositoryFiles", out _));
    }

    [Fact]
    public async Task ReadonlyGitStatusDoesNotCreateRepositoryFiles()
    {
        await GitAsync("init", "--quiet");
        var result = await ExecuteCommandAsync("git", ["status", "--short"]);

        Assert.Equal("completed", result.Status);
        Assert.False(File.Exists(Path.Combine(_root, "README.md")));
        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.False(result.Result.TryGetProperty("projectRepositoryFiles", out _));
    }

    [Fact]
    public async Task FailedFileEditDoesNotCreateRepositoryFiles()
    {
        var result = await ExecuteAsync(ClientToolNames.CodingEdit, ToolRiskClass.LocalMutation,
            new { path = "source.txt", oldText = "not present", newText = "after" });

        Assert.Equal("failed", result.Status);
        Assert.Equal("client.tool_failed", result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "README.md")));
        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
    }

    [Fact]
    public async Task MetadataFailureDoesNotTurnCommittedWriteIntoFailedMutation()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gitignore"));
        var result = await ExecuteAsync(ClientToolNames.CodingWrite, ToolRiskClass.LocalMutation,
            new { path = "source.txt", content = "saved once\n" });

        Assert.Equal("completed", result.Status);
        Assert.True(result.Result.GetProperty("success").GetBoolean());
        Assert.True(result.Result.GetProperty("applied").GetBoolean());
        Assert.Equal("saved once\n", await File.ReadAllTextAsync(Path.Combine(_root, "source.txt")));
        var maintenance = result.Result.GetProperty("projectRepositoryFiles");
        Assert.False(maintenance.GetProperty("success").GetBoolean());
        Assert.Equal("project.repository_files_maintenance_failed", maintenance.GetProperty("warning").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ShellChangesAreMaintainedEvenAfterNonzeroExitAndFromChildDirectory(int exitCode)
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        var result = await ExecuteCommandAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "[IO.File]::WriteAllText((Join-Path (Split-Path -Parent (Get-Location).Path) 'from-child.txt'), 'saved'); exit " + exitCode], "child");

        Assert.Equal(exitCode == 0 ? "completed" : "failed", result.Status);
        Assert.Equal(exitCode, result.Result.GetProperty("exitCode").GetInt32());
        Assert.Equal("saved", await File.ReadAllTextAsync(Path.Combine(_root, "from-child.txt")));
        Assert.True(result.Result.GetProperty("projectRepositoryFiles").GetProperty("readmeCreated").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(_root, "child", ".gitignore")));
    }

    [Fact]
    public async Task GitInternalWritesDoNotTriggerRepositoryMaintenance()
    {
        var result = await ExecuteCommandAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "$null = [IO.Directory]::CreateDirectory('.git'); [IO.File]::WriteAllText('.git/index', 'internal')"]);

        Assert.Equal("completed", result.Status);
        Assert.False(File.Exists(Path.Combine(_root, "README.md")));
        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.False(result.Result.TryGetProperty("projectRepositoryFiles", out _));
    }

    [Fact]
    public async Task GitUndoMaintainsFilesAfterSuccessfulRestore()
    {
        await GitAsync("init", "--quiet");
        await GitAsync("add", "source.txt");
        await GitAsync("-c", "user.name=Missum Test", "-c", "user.email=test@invalid.example", "commit", "--quiet", "-m", "Initial");
        await File.WriteAllTextAsync(Path.Combine(_root, "source.txt"), "modified\n");

        var result = await ExecuteAsync(ClientToolNames.CodingUndo, ToolRiskClass.LocalMutation, new { path = "source.txt" });

        Assert.Equal("completed", result.Status);
        Assert.Equal("before\n", await File.ReadAllTextAsync(Path.Combine(_root, "source.txt")));
        Assert.True(result.Result.GetProperty("projectRepositoryFiles").GetProperty("readmeCreated").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitAddProtectsLocalPdfRuntimeBeforeStaging(bool shell)
    {
        await GitAsync("init", "--quiet");
        Directory.CreateDirectory(Path.Combine(_root, ".publication-renderer"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".publication-renderer", "runtime.dll"), "local runtime");
        await File.WriteAllTextAsync(Path.Combine(_root, "Publikation-erzeugen.ps1"), "local launcher");
        await File.WriteAllTextAsync(Path.Combine(_root, "PDF-ERZEUGEN.md"), "local guide");

        var result = shell ? await ExecuteCommandAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "git add ."])
            : await ExecuteCommandAsync("git", ["add", "."]);

        Assert.Equal("completed", result.Status);
        var staged = await GitAsync("diff", "--cached", "--name-only");
        Assert.Contains("source.txt", staged, StringComparison.Ordinal);
        Assert.Contains("README.md", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.dll", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("Publikation-erzeugen.ps1", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("PDF-ERZEUGEN.md", staged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitAddDoesNotExecuteWhenIgnoreCannotBeRepaired()
    {
        await GitAsync("init", "--quiet");
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "# >>> Missum managed local files >>>\nmalformed\n");

        var result = await ExecuteCommandAsync("git", ["add", "."]);

        Assert.Equal("failed", result.Status);
        Assert.False(result.Result.GetProperty("executed").GetBoolean());
        Assert.Equal("project.repository_files_preflight_failed",
            result.Result.GetProperty("projectRepositoryFiles").GetProperty("warning").GetProperty("code").GetString());
        Assert.Empty(await GitAsync("diff", "--cached", "--name-only"));
    }

    [Theory]
    [InlineData("git-C")]
    [InlineData("child-cwd")]
    [InlineData("shell-C")]
    public async Task GitAddProtectsTheActualNestedWorktree(string route)
    {
        var child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);
        await GitAtAsync(child, "init", "--quiet");
        await File.WriteAllTextAsync(Path.Combine(child, "nested.txt"), "project source");
        Directory.CreateDirectory(Path.Combine(child, ".publication-renderer"));
        await File.WriteAllTextAsync(Path.Combine(child, ".publication-renderer", "runtime.dll"), "local runtime");
        await File.WriteAllTextAsync(Path.Combine(child, "Publikation-erzeugen.ps1"), "local launcher");

        var result = route switch
        {
            "git-C" => await ExecuteCommandAsync("git", ["-C", "child", "add", "."]),
            "child-cwd" => await ExecuteCommandAsync("git", ["add", "."], "child"),
            _ => await ExecuteCommandAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "git -C child add ."]),
        };

        Assert.Equal("completed", result.Status);
        Assert.True(File.Exists(Path.Combine(child, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.Equal(child, result.Result.GetProperty("projectRepositoryFiles").GetProperty("workspacePath").GetString());
        var staged = await GitAtAsync(child, "diff", "--cached", "--name-only");
        Assert.Contains("nested.txt", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.dll", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("Publikation-erzeugen.ps1", staged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C")]
    [InlineData("work-tree")]
    public async Task GitAddCannotRedirectIntoAnExternalWorktree(string route)
    {
        var workspace = Path.Combine(_root, "selected");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        await GitAtAsync(outside, "init", "--quiet");
        await File.WriteAllTextAsync(Path.Combine(outside, "external.txt"), "untouched");

        string[] arguments = route == "C" ? ["-C", outside, "add", "."]
            : ["--git-dir=" + Path.Combine(outside, ".git"), "--work-tree=" + outside, "add", "."];
        var result = await ExecuteCommandAsync("git", arguments, workspacePath: workspace);

        Assert.Equal("failed", result.Status);
        Assert.False(result.Result.GetProperty("executed").GetBoolean());
        Assert.Contains("außerhalb", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(await GitAtAsync(outside, "diff", "--cached", "--name-only"));
        Assert.False(File.Exists(Path.Combine(outside, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(workspace, ".gitignore")));
    }

    [Fact]
    public async Task AmbiguousShellPublishDoesNotExecuteAnyPart()
    {
        await GitAsync("init", "--quiet");
        var result = await ExecuteCommandAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Set-Content marker.txt changed; git add ."]);

        Assert.Equal("failed", result.Status);
        Assert.False(result.Result.GetProperty("executed").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_root, "marker.txt")));
        Assert.Empty(await GitAsync("diff", "--cached", "--name-only"));
    }

    private Task<ClientToolResult> ExecuteCommandAsync(string executable, string[] arguments, string workingDirectory = ".", string? workspacePath = null) =>
        ExecuteAsync(ClientToolNames.CodingCommand, ToolRiskClass.Process, new { executable, arguments, workingDirectory, timeoutSeconds = 15 }, workspacePath);

    private Task<ClientToolResult> ExecuteAsync(string name, ToolRiskClass risk, object arguments, string? workspacePath = null) => _broker.ExecuteAsync(
        new("repository-" + Guid.NewGuid().ToString("N"), "repository-test", name, JsonSerializer.SerializeToElement(arguments),
            risk, "Projektdateien prüfen", DateTimeOffset.UtcNow.AddMinutes(5)), Guid.NewGuid(), null, workspacePath ?? _root);

    private Task<string> GitAsync(params string[] arguments) => GitAtAsync(_root, arguments);

    private static async Task<string> GitAtAsync(string directory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = directory, CreateNoWindow = true,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        if (arguments.FirstOrDefault() == "init") await GitAtAsync(directory, "config", "core.autocrlf", "false");
        return await stdout;
    }

    public Task DisposeAsync()
    {
        var root = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("missum-broker-repository-", StringComparison.Ordinal)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Invalid repository-files broker cleanup path.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (!Path.GetFullPath(entry).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Invalid repository-files broker cleanup entry.");
            File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }
}
