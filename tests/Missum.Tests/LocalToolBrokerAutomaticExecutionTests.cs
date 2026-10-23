using System.Globalization;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class LocalToolBrokerAutomaticExecutionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-auto-tools-" + Guid.NewGuid().ToString("N"));
    // These tests exercise only Coding branches, with the production broker and no UI or authorization callback.
    private readonly LocalToolBroker _broker = new(null!, null!, null!, null!);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "probe.ps1"), """
            param([int]$ExitCode = 0)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'executed.txt'), 'EXECUTED')
            [Console]::Write('AUTO_STDOUT Grüße € 日本語')
            [Console]::Error.Write('AUTO_STDERR')
            exit $ExitCode
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ActualPowerShellRunsAutomaticallyAndKeepsTheRealExitStatus(int exitCode)
    {
        var result = await _broker.ExecuteAsync(Command(exitCode), Guid.NewGuid(), null, _root);

        Assert.Equal(exitCode == 0 ? "completed" : "failed", result.Status);
        Assert.Equal(exitCode, result.Result.GetProperty("exitCode").GetInt32());
        Assert.Equal("AUTO_STDOUT Grüße € 日本語", result.Result.GetProperty("stdout").GetString());
        Assert.Equal("AUTO_STDERR", result.Result.GetProperty("stderr").GetString());
        Assert.Equal("EXECUTED", await File.ReadAllTextAsync(Path.Combine(_root, "executed.txt")));
    }

    [Fact]
    public async Task FileWriteAndReadExecuteAutomaticallyInTheSelectedProject()
    {
        var written = await _broker.ExecuteAsync(Create(ClientToolNames.CodingWrite, ToolRiskClass.LocalMutation,
            new { path = "automatic.txt", content = "Tatsächlich gespeichert.\n" }), Guid.NewGuid(), null, _root);
        var read = await _broker.ExecuteAsync(Create(ClientToolNames.CodingRead, ToolRiskClass.ReadOnly,
            new { path = "automatic.txt" }), Guid.NewGuid(), null, _root);

        Assert.Equal("completed", written.Status);
        Assert.True(written.Result.GetProperty("applied").GetBoolean());
        Assert.Equal("completed", read.Status);
        Assert.False(written.Result.TryGetProperty("sha256", out _));
        Assert.False(read.Result.TryGetProperty("sha256", out _));
        Assert.Equal("Tatsächlich gespeichert.\n", await File.ReadAllTextAsync(Path.Combine(_root, "automatic.txt")));
    }

    [Theory]
    [InlineData(ClientToolNames.CodingWrite)]
    [InlineData(ClientToolNames.CodingEdit)]
    [InlineData(ClientToolNames.CodingCommand)]
    public async Task ProjectMutationsDoNotCreateUnrequestedReadmeOrGitignore(string tool)
    {
        var result = await MutateProjectAsync(tool);

        Assert.Equal("completed", result.Status);
        Assert.Equal(tool == ClientToolNames.CodingCommand ? "before\n" : "after\n",
            await File.ReadAllTextAsync(Path.Combine(_root, "source.txt")));
        if (tool == ClientToolNames.CodingCommand)
            Assert.Equal("EXECUTED", await File.ReadAllTextAsync(Path.Combine(_root, "executed.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "README.md")));
        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
        Assert.False(result.Result.TryGetProperty("projectRepositoryFiles", out _));
    }

    [Theory]
    [InlineData(ClientToolNames.CodingWrite)]
    [InlineData(ClientToolNames.CodingEdit)]
    [InlineData(ClientToolNames.CodingCommand)]
    public async Task ProjectMutationsPreserveAuthoredReadmeAndGitignoreBytes(string tool)
    {
        var readme = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# Eigene Dokumentation\r\n\r\nGrüße und projektbezogene Befehle.\r\n")).ToArray();
        var ignore = Encoding.UTF8.GetBytes("# Eigene Ausschlüsse\r\n/custom.cache\r\n!wichtige-daten.csv\r\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "README.md"), readme);
        await File.WriteAllBytesAsync(Path.Combine(_root, ".gitignore"), ignore);

        var result = await MutateProjectAsync(tool);

        Assert.Equal("completed", result.Status);
        Assert.Equal(readme, await File.ReadAllBytesAsync(Path.Combine(_root, "README.md")));
        Assert.Equal(ignore, await File.ReadAllBytesAsync(Path.Combine(_root, ".gitignore")));
        Assert.False(result.Result.TryGetProperty("projectRepositoryFiles", out _));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("risk")]
    [InlineData("missing-workspace")]
    [InlineData("outside-workspace")]
    [InlineData("invalid-arguments")]
    public async Task AutomaticAuthorizationStillRejectsInvalidCommandsBeforeAnyExecution(string failure)
    {
        var proposal = Command(0);
        if (failure == "expired") proposal = proposal with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        if (failure == "risk") proposal = proposal with { RiskClass = ToolRiskClass.ReadOnly };
        if (failure == "outside-workspace") proposal = proposal with { Arguments = JsonSerializer.SerializeToElement(new
        {
            executable = "powershell.exe", arguments = Array.Empty<string>(), workingDirectory = "..",
        }) };
        if (failure == "invalid-arguments") proposal = proposal with { Arguments = JsonSerializer.SerializeToElement(new
        {
            executable = "powershell.exe", arguments = "-File probe.ps1",
        }) };

        var result = await _broker.ExecuteAsync(proposal, Guid.NewGuid(), null, failure == "missing-workspace" ? null : _root);

        Assert.Equal("failed", result.Status);
        Assert.Equal("client.tool_failed", result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "executed.txt")));
    }

    [Fact]
    public async Task ACancelledCommandIsNeverDispatched()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _broker.ExecuteAsync(Command(0),
            Guid.NewGuid(), null, _root, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_root, "executed.txt")));
    }

    private async Task<ClientToolResult> MutateProjectAsync(string tool)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "source.txt"), "before\n");
        var proposal = tool switch
        {
            ClientToolNames.CodingWrite => Create(tool, ToolRiskClass.LocalMutation,
                new { path = "source.txt", content = "after\n" }),
            ClientToolNames.CodingEdit => Create(tool, ToolRiskClass.LocalMutation,
                new { path = "source.txt", oldText = "before", newText = "after" }),
            _ => Command(0),
        };
        return await _broker.ExecuteAsync(proposal, Guid.NewGuid(), null, _root);
    }

    private static ToolProposal Command(int exitCode) => Create(ClientToolNames.CodingCommand, ToolRiskClass.Process,
        new { executable = "powershell.exe", arguments = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", "probe.ps1", "-ExitCode", exitCode.ToString(CultureInfo.InvariantCulture) },
            workingDirectory = ".", timeoutSeconds = 15 });

    private static ToolProposal Create(string name, ToolRiskClass riskClass, object arguments) => new(
        "proposal-auto-" + Guid.NewGuid().ToString("N"), "run-auto-test", name, JsonSerializer.SerializeToElement(arguments),
        riskClass, "Lokales Testwerkzeug automatisch ausführen", DateTimeOffset.UtcNow.AddMinutes(5));

    public Task DisposeAsync()
    {
        var root = Path.GetFullPath(_root);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("missum-auto-tools-", StringComparison.Ordinal)
            || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Invalid automatic-execution test cleanup path.");
        Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }
}
