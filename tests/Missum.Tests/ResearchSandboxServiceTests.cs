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
    public async Task SelectedProjectUsesItsRootAndNeverArchivesTheUserWorkspace()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Workspace research", ChatMode.ClaudeScience);
        var workspace = Path.Combine(environment.Directory, "selected-workspace"); Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
        var project = "research-" + session.Id.ToString("N");
        var profile = new AssistantRuntimeProfile("test", "AI Assistent", _root,
            new Uri("http://127.0.0.1:8080"), 8081, 8082, Path.Combine(_root, "native"), Path.Combine(_root, "llama.exe"), Path.Combine(_root, "stack"), "test");
        using var service = new ResearchSandboxService(profile, chats);
        var layout = await service.EnsureProjectAsync(project);
        Assert.Equal(workspace, layout.RootPath);
        Assert.False(File.Exists(Path.Combine(workspace, "README.md")));
        Assert.False(File.Exists(Path.Combine(workspace, ".gitignore")));
        await service.WriteTextAsync(project, "new.py", "print('workspace')");
        Assert.True(File.Exists(Path.Combine(workspace, "work", "new.py")));
        Assert.False(Directory.Exists(Path.Combine(workspace, "Science")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ArchiveProjectAsync(project));
        Assert.True(Directory.Exists(workspace));
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
    public async Task EditsUseOneProjectTreeAndRetainOnlyHashMetadata()
    {
        var layout = await _service.EnsureProjectAsync("research-project-2");
        await _service.WriteTextAsync("research-project-2", "analysis.py", "print('before')");
        var second = await _service.WriteTextAsync("research-project-2", "analysis.py", "print('after')");

        Assert.Equal("print('after')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "analysis.py")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
        Assert.True(File.Exists(Path.Combine(layout.RunsPath, "edit-" + second.ChangeSetId + ".json")));
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RestoreChangeSetAsync("research-project-2", second.ChangeSetId));
        Assert.Contains("ohne Dateirevisionen", unavailable.Message, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "analysis.py"), "print('manual')");
        await _service.WriteTextAsync("research-project-2", "analysis.py", "print('model')");
        Assert.Equal("print('model')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "analysis.py")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
    }

    [Fact]
    public async Task PreexistingLegacyRevisionCanStillBeRestored()
    {
        var layout = await _service.EnsureProjectAsync("legacy-edit");
        var change = await _service.WriteTextAsync(layout.ProjectId, "analysis.py", "print('after')");
        var legacy = Path.Combine(layout.SnapshotsPath, change.ChangeSetId);
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "before.bin"), "print('before')");
        await File.WriteAllTextAsync(Path.Combine(legacy, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            projectId = layout.ProjectId, changeSetId = change.ChangeSetId, relativePath = change.RelativePath,
            afterSha256 = change.AfterSha256, isNewFile = false,
        }));

        await _service.RestoreChangeSetAsync(layout.ProjectId, change.ChangeSetId);

        Assert.Equal("print('before')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "analysis.py")));
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
        await _service.WriteTextAsync("live-science", "measured_dependency.py", "VALUE = 'isolated sibling import works'", cancellationToken: timeout.Token);
        await _service.WriteTextAsync("live-science", "verify.py", """
            import json, socket
            import numpy as np
            import sympy as sp
            from measured_dependency import VALUE
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
                              "networkBlocked": network_blocked, "rootReadOnly": root_read_only,
                              "dependency": VALUE}))
            """, cancellationToken: timeout.Token);

        var result = await _service.RunPythonAsync("live-science", "verify.py", timeoutSeconds: 120,
            cancellationToken: timeout.Token);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("x**2 + 2*x + 1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("1.4142135623730951", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"networkBlocked\": true", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"rootReadOnly\": true", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("isolated sibling import works", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("work/measured_dependency.py", result.InputHashes!.Keys);
        var manifest = await File.ReadAllTextAsync(Path.Combine(layout.RunsPath, result.RunId + ".json"), timeout.Token);
        Assert.Contains("docker --network none", manifest, StringComparison.Ordinal);
        Assert.Contains("scriptSha256", manifest, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task CodingSubprocessImportsPreinstalledMathlibOfflineWithoutLakeOrCustomEnvironment()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_RESEARCH_SANDBOX_LIVE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await _service.WriteTextAsync("live-mathlib", "check_mathlib.py", """
            import pathlib, subprocess, tempfile
            with tempfile.TemporaryDirectory() as directory:
                source = pathlib.Path(directory) / "Proof.lean"
                source.write_text("import Mathlib\nexample (x y : ℝ) : (x+y)^2 = x^2+2*x*y+y^2 := by ring\nexample : (2 : ℝ)+2 = 4 := by norm_num\n", encoding="utf-8")
                checked = subprocess.run(["lean", str(source)], capture_output=True, text=True)
                assert checked.returncode == 0, checked.stdout + checked.stderr
                source.write_text("import Mathlib\nexample : (1 : ℝ) = 2 := by norm_num\n", encoding="utf-8")
                rejected = subprocess.run(["lean", str(source)], capture_output=True, text=True)
                assert rejected.returncode != 0, "False statement unexpectedly accepted"
                print("Mathlib imports and tactics work offline; false statement rejected")
            """, cancellationToken: timeout.Token);
        var result = await _service.RunPythonAsync("live-mathlib", "check_mathlib.py", timeoutSeconds: 120,
            cancellationToken: timeout.Token);
        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Contains("Mathlib imports and tactics work offline; false statement rejected", result.StandardOutput, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
