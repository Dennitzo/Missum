using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class LeanToolIntegrationTests
{
    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    [Trait("Category", "Live")]
    public async Task EveryProjectModeInitializesChecksAndRechecksRealMathlibProofs(ChatMode mode)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_SCIENCE_TOOLS_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Lean project integration", mode);
        var workspace = Path.Combine(environment.Directory, "any-project");
        Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
        var projectId = "research-" + session.Id.ToString("N");
        var profile = new AssistantRuntimeProfile("test", "AI Assistent", environment.Directory,
            new Uri("http://127.0.0.1:8080"), 8081, 8082, Path.Combine(environment.Directory, "native"),
            Path.Combine(environment.Directory, "llama.exe"), Path.Combine(environment.Directory, "stack"), "test");
        using var sandbox = new ResearchSandboxService(profile, chats);
        var broker = new LocalToolBroker(null!, null!, null!, chats, researchSandbox: sandbox);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        async Task<ClientToolResult> Execute(object args) => await broker.ExecuteAsync(new(
            "lean-check-" + Guid.NewGuid().ToString("N"), "lean-integration", ClientToolNames.MathFormalProof,
            JsonSerializer.SerializeToElement(args), ToolRiskClass.Process, "Lean integration", DateTimeOffset.UtcNow.AddMinutes(5)),
            session.Id, null, workspace, cancellationToken: deadline.Token);
        var initialized = await Execute(new { projectId, operation = "initialize", timeoutSeconds = 90 });
        Assert.Equal("completed", initialized.Status);
        Assert.Equal("NotEstablished", initialized.Result.GetProperty("formalVerification").GetString());
        const string source = "import Mathlib\r\ntheorem kinetic_identity (m v : ℝ) : m * v ^ 2 / 2 = (1 / 2 : ℝ) * m * v ^ 2 := by ring\r\n";
        var accepted = await Execute(new { projectId, source, timeoutSeconds = 90 });
        Assert.True(accepted.Status == "completed", JsonSerializer.Serialize(accepted));
        Assert.Equal("KernelAccepted", accepted.Result.GetProperty("formalVerification").GetString());
        var receipt = accepted.Result.GetProperty("lean");
        Assert.Equal("passed", receipt.GetProperty("axiomAudit").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), receipt.GetProperty("sourceSha256").GetString());
        Assert.Equal("c5ea00351c28e24afc9f0f84379aa41082b1188f", receipt.GetProperty("mathlibRevision").GetString());
        var checkedPath = receipt.GetProperty("source").GetString()!;
        var measuredInputs = accepted.Result.GetProperty("inputHashes");
        Assert.Equal(receipt.GetProperty("sourceFileSha256").GetString(), measuredInputs.GetProperty("work/" + checkedPath).GetString());
        Assert.Equal(receipt.GetProperty("checkedSourceSha256").GetString(),
            measuredInputs.GetProperty("work/" + receipt.GetProperty("checkedSource").GetString()).GetString());
        var checkedAgain = await Execute(new { projectId, path = checkedPath, timeoutSeconds = 90 });
        Assert.Equal("KernelAccepted", checkedAgain.Result.GetProperty("formalVerification").GetString());
        var rejected = await Execute(new { projectId, source = "import Mathlib\nexample : (1 : ℝ) = 2 := by norm_num", timeoutSeconds = 90 });
        Assert.Equal("failed", rejected.Status);
        Assert.Equal("NotEstablished", rejected.Result.GetProperty("formalVerification").GetString());
        Assert.NotEmpty(rejected.Result.GetProperty("lean").GetProperty("diagnostics").EnumerateArray());
        var layout = await sandbox.EnsureProjectAsync(projectId, deadline.Token);
        Assert.True(File.Exists(Path.Combine(layout.WorkPath, checkedPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.All(accepted.Result.GetProperty("runs").EnumerateArray(), run => Assert.Equal(0, run.GetProperty("exitCode").GetInt32()));
        if (Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY") is { Length: > 0 } evidenceRoot)
        {
            Directory.CreateDirectory(evidenceRoot);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "lean-" + mode + ".json"),
                JsonSerializer.Serialize(new { initialized, accepted, checkedAgain, rejected }), deadline.Token);
        }
    }
}
