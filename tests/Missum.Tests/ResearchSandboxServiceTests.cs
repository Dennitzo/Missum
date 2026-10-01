using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ResearchSandboxServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-research-sandbox-tests", Guid.NewGuid().ToString("N"));
    private readonly ResearchSandboxService _service;

    public ResearchSandboxServiceTests()
    {
        var profile = new AssistantRuntimeProfile("test", "AI Assistent", _root,
            new Uri("http://127.0.0.1:8080"), 8081, 8082,
            Path.Combine(_root, "native"), Path.Combine(_root, "llama.exe"), Path.Combine(_root, "stack"), "test");
        _service = new ResearchSandboxService(profile);
    }

    [Fact]
    public async Task ScienceUsesSelectedWorkspaceAndMigratesExistingFilesWithRestorableArchive()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Workspace research", ChatMode.ClaudeScience);
        var workspace = Path.Combine(environment.Directory, "selected-workspace"); Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
        var project = "research-" + session.Id.ToString("N");
        var profile = new AssistantRuntimeProfile("test", "AI Assistent", _root,
            new Uri("http://127.0.0.1:8080"), 8081, 8082, Path.Combine(_root, "native"), Path.Combine(_root, "llama.exe"), Path.Combine(_root, "stack"), "test");
        await _service.WriteTextAsync(project, "existing.py", "print('preserved')");
        using var relocated = new ResearchSandboxService(profile, chats);
        var layout = await relocated.EnsureProjectAsync(project);
        Assert.Equal(Path.Combine(workspace, "Science", project), layout.RootPath);
        Assert.Equal("print('preserved')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "existing.py")));
        var change = await relocated.WriteTextAsync(project, "new.py", "print('workspace')");
        Assert.True(change.IsNewFile);
        Assert.False(File.Exists(Path.Combine(_root, "ResearchSandbox", project, "work", "new.py")));
        var archive = await relocated.ArchiveProjectAsync(project);
        Assert.StartsWith(Path.Combine(workspace, "Science", "Trash"), archive, StringComparison.OrdinalIgnoreCase);
        await relocated.RestoreProjectAsync(project);
        Assert.True(File.Exists(Path.Combine(layout.WorkPath, "new.py")));
    }

    [Fact]
    public async Task ScienceCannotSilentlyUsePrivateStorageWhenNoWorkspaceIsSelected()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Missing workspace", ChatMode.ClaudeScience);
        var profile = new AssistantRuntimeProfile("test", "AI Assistent", _root,
            new Uri("http://127.0.0.1:8080"), 8081, 8082, Path.Combine(_root, "native"), Path.Combine(_root, "llama.exe"), Path.Combine(_root, "stack"), "test");
        using var sandbox = new ResearchSandboxService(profile, chats);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.EnsureProjectAsync("research-" + session.Id.ToString("N")));
    }

    [Fact]
    public async Task CreatesSeparateProjectDirectoriesAndRejectsTraversal()
    {
        var layout = await _service.EnsureProjectAsync("research-project-1");
        Assert.True(Directory.Exists(layout.InputsPath));
        Assert.True(Directory.Exists(layout.WorkPath));
        Assert.True(Directory.Exists(layout.ArtifactsPath));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.WriteTextAsync("research-project-1", "..\\outside.py", "print(1)"));
        Assert.False(File.Exists(Path.Combine(_root, "outside.py")));
    }

    [Fact]
    public async Task ChangeSetRestoresAiEditsAndKeepsExternalEditsForManualRecovery()
    {
        var layout = await _service.EnsureProjectAsync("research-project-2");
        var first = await _service.WriteTextAsync("research-project-2", "analysis.py", "print('before')");
        var second = await _service.WriteTextAsync("research-project-2", "analysis.py", "print('after')", first.AfterSha256);

        await _service.RestoreChangeSetAsync("research-project-2", second.ChangeSetId);
        Assert.Equal("print('before')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "analysis.py")));

        var third = await _service.WriteTextAsync("research-project-2", "analysis.py", "print('model')");
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "analysis.py"), "print('manual')");
        var exception = await Assert.ThrowsAsync<IOException>(() => _service.RestoreChangeSetAsync("research-project-2", third.ChangeSetId));
        Assert.Contains("Recovery-Bundle", exception.Message);
        Assert.Equal("print('manual')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "analysis.py")));
        Assert.Contains(Directory.GetDirectories(layout.SnapshotsPath), path => Path.GetFileName(path).StartsWith("recovery-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectDeletionUsesRestorableThirtyDayArchive()
    {
        var layout = await _service.EnsureProjectAsync("research-project-3");
        await File.WriteAllTextAsync(Path.Combine(layout.ArtifactsPath, "result.txt"), "keep me");

        var archived = await _service.ArchiveProjectAsync("research-project-3");
        Assert.False(Directory.Exists(layout.RootPath));
        Assert.True(Directory.Exists(archived));
        await _service.RestoreProjectAsync("research-project-3");
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(layout.ArtifactsPath, "result.txt")));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DockerSandboxExecutesScientificPythonAndWritesAuditableRun()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_RESEARCH_SANDBOX_LIVE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var runtime = await _service.PrepareRuntimeAsync(timeout.Token);
        Assert.True(runtime.IsReady, runtime.Detail);
        var layout = await _service.EnsureProjectAsync("live-science", timeout.Token);
        await _service.WriteTextAsync("live-science", "verify.py", """
            import json, socket
            import numpy as np
            import sympy as sp
            x = sp.Symbol("x")
            value = sp.expand((x + 1) ** 2)
            network_blocked = False
            root_read_only = False
            try:
                socket.create_connection(("1.1.1.1", 53), timeout=1)
            except OSError:
                network_blocked = True
            try:
                open("/escape.txt", "w", encoding="utf-8").write("forbidden")
            except OSError:
                root_read_only = True
            print(json.dumps({"symbolic": str(value), "numeric": float(np.sqrt(2)),
                              "networkBlocked": network_blocked, "rootReadOnly": root_read_only}))
            """, cancellationToken: timeout.Token);

        var result = await _service.RunPythonAsync("live-science", "verify.py", timeoutSeconds: 120,
            cancellationToken: timeout.Token);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("x**2 + 2*x + 1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("1.4142135623730951", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"networkBlocked\": true", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"rootReadOnly\": true", result.StandardOutput, StringComparison.Ordinal);
        var manifest = await File.ReadAllTextAsync(Path.Combine(layout.RunsPath, result.RunId + ".json"), timeout.Token);
        Assert.Contains("docker --network none", manifest, StringComparison.Ordinal);
        Assert.Contains("scriptSha256", manifest, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
