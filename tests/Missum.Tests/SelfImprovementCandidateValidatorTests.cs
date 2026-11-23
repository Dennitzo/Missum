using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class SelfImprovementCandidateValidatorTests : IDisposable
{
    private const string CandidateId = "0123456789ABCDEF0123456789ABCDEF";
    private const string Digest = "sha256:" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-si-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _candidate;

    public SelfImprovementCandidateValidatorTests()
    {
        _workspace = Path.Combine(_root, "ws");
        _candidate = Path.Combine(_root, "jobs", CandidateId);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.Combine(_candidate, "portable"));
        Git("init", "-q");
        File.WriteAllText(Path.Combine(_workspace, "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_candidate, "portable", "Missum Labs.exe"), "exe");
        WriteEntries("source-manifest.json", _workspace, "a.cs");
        WriteEntries("portable-files.json", Path.Combine(_candidate, "portable"), "Missum Labs.exe");
        File.WriteAllText(Path.Combine(_candidate, "portable.manifest.json"),
            JsonSerializer.Serialize(new { mode = "SingleFile", executableSha256 = Hash(Path.Combine(_candidate, "portable", "Missum Labs.exe")) }));
    }

    public void Dispose()
    {
        try { foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task UnchangedCandidatePasses() => Assert.Empty(await ValidateAsync(Report()));

    [Fact]
    public async Task ChangedSourceIsRejected()
    {
        File.WriteAllText(Path.Combine(_workspace, "a.cs"), "class A { int x; }");
        Assert.Contains(await ValidateAsync(Report()), p => p.Code == "selfImprovement.source_changed");
    }

    [Fact]
    public async Task NewSourceFileIsRejected()
    {
        File.WriteAllText(Path.Combine(_workspace, "b.cs"), "class B {}");
        Assert.Contains(await ValidateAsync(Report()), p => p.Code == "selfImprovement.source_changed");
    }

    [Fact]
    public async Task ModifiedOrExtraPortableFileIsRejected()
    {
        File.WriteAllText(Path.Combine(_candidate, "portable", "extra.dll"), "x");
        Assert.Contains(await ValidateAsync(Report()), p => p.Code == "selfImprovement.portable_modified");
        File.Delete(Path.Combine(_candidate, "portable", "extra.dll"));
        File.WriteAllText(Path.Combine(_candidate, "portable", "Missum Labs.exe"), "tampered");
        var problems = await ValidateAsync(Report());
        Assert.Contains(problems, p => p.Code == "selfImprovement.portable_modified");
        Assert.Contains(problems, p => p.Code == "selfImprovement.portable_manifest_mismatch");
    }

    [Fact]
    public async Task WrongImageDigestIsRejected() =>
        Assert.Contains(await ValidateAsync(Report(), "sha256:" + new string('0', 64)), p => p.Code == "selfImprovement.image_digest_mismatch");

    [Fact]
    public async Task FailedOrMissingCheckIsRejected()
    {
        Assert.Contains(await ValidateAsync(Report(failStep: "app-tests")), p => p.Code == "selfImprovement.check_failed");
        Assert.Contains(await ValidateAsync(Report(omitStep: "gateway-smoke")), p => p.Code == "selfImprovement.check_missing");
        Assert.Contains(await ValidateAsync(Report(status: "failed")), p => p.Code == "selfImprovement.candidate_not_verified");
    }

    [Fact]
    public async Task ArtifactsOutsideCandidateAreRejected() =>
        Assert.Contains(await ValidateAsync(Report(sourceManifest: Path.Combine(_root, "elsewhere.json"))),
            p => p.Code == "selfImprovement.artifacts_invalid");

    private Task<IReadOnlyList<SelfImprovementCandidateValidator.Problem>> ValidateAsync(JsonElement report, string digest = Digest) =>
        SelfImprovementCandidateValidator.ValidateAsync(CandidateId, _candidate, _workspace, report,
            (_, _) => Task.FromResult<string?>(digest), CancellationToken.None);

    private JsonElement Report(string status = "verified", string? failStep = null, string? omitStep = null, string? sourceManifest = null) =>
        JsonSerializer.SerializeToElement(new
        {
            candidateId = CandidateId, status, phase = "complete",
            steps = SelfImprovementCandidateValidator.RequiredSteps.Where(s => s != omitStep)
                .Select(s => new { name = s, exitCode = s == failStep ? 1 : 0 }),
            artifacts = new
            {
                sourceManifest = sourceManifest ?? Path.Combine(_candidate, "source-manifest.json"),
                portableFiles = Path.Combine(_candidate, "portable-files.json"),
                portableDirectory = Path.Combine(_candidate, "portable"),
                portableManifest = Path.Combine(_candidate, "portable.manifest.json"),
                images = new Dictionary<string, string> { ["missum-ai/gateway:candidate"] = Digest },
            },
        });

    private void WriteEntries(string name, string root, params string[] files) =>
        File.WriteAllText(Path.Combine(_candidate, name), JsonSerializer.Serialize(
            files.Select(f => new { path = f, sha256 = Hash(Path.Combine(root, f)) })));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _workspace, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
