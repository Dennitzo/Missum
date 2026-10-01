using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Coding;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Xunit.Abstractions;

namespace Missum.Tests;

public sealed class ScienceToolExecutionTests(ITestOutputHelper output)
{
    private static readonly string[] VerificationArguments = ["verify.py"];
    private const string SmtSource = """
        import json
        from z3 import Int, Solver, sat, unsat, get_version_string
        x = Int("x")
        satisfiable = Solver()
        satisfiable.add(x == 7)
        first = satisfiable.check()
        impossible = Solver()
        impossible.add(x > 4, x < 2)
        second = impossible.check()
        assert first == sat and second == unsat
        value = satisfiable.model()[x].as_long()
        assert value == 7
        print(json.dumps({"sat": str(first), "model": value, "unsat": str(second), "z3": get_version_string()}))
        """;
    private const string TestSource = """
        import unittest
        class Arithmetic(unittest.TestCase):
            def test_addition(self): self.assertEqual(2 + 3, 5)
            def test_square(self): self.assertEqual(7 * 7, 49)
        if __name__ == "__main__": unittest.main()
        """;
    private const string BenchmarkSource = """
        import json, pathlib
        counter = pathlib.Path("actual-repetitions.txt")
        value = int(counter.read_text()) + 1 if counter.exists() else 1
        counter.write_text(str(value), encoding="utf-8")
        print(json.dumps({"iteration": value, "checksum": sum(range(1000))}))
        """;
    private const string LeanProof = "example : (2 : Nat) + 2 = 4 := by\n  rfl\n";
    private const string LeanFailure = "example : False := by\n  trivial\n";

    [Theory]
    [InlineData("smt")]
    [InlineData("test")]
    [InlineData("test-failure")]
    [InlineData("benchmark")]
    public async Task CodingScienceToolsExecuteRealProcessesAndKeepReproducibleResults(string operation)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var executor = new ScientificResearchToolExecutor(environment.Directory);
        const string projectId = "execution-proof";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var name = ToolName(operation);
        JsonElement arguments;
        if (operation == "smt") arguments = JsonSerializer.SerializeToElement(new { projectId, source = SmtSource, timeoutSeconds = 60 });
        else
        {
            await executor.ExecuteAsync(ClientToolNames.ResearchCodeWrite,
                JsonSerializer.SerializeToElement(new { projectId, path = "verify.py", content = Script(operation) }), deadline.Token);
            arguments = JsonSerializer.SerializeToElement(new
            {
                projectId, experimentId = operation, executable = "python", arguments = VerificationArguments, timeoutSeconds = 60,
                // Deliberately omit repetitions: the advertised default must run three times.
            });
        }

        var result = await executor.ExecuteAsync(name, arguments, deadline.Token);

