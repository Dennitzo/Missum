using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificPublicationImagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-publication-images", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task ReferencedCurrentImagesAreHashedDeduplicatedAndCopiedImmutably()
    {
        var sandbox = CreateSandbox("current-project");
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var path = Path.Combine(sandbox.Layout.ArtifactsPath, "plot.png");
        await File.WriteAllBytesAsync(path, Png);
        await File.WriteAllBytesAsync(Path.Combine(sandbox.Layout.ArtifactsPath, "not-referenced.png"), Png);

        var prepared = await ScientificPublicationImages.PrepareAsync(
            "![Modell](/sandbox/artifacts/plot.png)\n\n![Wiederholung](plot.png)", sandbox.Layout.ProjectId, sandbox, start);

        var image = Assert.Single(prepared.Images);
        Assert.Matches("^figures/[a-f0-9]{64}\\.png$", image.RelativePath);
        Assert.Equal(2, Regex.Count(prepared.Markdown, Regex.Escape(image.RelativePath)));
        await File.WriteAllTextAsync(path, "changed after snapshot");
        var staging = Path.Combine(_root, "staging");
        await prepared.WriteImagesAsync(staging);
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(staging, image.RelativePath)));
        Assert.Single(Directory.GetFiles(Path.Combine(staging, "figures")));
    }

    [Fact]
    public async Task ForeignPathsTraversalAndWrongProjectNeverReadExternalImages()
    {
        var sandbox = CreateSandbox("owner");
        var foreign = CreateSandbox("foreign");
        var foreignFile = Path.Combine(foreign.Layout.ArtifactsPath, "private.png");
        await File.WriteAllBytesAsync(foreignFile, Png);
        var markdown = "![Traversal](/sandbox/artifacts/../../foreign/artifacts/private.png)\n"
            + "![Encoded](/sandbox/artifacts/%2e%2e/%2e%2e/foreign/artifacts/private.png)\n"
            + "![Absolute](<" + foreignFile.Replace('\\', '/') + ">)\n"
            + "![Other mount](/sandbox/inputs/private.png)";

        var prepared = await ScientificPublicationImages.PrepareAsync(markdown, "owner", sandbox, DateTimeOffset.UtcNow.AddMinutes(-1));
        var wrongOwner = await ScientificPublicationImages.PrepareAsync("![Other](private.png)", "owner", foreign, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Empty(prepared.Images);
        Assert.Empty(wrongOwner.Images);
        Assert.DoesNotContain("![", prepared.Markdown);
        Assert.DoesNotContain(foreignFile, prepared.Markdown);
        Assert.Contains("lokale Bilddatei nicht verfügbar", prepared.Markdown);
    }

    [Fact]
    public async Task OldMissingAndInvalidImagesBecomeCaptionsWithoutBlockingPublication()
    {
        var sandbox = CreateSandbox("invalid-images");
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var old = Path.Combine(sandbox.Layout.ArtifactsPath, "old.png");
        await File.WriteAllBytesAsync(old, Png);
        File.SetLastWriteTimeUtc(old, start.AddMinutes(-1).UtcDateTime);
        await File.WriteAllTextAsync(Path.Combine(sandbox.Layout.ArtifactsPath, "fake.png"), new string('x', 100));
        await File.WriteAllBytesAsync(Path.Combine(sandbox.Layout.ArtifactsPath, "partial.png"), Png[..40]);

        var prepared = await ScientificPublicationImages.PrepareAsync(
            "![Alt](old.png)\n![Falsch](fake.png)\n![Unvollständig](partial.png)\n![Fehlt](missing.png)",
            sandbox.Layout.ProjectId, sandbox, start);
        var unbound = await ScientificPublicationImages.PrepareAsync("![Alt](old.png)", sandbox.Layout.ProjectId, sandbox, null);

        Assert.Empty(prepared.Images);
        Assert.Empty(unbound.Images);
        Assert.Equal(4, Regex.Count(prepared.Markdown, "lokale Bilddatei nicht verfügbar"));
    }

    [Fact]
    public async Task LongEscapedFileNamesProduceShortSafeOutputNames()
    {
        var sandbox = CreateSandbox("long-names");
        var name = new string('x', 150) + " mit Leerzeichen (α).png";
        await File.WriteAllBytesAsync(Path.Combine(sandbox.Layout.ArtifactsPath, name), Png);

        var prepared = await ScientificPublicationImages.PrepareAsync("![Lange Bezeichnung](<artifacts/" + name + ">)",
            sandbox.Layout.ProjectId, sandbox, DateTimeOffset.UtcNow.AddMinutes(-1));

        var image = Assert.Single(prepared.Images);
        Assert.Equal(68, Path.GetFileName(image.RelativePath).Length);
        Assert.DoesNotContain(name, prepared.Markdown);
        Assert.Contains("Lange Bezeichnung", prepared.Markdown);
    }

    [Fact]
    public async Task CodeExamplesStayUntouchedAndUnavailableCaptionsEscapeHtml()
    {
        var sandbox = CreateSandbox("code-examples");
        await File.WriteAllBytesAsync(Path.Combine(sandbox.Layout.ArtifactsPath, "plot.png"), Png);
        const string examples = "`![Inline](plot.png)`\n\n```python\nprint('![Fenced](plot.png)')\n```\n\n";

        var prepared = await ScientificPublicationImages.PrepareAsync(examples
            + "![<b>Figure</b>](plot.png)\n![<script>alert(1)</script>](missing.png)",
            sandbox.Layout.ProjectId, sandbox, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.StartsWith(examples, prepared.Markdown);
        Assert.Single(prepared.Images);
        Assert.DoesNotContain("<script>", prepared.Markdown);
        Assert.DoesNotContain("<b>", prepared.Markdown);
        Assert.Contains("&lt;b&gt;Figure&lt;/b&gt;", prepared.Markdown);
        Assert.Contains("&lt;script&gt;", prepared.Markdown);
    }

    [Fact]
    public async Task PdfServiceReceivesStagedImageAndRetainsFinalAssetAfterCleanup()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Publikationsabbildung");
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-image-integration", session.Id, "scientificEvidence",
            "Ein Modell illustrieren", "Ein Modell illustrieren", "sandboxResearch", "multiPath", "active", 1, 1, now, now);
        await repository.UpsertProjectAsync(project);
        var sandbox = CreateSandbox(project.Id);
        await File.WriteAllBytesAsync(Path.Combine(sandbox.Layout.ArtifactsPath, "result.png"), Png);
        var manifest = JsonSerializer.Serialize(new { projectId = project.Id, runId = "image-run", runStartedAt = now.AddMinutes(-1) });
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}", [], [], "image-report", "scientificMarkdown", "unresolved",
            "## Ergebnisse\n\n![Abbildung 1: Modell](/sandbox/artifacts/result.png)", manifest,
            "test.snapshot", "{}", "image-run", 1, now));
        string? renderedSource = null;
        string? relativeImage = null;
        using var service = new ScientificPublicationService(repository, async (source, token) =>
        {
            renderedSource = source;
            var markdown = await File.ReadAllTextAsync(source, token);
            var match = Regex.Match(markdown, @"figures/[a-f0-9]{64}\.png");
            Assert.True(match.Success, markdown);
            relativeImage = match.Value;
            Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(source)!, match.Value), token));
            var pdf = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllBytesAsync(pdf, Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string(' ', 2048) + "\n%%EOF\n"), token);
            return pdf;
        }, Path.Combine(environment.Directory, "image-publications"), sandbox: sandbox);

        var publication = await service.EnsureCurrentAsync(project.Id);

        Assert.NotNull(publication);
        Assert.NotNull(renderedSource);
        Assert.NotNull(relativeImage);
        Assert.False(Directory.Exists(Path.GetDirectoryName(renderedSource)));
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(publication.MarkdownPath)!, relativeImage)));
        Assert.Contains(relativeImage, await File.ReadAllTextAsync(publication.MarkdownPath));
        Assert.True(File.Exists(publication.PdfPath));
    }

    private BoundSandbox CreateSandbox(string projectId)
    {
        var root = Path.Combine(_root, projectId);
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);
        return new(new(projectId, root, Path.Combine(root, "inputs"), Path.Combine(root, "work"), artifacts,
            Path.Combine(root, "notebooks"), Path.Combine(root, "manuscripts"), Path.Combine(root, "env"),
            Path.Combine(root, "runs"), Path.Combine(root, "snapshots"), DateTimeOffset.UtcNow, "fixture"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class BoundSandbox(ResearchSandboxLayout layout) : IResearchSandboxService
    {
        public ResearchSandboxLayout Layout { get; } = layout;
        public Task<ResearchSandboxLayout> EnsureProjectAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(Layout);
        public Task<ResearchSandboxRuntimeStatus> PrepareRuntimeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchSandboxFileChange> WriteTextAsync(string projectId, string relativePath, string content,
            string? expectedSha256 = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreChangeSetAsync(string projectId, string changeSetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchSandboxRunResult> RunPythonAsync(string projectId, string relativeScriptPath,
            IReadOnlyList<string>? arguments = null, int timeoutSeconds = 7200, string? relativeWorkingDirectory = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ArchiveProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
