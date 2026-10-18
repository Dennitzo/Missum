using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;

namespace Missum.Tests;

public sealed class PublicationFileWorkflowTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task OldNumberingEditionIsRefreshedOnceWithoutEditingTheManuscript()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "Publikation.md");
        const string raw = "# Untersuchung\n\n## 1. Modell\n\nText.\n\n## 2. Literatur und Daten\n\n### 2.1 Fachliteratur\n\nQuelle.";
        await File.WriteAllTextAsync(source, raw);
        var pdf = (await PdfAsync(source, CancellationToken.None))!;
        static string Hash(string text) => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        await File.WriteAllTextAsync(Path.Combine(environment.Directory, "publication.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                fingerprint = Hash("scientific-publication-v11-mathematical-proof-exposition-linked-table-of-contents\n" + raw + "\n"),
                markdownSha256 = Hash(raw),
                pdfSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(pdf))),
            }));
        var renderCount = 0;
        async Task<string?> Render(string path, CancellationToken token)
        {
            renderCount++;
            return await PdfAsync(path, token);
        }
        using (var service = Service(environment, Render))
        {
            service.RegisterWorkspace("edition-update", environment.Directory);
            Assert.NotNull(await service.EnsureCurrentAsync("edition-update"));
        }
        Assert.Equal(1, renderCount);
        using (var reopened = Service(environment, Render))
        {
            reopened.RegisterWorkspace("edition-update", environment.Directory);
            Assert.NotNull(await reopened.EnsureCurrentAsync("edition-update"));
        }
        Assert.Equal(1, renderCount);
        Assert.Equal(raw, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task UnchangedPublicationDoesNotReopenImagePayloadsAndImageEditInvalidatesCache()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var image = Path.Combine(environment.Directory, "plot.png");
        await File.WriteAllBytesAsync(image, Png);
        var renderCount = 0;
        using var service = Service(environment, async (source, token) =>
        {
            renderCount++;
            return await PdfAsync(source, token);
        });
        var markdown = await service.EnsureWorkspaceDocumentAsync("cached-images", environment.Directory);
        await File.WriteAllTextAsync(markdown, "# Untersuchung\n\n![Messung](plot.png)\n");
        var first = (await service.EnsureCurrentAsync("cached-images"))!;
        await using (var locked = new FileStream(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unchanged = await service.EnsureCurrentAsync("cached-images");
            Assert.Equal(first.ContentHash, unchanged!.ContentHash);
            Assert.Equal(1, renderCount);
        }
        await File.WriteAllBytesAsync(image, ChangedPng());
        var updated = await service.EnsureCurrentAsync("cached-images");
        Assert.NotEqual(first.ContentHash, updated!.ContentHash);
        Assert.Equal(2, renderCount);
    }

    [Fact]
    public async Task SlowPublicationDoesNotBlockAnotherWorkspace()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var firstRoot = Path.Combine(environment.Directory, "first");
        var secondRoot = Path.Combine(environment.Directory, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Service(environment, async (source, token) =>
        {
            if (source.StartsWith(firstRoot, StringComparison.OrdinalIgnoreCase))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return await PdfAsync(source, token);
        });
        await service.EnsureWorkspaceDocumentAsync("first-workspace", firstRoot);
        await service.EnsureWorkspaceDocumentAsync("second-workspace", secondRoot);
        var first = service.EnsureCurrentAsync("first-workspace");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.NotNull(await service.EnsureCurrentAsync("second-workspace").WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task ExistingNestedPublicationRemainsTheAuthoritativeFile()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        const string projectId = "research-file-workflow";
        var root = Path.Combine(environment.Directory, "Science", projectId);
        var existing = Path.Combine(root, "publications", "existing-owner", "current", "Publikation.md");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        const string authored = "# Bestehende Untersuchung\n\n## Berechnung\n\n$F=ma$ bleibt erhalten.\n";
        await File.WriteAllTextAsync(existing, authored);
        using var service = Service(environment);

        var path = await service.EnsureWorkspaceDocumentAsync(projectId, environment.Directory, "Neuer UI-Titel");
        var publication = await service.EnsureCurrentAsync(projectId);

        Assert.Equal(existing, path);
        Assert.NotNull(publication);
        Assert.Equal(existing, publication.MarkdownPath);
        Assert.Equal(authored, await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(Path.Combine(environment.Directory, "Publikation.md")));
        Assert.False(File.Exists(Path.Combine(root, "Publikation.md")));
    }

    [Fact]
    public async Task UserProjectRootWinsOverAnExistingLegacyTree()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        const string projectId = "research-root-wins";
        var legacy = Path.Combine(environment.Directory, "Science", projectId);
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "Publikation.md"), "# Veraltete Kopie\n\nAlter Text.");
        var source = Path.Combine(environment.Directory, "Publikation.md");
        await File.WriteAllTextAsync(source, "# Aktueller Projektstand\n\nAktueller Text.");
        using var service = Service(environment);
        Assert.Equal(source, await service.EnsureWorkspaceDocumentAsync(projectId, environment.Directory));
        Assert.Equal(source, (await service.EnsureCurrentAsync(projectId))!.MarkdownPath);
    }

    [Fact]
    public async Task NewProjectNeedsNoResearchRowsAndKeepsOneEditableDocument()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var service = Service(environment);
        var source = await service.EnsureWorkspaceDocumentAsync("research-new-file", environment.Directory, "Offene Frage");
        Assert.Equal(Path.Combine(environment.Directory, "Publikation.md"), source);
        Assert.Equal("# Offene Frage\n\n## Literatur und Daten\n\n### Fachliteratur\n\n### Forschungsdaten\n\n",
            await File.ReadAllTextAsync(source));
        Assert.Null(await environment.Get<IScientificResearchRepository>().GetProjectAsync("research-new-file"));

        await File.WriteAllTextAsync(source, "# Offene Frage\n\n## Ansatz\n\nDie Annahme wird untersucht.");
        var publication = await service.EnsureCurrentAsync("research-new-file");
        Assert.NotNull(publication);
        Assert.Equal(Path.ChangeExtension(source, ".pdf"), publication.PdfPath);
        Assert.Equal(0, publication.Revision);
        Assert.Empty(Directory.EnumerateDirectories(environment.Directory, ".missum-publication-*"));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, "publications")));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, "Science")));
    }

    [Fact]
    public async Task EditingAndRemovingSectionsCannotBeOverwrittenByOldDatabaseState()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Publikation", ChatMode.Coding);
        const string projectId = "research-edited-file";
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject(projectId, session.Id, "mathematicalInvestigation", "Alt", "Alt",
            "codingWorkspaceResearch", "multiPath", "active", 1, 83, now, now, environment.Directory);
        await environment.Get<IScientificResearchRepository>().UpsertProjectAsync(project);
        using var service = Service(environment);
        var source = await service.EnsureWorkspaceDocumentAsync(projectId, environment.Directory);
        await File.WriteAllTextAsync(source, "# Aktuelle Publikation\n\n## Entfernt\n\nALTERABSCHNITT\n\n## Behalten\n\nNeuer Befund.");
        var first = (await service.EnsureCurrentAsync(projectId))!;
        const string edited = "# Aktuelle Publikation\n\n## Behalten\n\nNur dieser Befund bleibt.";
        await File.WriteAllTextAsync(source, edited);

        var updated = (await service.EnsureCurrentAsync(projectId))!;
        Assert.NotEqual(first.ContentHash, updated.ContentHash);
        Assert.Equal(first.PdfPath, updated.PdfPath);
        Assert.Equal(edited, await File.ReadAllTextAsync(source));
        Assert.DoesNotContain("ALTERABSCHNITT", await File.ReadAllTextAsync(source));
        Assert.Equal(project, await environment.Get<IScientificResearchRepository>().GetProjectAsync(projectId));
        File.Delete(source);
        Assert.Null(await service.EnsureCurrentAsync(projectId));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task ChangingAReferencedPlotInvalidatesPdfWithoutEditingManuscript()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var renders = 0;
        string? renderedMarkdown = null;
        using var service = Service(environment, async (source, token) =>
        {
            renders++;
            renderedMarkdown = await File.ReadAllTextAsync(source, token);
            return await PdfAsync(source, token);
        });
        var source = await service.EnsureWorkspaceDocumentAsync("research-plot-file", environment.Directory, "Messdaten");
        var work = Path.Combine(environment.Directory, "work");
        Directory.CreateDirectory(work);
        var plot = Path.Combine(work, "plot.png");
        await File.WriteAllBytesAsync(plot, Png);
        const string manuscript = "# Messdaten\n\n## Ergebnis\n\n![Gleichungsvergleich](work/plot.png)\n\nDie Kurven vergleichen dieselben Gleichungen.";
        await File.WriteAllTextAsync(source, manuscript);
        var first = (await service.EnsureCurrentAsync("research-plot-file"))!;
        Assert.Equal(first, await service.EnsureCurrentAsync("research-plot-file"));
        Assert.Equal(1, renders);
        await File.WriteAllBytesAsync(plot, ChangedPng());
        var updated = (await service.EnsureCurrentAsync("research-plot-file"))!;

        Assert.Equal(2, renders);
        Assert.NotEqual(first.ContentHash, updated.ContentHash);
        Assert.Equal(manuscript, await File.ReadAllTextAsync(source));
        Assert.NotNull(renderedMarkdown);
        Assert.Matches(@"!\[Gleichungsvergleich\]\(figures/[a-f0-9]{64}\.png\)", renderedMarkdown);
        Assert.Empty(Directory.EnumerateDirectories(environment.Directory, ".missum-publication-*"));
    }

    [Fact]
    public async Task FailedTypesettingPreservesPreviousPdfAndEditedSource()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fail = false;
        using var service = Service(environment, (source, token) => fail
            ? throw new InvalidOperationException("KaTeX: unbekannter Befehl") : PdfAsync(source, token));
        var source = await service.EnsureWorkspaceDocumentAsync("research-invalid-formula", environment.Directory);
        await File.WriteAllTextAsync(source, "# Formel\n\n$F=ma$.");
        var first = (await service.EnsureCurrentAsync("research-invalid-formula"))!;
        var previousPdf = await File.ReadAllBytesAsync(first.PdfPath);
        const string edited = "# Formel\n\n$\\badCommand{F}$";
        await File.WriteAllTextAsync(source, edited);
        fail = true;

        var error = await Assert.ThrowsAsync<ScientificPublicationContentException>(() => service.EnsureCurrentAsync("research-invalid-formula"));
        Assert.Contains(source, error.Message);
        Assert.DoesNotContain("research.update", error.Message);
        Assert.Equal(edited, await File.ReadAllTextAsync(source));
        Assert.Equal(previousPdf, await File.ReadAllBytesAsync(first.PdfPath));
    }

    [Fact]
    public void LeanSyntaxIsNotPresentedAsMathematicalProseAndExistingExplanationIsPreserved()
    {
        const string source = "# Beweis\n\n## Aussage\n\nDie Summe ist wegen der Kommutativität unverändert.\n\n"
            + "```lean\ntheorem swap (a b : Nat) : a + b = b + a := by omega\n```\n\n"
            + "## Grenzen\n\nDies bestätigt keine physikalische Modellannahme.";
        var prose = ScientificPublicationService.PreparePublicationProse(source);
        Assert.Contains("Die Summe ist wegen der Kommutativität unverändert.", prose);
        Assert.Contains("als mathematischer Beweis mit Voraussetzungen", prose);
        Assert.Contains("## Grenzen", prose);
        Assert.Contains("Dies bestätigt keine physikalische Modellannahme.", prose);
        Assert.DoesNotContain("```", prose);
        Assert.DoesNotContain("theorem swap", prose);
        Assert.DoesNotContain("by omega", prose);
        Assert.DoesNotContain("vollständig geprüft", prose);
    }

    [Theory]
    [InlineData("```lean4\ntheorem swap : True := by trivial\n```")]
    [InlineData("~~~~LEAN\ntheorem swap : True := by trivial\n~~~~")]
    [InlineData("```lean\ntheorem swap : True := by trivial")]
    public void LeanFenceVariantsDoNotLeakUntranslatedMachineProofs(string fence)
    {
        var prose = ScientificPublicationService.PreparePublicationProse("# Beweis\n\n" + fence);
        Assert.Contains("Redaktioneller Hinweis", prose);
        Assert.DoesNotContain("theorem swap", prose);
        Assert.DoesNotContain("by trivial", prose);
        Assert.DoesNotContain("vollständig geprüft", prose);
    }

    [Fact]
    public void OrdinaryCodeAndMathematicalProofTextRemainUnchanged()
    {
        const string source = "# Beweis\n\nSei $x\\in\\mathbb{R}$. Aus $2x=(1+1)x=x+x$ folgt die Behauptung.\n\n"
            + "```python\nlean = 'a project tool'\nprint(lean)\n```";
        var prose = ScientificPublicationService.PreparePublicationProse(source);
        Assert.Contains(source, prose.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("Redaktioneller Hinweis", prose);
    }

    [Fact]
    public async Task ProjectIdentifierCannotEscapeIntoAnotherFolder()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var service = Service(environment);
        Assert.Throws<ArgumentException>(() => service.RegisterWorkspace("../other", environment.Directory));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DirectFilePublicationRetainsLinkedContentsAndProseProofs()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_PUBLICATION_LAYOUT_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var service = new ScientificPublicationService(environment.Get<IScientificResearchRepository>(), renderer);
        var source = await service.EnsureWorkspaceDocumentAsync("research-real-toc", environment.Directory, "Beweis und Modell");
        await File.WriteAllTextAsync(source, "# Beweis und Modell\n\n## Voraussetzungen\n\nSei $x\\in\\mathbb{R}$.\n\n"
            + "## Mathematischer Beweis\n\nAus dem Distributivgesetz folgt\n\n$$2x=(1+1)x=x+x.$$\n\n"
            + "Damit ist die doppelte Addition gleich der Multiplikation mit zwei.\n\n"
            + "## Grenzen\n\nDie Aussage betrifft reelle Zahlen.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var publication = await service.EnsureCurrentAsync("research-real-toc", timeout.Token);
        Assert.NotNull(publication);
        using var pdf = PdfDocument.Open(publication.PdfPath);
        var contents = pdf.GetPages().Single(page => page.GetWords().Any(word => word.Text == "Inhaltsverzeichnis"));
        Assert.NotEmpty(contents.GetAnnotations().Select(annotation => annotation.Action).OfType<GoToAction>());
        var text = string.Join(' ', pdf.GetPages().Select(page => page.Text));
        Assert.Contains("Distributivgesetz", text);
        Assert.Contains("doppelte Addition", text);
        Assert.DoesNotContain("theorem", text);
        Assert.DoesNotContain("by omega", text);
        Assert.DoesNotContain("```", text);
        Assert.True(pdf.TryGetBookmarks(out _));
    }

    private static ScientificPublicationService Service(TestEnvironment environment,
        Func<string, CancellationToken, Task<string?>>? renderer = null) =>
        new(environment.Get<IScientificResearchRepository>(), renderer ?? PdfAsync, Path.Combine(environment.Directory, "fallback"));

    private static async Task<string?> PdfAsync(string source, CancellationToken token)
    {
        var pdf = Path.ChangeExtension(source, ".pdf");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7\n" + new string(' ', 1200), new UTF8Encoding(false), token);
        return pdf;
    }

    private static byte[] ChangedPng()
    {
        var data = "Plot\0changed"u8.ToArray();
        var chunk = new byte[data.Length + 12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
        return [.. Png[..^12], .. chunk, .. Png[^12..]];
    }
}
