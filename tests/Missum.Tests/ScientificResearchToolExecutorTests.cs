using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Coding;

namespace Missum.Tests;

public sealed class ScientificResearchToolExecutorTests : IAsyncLifetime
{
    private static readonly string[] EchoArguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('SCIENTIFIC_OK')"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-scientific-tools-" + Guid.NewGuid().ToString("N"));
    private ScientificResearchToolExecutor _executor = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _executor = new ScientificResearchToolExecutor(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WriteAndRestoreCreatedFileUsesHashBoundChangeSet()
    {
        var written = await Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "proof-1", path = "src/candidate.py", content = "print(42)\n" });
        var changeSetId = written.GetProperty("changeSetId").GetString()!;
        var path = Path.Combine(_root, ".assistant", "research", "proof-1", "src", "candidate.py");
        Assert.Equal("print(42)\n", await File.ReadAllTextAsync(path));

        var restored = await Execute(ClientToolNames.ResearchCodeRestore, new { projectId = "proof-1", changeSetId });

        Assert.True(restored.GetProperty("success").GetBoolean());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ExistingFileRequiresCurrentHashAndRestoresOriginalBytes()
    {
        var root = Path.Combine(_root, ".assistant", "research", "replication");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "model.py");
        var original = Encoding.UTF8.GetBytes("value = 1\n");
        await File.WriteAllBytesAsync(path, original);
        var hash = Convert.ToHexStringLower(SHA256.HashData(original));
        var written = await Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "replication", path = "model.py", content = "value = 2\n", expectedSha256 = hash });

        await Execute(ClientToolNames.ResearchCodeRestore,
            new { projectId = "replication", changeSetId = written.GetProperty("changeSetId").GetString() });

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RestoreDoesNotOverwriteManualChangesAndCreatesRecoveryBundle()
    {
        var written = await Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "manual-edit", path = "result.txt", content = "AI result" });
        var path = Path.Combine(_root, ".assistant", "research", "manual-edit", "result.txt");
        await File.WriteAllTextAsync(path, "manual result");

        var restored = await Execute(ClientToolNames.ResearchCodeRestore,
            new { projectId = "manual-edit", changeSetId = written.GetProperty("changeSetId").GetString() });

        Assert.False(restored.GetProperty("success").GetBoolean());
        Assert.True(restored.GetProperty("conflict").GetBoolean());
        Assert.Equal("manual result", await File.ReadAllTextAsync(path));
        Assert.True(Directory.Exists(Path.Combine(_root, ".assistant", "research", "manual-edit", ".state", "recovery",
            restored.GetProperty("recoveryId").GetString()!)));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(".git/config")]
    [InlineData(".state/changesets/forged/manifest.json")]
    public async Task WriteRejectsPathsOutsideResearchProject(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "safe", path, content = "forbidden" }));
    }

    [Fact]
    public async Task ResearchProjectIdentifierCannotCollapseItsStorageRoot()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "..", path = "escape.txt", content = "forbidden" }));
    }

    [Fact]
    public async Task ResearchExecutionWritesReproducibleManifest()
    {
        var result = await Execute(ClientToolNames.ResearchCodeExecute, new
        {
            projectId = "experiment",
            experimentId = "echo-1",
            executable = "powershell.exe",
            arguments = EchoArguments,
            timeoutSeconds = 30,
        });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("WindowsJobObjectProcessTree", result.GetProperty("processIsolation").GetString());
        Assert.Equal("NotEnforced", result.GetProperty("networkIsolation").GetString());
        var manifest = Path.Combine(_root, result.GetProperty("manifestPath").GetString()!);
        Assert.True(File.Exists(manifest));
        Assert.Contains("SCIENTIFIC_OK", await File.ReadAllTextAsync(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolicAndNumericAdaptersExecuteRealLocalToolchainsWhenAvailable()
    {
        var symbolic = await Execute(ClientToolNames.MathSymbolic,
            new { projectId = "math", expression = "(x + 1)**2", operation = "expand", symbol = "x", timeoutSeconds = 30 });
        var numeric = await Execute(ClientToolNames.MathNumeric,
            new { projectId = "math", expression = "sqrt(2)", precision = 60, timeoutSeconds = 30 });

        Assert.True(symbolic.GetProperty("success").GetBoolean());
        Assert.Contains("x**2 + 2*x + 1", symbolic.GetProperty("runs")[0].GetProperty("stdout").GetString(), StringComparison.Ordinal);
        Assert.True(numeric.GetProperty("success").GetBoolean());
        Assert.Contains("1.414213562373095", numeric.GetProperty("runs")[0].GetProperty("stdout").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeanAdapterOnlyMarksSuccessfulKernelRunAsFormalVerification()
    {
        var result = await Execute(ClientToolNames.MathFormalProof,
            new { projectId = "formal", source = "example : True := by\n  trivial\n", useMathlib = false, timeoutSeconds = 30 });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("KernelAccepted", result.GetProperty("formalVerification").GetString());
        Assert.Equal("passed", result.GetProperty("axiomAudit").GetString());
        Assert.NotEmpty(result.GetProperty("proofs").EnumerateArray());
        var preparation = result.GetProperty("preparation");
        Assert.True(preparation.GetProperty("prepared").GetBoolean());
        Assert.Equal("NotEstablished", preparation.GetProperty("formalVerification").GetString());
        Assert.Equal(preparation.GetProperty("checkedSource").GetString(), result.GetProperty("checkedSource").GetString());
        Assert.Equal(preparation.GetProperty("checkedSourceSha256").GetString(), result.GetProperty("checkedSourceSha256").GetString());
    }

    [Fact]
    public async Task LeanInitializationDoesNotClaimAProofAndExistingSourcesCanBeChecked()
    {
        var initialized = await Execute(ClientToolNames.MathFormalProof,
            new { projectId = "existing-proof", operation = "initialize", useMathlib = false, timeoutSeconds = 30 });
        Assert.True(initialized.GetProperty("success").GetBoolean());
        Assert.Equal("NotEstablished", initialized.GetProperty("formalVerification").GetString());
        var written = await Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "existing-proof", path = "lean/Identity.lean", content = "theorem identity (n : Nat) : n = n := by rfl\n" });
        Assert.True(written.GetProperty("success").GetBoolean());
        var checkedProof = await Execute(ClientToolNames.MathFormalProof,
            new { projectId = "existing-proof", path = "lean/Identity.lean", useMathlib = false, timeoutSeconds = 30 });
        Assert.True(checkedProof.GetProperty("success").GetBoolean());
        Assert.Equal("KernelAccepted", checkedProof.GetProperty("formalVerification").GetString());
        Assert.Equal("lean/Identity.lean", checkedProof.GetProperty("source").GetString());
    }

    [Fact]
    public async Task LeanDefinitionsAloneDoNotEstablishAMathematicalProof()
    {
        var result = await Execute(ClientToolNames.MathFormalProof,
            new { projectId = "no-theorem", source = "def value : Nat := 42\n", useMathlib = false, timeoutSeconds = 30 });
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("NotEstablished", result.GetProperty("formalVerification").GetString());
    }

    [Fact]
    public async Task LeanUnicodeCompilerFailuresKeepAReadableStructuredReceipt()
    {
        var result = await Execute(ClientToolNames.MathFormalProof,
            new { projectId = "unicode-failure", source = "theorem unmöglich (δ : Nat) : δ = δ + 1 := by rfl\n", useMathlib = false, timeoutSeconds = 30 });
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("NotEstablished", result.GetProperty("formalVerification").GetString());
        Assert.NotEmpty(result.GetProperty("diagnostics").EnumerateArray());
        Assert.Contains(result.GetProperty("diagnostics").EnumerateArray(), line => line.GetString()!.Contains('δ'));
        Assert.True(result.GetProperty("exitCode").GetInt32() != 0);
        Assert.True(File.Exists(Path.Combine(_root, ".assistant", "research", "unicode-failure", result.GetProperty("receipt").GetString()!)));
    }

    [Fact]
    public async Task LeanExistingCrLfAndBomSourceIsBoundToItsExactOriginalBytes()
    {
        const string projectId = "crlf-bom-proof";
        var initialized = await Execute(ClientToolNames.MathFormalProof,
            new { projectId, operation = "initialize", useMathlib = false, timeoutSeconds = 30 });
        Assert.True(initialized.GetProperty("success").GetBoolean());
        const string source = "import Std\r\ntheorem identity (δ : Nat) : δ = δ := by rfl\r\n";
        var originalBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(source)).ToArray();
        var path = Path.Combine(_root, ".assistant", "research", projectId, "lean", "Identity.lean");
        await File.WriteAllBytesAsync(path, originalBytes);

        var result = await Execute(ClientToolNames.MathFormalProof,
            new { projectId, path = "lean/Identity.lean", useMathlib = false, timeoutSeconds = 30 });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("KernelAccepted", result.GetProperty("formalVerification").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(originalBytes)), result.GetProperty("sourceFileSha256").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), result.GetProperty("sourceSha256").GetString());
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("../escape.lean")]
    [InlineData("C:/proof.lean")]
    [InlineData(".git/unsafe.lean")]
    [InlineData("proof.txt")]
    public void LeanExistingSourceMustStayInTheProject(string path) => Assert.Throws<UnauthorizedAccessException>(() =>
        ScientificResearchToolExecutor.ValidateArguments(ClientToolNames.MathFormalProof,
            JsonSerializer.SerializeToElement(new { projectId = "source-bound", path })));

    [Theory]
    [InlineData("import Mathlib\nexample (x : ℝ) : x + 0 = x := by simp")]
    [InlineData("import Std\ntheorem identity (n : Nat) : n = n := by rfl")]
    [InlineData("import Mathlib\r\nexample (x : ℝ) : x + 0 = x := by simp\r\n")]
    public void LeanAllowsOnlyInstalledTrustedImports(string source) => LeanProofSourceContract.Validate(source);

    [Theory]
    [InlineData("import LocalUnchecked\nexample : True := by trivial")]
    [InlineData("import Lean\nexample : True := by trivial")]
    [InlineData("theorem forged : False := by native_decide")]
    [InlineData("axiom assumed : False\ntheorem forged : False := assumed")]
    public void LeanRejectsUntrustedImportsAndProofTrustBypasses(string source) =>
        Assert.Throws<InvalidDataException>(() => LeanProofSourceContract.Validate(source));

    [Fact]
    public void LeanHashRejectionExplainsRemovalRatherThanAddingCompilerDirectives()
    {
        var error = Assert.Throws<InvalidDataException>(() => LeanProofSourceContract.Validate("import Mathlib\n/- # Heading -/\ntheorem identity (n : Nat) : n = n := by rfl"));
        Assert.Contains("Entferne jedes #-Zeichen", error.Message);
        Assert.Contains("Füge KEIN #check", error.Message);
    }

    private Task<JsonElement> Execute(string name, object arguments) =>
        _executor.ExecuteAsync(name, JsonSerializer.SerializeToElement(arguments));

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
}