        Assert.Equal(operation != "test-failure", result.GetProperty("success").GetBoolean());
        var runs = result.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(operation == "benchmark" ? 3 : 1, runs.Length);
        AssertRunResults(operation, runs, "stdout", "stderr", "exitCode");
        var manifestPath = Path.Combine(environment.Directory, result.GetProperty("manifestPath").GetString()!);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, deadline.Token));
        Assert.Equal(name, manifest.RootElement.GetProperty("toolName").GetString());
        Assert.Equal(runs.Length, manifest.RootElement.GetProperty("repetitions").GetInt32());
        Assert.Equal(runs.Length, manifest.RootElement.GetProperty("runs").GetArrayLength());
        Assert.Equal("WindowsJobObjectProcessTree", result.GetProperty("processIsolation").GetString());
        Assert.Equal("NotEnforced", result.GetProperty("networkIsolation").GetString());
        if (operation == "benchmark")
            Assert.Equal("3", await File.ReadAllTextAsync(Path.Combine(environment.Directory, ".assistant", "research", projectId, "actual-repetitions.txt"), deadline.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CodingLeanReportsKernelAcceptanceOnlyForARealSuccessfulProof(bool valid)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var executor = new ScientificResearchToolExecutor(environment.Directory);
        var result = await executor.ExecuteAsync(ClientToolNames.MathFormalProof,
            JsonSerializer.SerializeToElement(new { projectId = "lean-check", source = valid ? LeanProof : LeanFailure, timeoutSeconds = 60 }));
        Assert.Equal(valid, result.GetProperty("success").GetBoolean());
        Assert.Equal(valid ? "KernelAccepted" : "NotEstablished", result.GetProperty("formalVerification").GetString());
        var run = Assert.Single(result.GetProperty("runs").EnumerateArray());
        Assert.Equal(valid, run.GetProperty("exitCode").GetInt32() == 0);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(environment.Directory, result.GetProperty("manifestPath").GetString()!)));
        Assert.Contains(manifest.RootElement.GetProperty("arguments").EnumerateArray(), argument => argument.GetString() == "-DwarningAsError=true");
    }

    [Theory]
    [InlineData("example : False := by sorry")]
    [InlineData("example : False := by admit")]
    [InlineData("set_option warningAsError false in example : False := by sorry")]
    [InlineData("axiom fabricated : False")]
    [InlineData("#eval System.Exit.exit 0")]
    public async Task CodingLeanRejectsAdmissionsBeforeCreatingSourceOrStartingTheCompiler(string source)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var executor = new ScientificResearchToolExecutor(environment.Directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => executor.ExecuteAsync(ClientToolNames.MathFormalProof,
            JsonSerializer.SerializeToElement(new { projectId = "rejected", source })));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, ".assistant")));
    }

    [Theory]
    [InlineData("example : False := by sorry")]
    [InlineData("example : False := by admit")]
    [InlineData("set_option warningAsError false in example : False := by sorry")]
    [InlineData("axiom fabricated : False")]
    [InlineData("#eval System.Exit.exit 0")]
    [InlineData("run_cmd pure ()")]
    [InlineData("import Unchecked")]
    public async Task ScienceBrokerRejectsAdmissionAndCompilerOverrideSourcesBeforeRunningAnything(string source)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Formal contract", ChatMode.ClaudeScience);
        using var sandbox = new ResearchSandboxService(Profile(environment.Directory), chats);
        var broker = new LocalToolBroker(null!, null!, null!, chats, researchSandbox: sandbox);
        var result = await broker.ExecuteAsync(Proposal(ClientToolNames.MathFormalProof,
            new { projectId = "research-" + session.Id.ToString("N"), source }), session.Id, null);
        Assert.Equal("failed", result.Status);
        Assert.Equal("client.tool_failed", result.ErrorCode);
        Assert.Contains("deklarative Lean", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, "ResearchSandbox")));
    }

    [Theory]
    [InlineData("smt")]
    [InlineData("test")]
    [InlineData("test-failure")]
    [InlineData("benchmark")]
    [InlineData("lean")]
    [InlineData("lean-failure")]
    [Trait("Category", "Live")]
    public async Task ScienceBrokerExecutesTheActualDockerToolAndPreservesRunEvidence(string operation)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_SCIENCE_TOOLS_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Sandbox execution proof", ChatMode.ClaudeScience);
        var workspace = Path.Combine(environment.Directory, "science-workspace"); Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
        var projectId = "research-" + session.Id.ToString("N");
        using var sandbox = new ResearchSandboxService(Profile(environment.Directory), chats);
        var broker = new LocalToolBroker(null!, null!, null!, chats, researchSandbox: sandbox);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var runtime = await sandbox.PrepareRuntimeAsync(deadline.Token);
        Assert.True(runtime.IsReady, runtime.Detail);
        object arguments;
        if (operation == "smt") arguments = new { projectId, source = SmtSource, timeoutSeconds = 60 };
        else if (operation.StartsWith("lean", StringComparison.Ordinal))
            arguments = new { projectId, source = operation == "lean" ? LeanProof : LeanFailure, timeoutSeconds = 60 };
        else
        {
            var write = await broker.ExecuteAsync(Proposal(ClientToolNames.ResearchCodeWrite,
                new { projectId, path = "verify.py", content = Script(operation) }), session.Id, null, cancellationToken: deadline.Token);
            Assert.Equal("completed", write.Status);
            arguments = new { projectId, experimentId = operation, executable = "python", arguments = VerificationArguments, timeoutSeconds = 60 };
        }

        var result = await broker.ExecuteAsync(Proposal(ToolName(operation), arguments), session.Id, null, cancellationToken: deadline.Token);

        var succeeds = operation is not ("test-failure" or "lean-failure");
        Assert.Equal(succeeds ? "completed" : "failed", result.Status);
        Assert.Equal(succeeds, result.Result.GetProperty("success").GetBoolean());
        if (!succeeds) Assert.Equal("client.scientific_tool_failed", result.ErrorCode);
        var runs = result.Result.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(operation == "benchmark" ? 3 : 1, runs.Length);
        Assert.Equal(runs.Length, result.Result.GetProperty("repetitions").GetInt32());
        AssertRunResults(operation, runs, "standardOutput", "standardError", "exitCode");
        Assert.Equal("Docker network none", result.Result.GetProperty("networkIsolation").GetString());
        var layout = await sandbox.EnsureProjectAsync(projectId, deadline.Token);
        foreach (var run in runs)
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(layout.RunsPath, run.GetProperty("runId").GetString() + ".json"), deadline.Token));
            var record = manifest.RootElement;
            Assert.Equal("docker --network none", record.GetProperty("networkIsolation").GetString());
            Assert.Equal(run.GetProperty("exitCode").GetInt32(), record.GetProperty("exitCode").GetInt32());
            var script = Path.Combine(layout.RootPath, record.GetProperty("script").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(script, deadline.Token))), record.GetProperty("scriptSha256").GetString());
        }
        if (operation == "benchmark") Assert.Equal("3", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "actual-repetitions.txt"), deadline.Token));
        if (operation.StartsWith("lean", StringComparison.Ordinal))
        {
            Assert.Equal(succeeds ? "KernelAccepted" : "NotEstablished", result.Result.GetProperty("formalVerification").GetString());
            using var verification = JsonDocument.Parse(runs[0].GetProperty("standardOutput").GetString()!);
            Assert.Equal(succeeds, verification.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("4.30.0", verification.RootElement.GetProperty("version").GetString(), StringComparison.Ordinal);
            var expectedSource = operation == "lean" ? LeanProof : LeanFailure;
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedSource))), verification.RootElement.GetProperty("sourceSha256").GetString());
        }
        output.WriteLine($"{operation}: {result.Status}; {runs.Length} actual Docker run(s); verified source hashes and persisted manifests.");
        var evidenceRoot = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            var evidence = Path.Combine(Path.GetFullPath(evidenceRoot), "science-tool-" + operation + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(result), deadline.Token);
            foreach (var file in Directory.EnumerateFiles(layout.RunsPath, "*.json")) File.Copy(file, Path.Combine(evidence, Path.GetFileName(file)));
            output.WriteLine("Evidence: " + evidence);
        }
    }

    private static void AssertRunResults(string operation, JsonElement[] runs, string stdout, string stderr, string exitCode)
    {
        if (operation == "smt")
        {
            using var proof = JsonDocument.Parse(runs[0].GetProperty(stdout).GetString()!);
            Assert.Equal("sat", proof.RootElement.GetProperty("sat").GetString());
            Assert.Equal(7, proof.RootElement.GetProperty("model").GetInt32());
            Assert.Equal("unsat", proof.RootElement.GetProperty("unsat").GetString());
            Assert.Equal(0, runs[0].GetProperty(exitCode).GetInt32());
        }
        else if (operation == "test")
        {
            Assert.Equal(0, runs[0].GetProperty(exitCode).GetInt32());
            Assert.Contains("Ran 2 tests", runs[0].GetProperty(stderr).GetString(), StringComparison.Ordinal);
            Assert.Contains("OK", runs[0].GetProperty(stderr).GetString(), StringComparison.Ordinal);
        }
        else if (operation == "test-failure") Assert.Equal(7, runs[0].GetProperty(exitCode).GetInt32());
        else if (operation == "benchmark")
            for (var index = 0; index < 3; index++)
            {
                Assert.Equal(0, runs[index].GetProperty(exitCode).GetInt32());
                using var observation = JsonDocument.Parse(runs[index].GetProperty(stdout).GetString()!);
                Assert.Equal(index + 1, observation.RootElement.GetProperty("iteration").GetInt32());
                Assert.Equal(499500, observation.RootElement.GetProperty("checksum").GetInt32());
            }
    }

    private static string Script(string operation) => operation switch
    {
        "benchmark" => BenchmarkSource,
        "test-failure" => "import sys\nprint('ACTUAL_EXPECTED_FAILURE')\nsys.exit(7)\n",
        _ => TestSource,
    };
    private static string ToolName(string operation) => operation switch
    {
        "smt" => ClientToolNames.MathSmt,
        "benchmark" => ClientToolNames.ResearchCodeBenchmark,
        "lean" or "lean-failure" => ClientToolNames.MathFormalProof,
        _ => ClientToolNames.ResearchCodeTest,
    };
    private static ToolProposal Proposal(string name, object arguments) => new(
        "science-proof-" + Guid.NewGuid().ToString("N"), "science-test-run", name, JsonSerializer.SerializeToElement(arguments),
        name == ClientToolNames.ResearchCodeWrite ? ToolRiskClass.LocalMutation : ToolRiskClass.Process,
        "Isolierte Werkzeugabnahme", DateTimeOffset.UtcNow.AddMinutes(5));
    private static AssistantRuntimeProfile Profile(string root) => new("test", "AI Assistent", root,
        new Uri("http://127.0.0.1:8080"), 8081, 8082, Path.Combine(root, "native"), Path.Combine(root, "llama.exe"), Path.Combine(root, "stack"), "test");
}
