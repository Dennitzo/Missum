using System.Text;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace Missum.Tests;

public sealed class ScientificPublicationTests(ITestOutputHelper output)
{
    private const string UnitDerivationMarkdown = """
        ## Vollständiger Rechenweg: kinetische Energie

        Voraussetzung ist eine konstante Masse bei nichtrelativistischer Geschwindigkeit. Die Werte sind ein ausdrücklich illustratives Rechenbeispiel, keine Messdaten.

        ### Symbol- und Einheitenlegende

        - $E$ ist die kinetische Energie in Joule ($\mathrm{J}$), der SI-Einheit der Energie.
        - $m$ ist die Masse in Kilogramm ($\mathrm{kg}$), der SI-Basiseinheit der Masse.
        - $v$ ist die Geschwindigkeit in $\mathrm{m/s}$, also Meter je Sekunde.
        - $1/2$ ist ein konstanter Faktor mit Einheit $1$, also dimensionslos.

        Rechenschritt 1: Die Definition lautet

        $$E=\frac{1}{2}mv^{2},\qquad [E]=[m][v]^{2}=\mathrm{kg}\,\mathrm{m}^{2}\,\mathrm{s}^{-2}=\mathrm{J}.$$

        Rechenschritt 2: Einsetzen von $m=2\,\mathrm{kg}$ und $v=3\,\mathrm{m}\,\mathrm{s}^{-1}$ einschließlich ihrer Einheiten.

        $$E=\frac{1}{2}(2\,\mathrm{kg})(3\,\mathrm{m}\,\mathrm{s}^{-1})^{2}.$$

        Rechenschritt 3: Quadrieren der Geschwindigkeit, dann Multiplikation; die Einheit wird in jeder Gleichheit mitgeführt.

        $$\begin{aligned}
        E&=\frac{1}{2}(2\,\mathrm{kg})(9\,\mathrm{m}^{2}\,\mathrm{s}^{-2})\\
         &=9\,\mathrm{kg}\,\mathrm{m}^{2}\,\mathrm{s}^{-2}\\
         &=9\,\mathrm{J}.
        \end{aligned}$$

        Rechenschritt 4: Die Dimensionskontrolle bestätigt Energie auf beiden Seiten: $[E]=\mathrm{J}$. Alle Eingaben und das Ergebnis verwenden SI-Einheiten.
        """;

    [Fact]
    public async Task DraftPublicationExistsBeforeResultsAndReusesUnchangedSnapshot()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        var renderCalls = 0;
        using var service = new ScientificPublicationService(repository, async (path, token) =>
        {
            renderCalls++;
            return await FakePdfAsync(path, token);
        }, Path.Combine(environment.Directory, "publications"));

        var first = await service.EnsureCurrentAsync(project.Id);
        var again = await service.EnsureCurrentAsync(project.Id);

        Assert.NotNull(first);
        Assert.Equal(first, again);
        Assert.Equal(1, renderCalls);
        Assert.True(first.IsDraft);
        Assert.Contains("Arbeitsfassung", await File.ReadAllTextAsync(first.MarkdownPath));
        Assert.Contains("Es wird noch kein Ergebnis behauptet.", await File.ReadAllTextAsync(first.MarkdownPath));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(first.PdfPath)!)!, ".pending-*"));
        Assert.Equal(project, await repository.GetProjectAsync(project.Id));
    }

    [Fact]
    public async Task LaterResultChangesPublicationEvenWithoutRevisionBumpAndKeepsPreviousPdf()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        using var service = new ScientificPublicationService(repository, FakePdfAsync, Path.Combine(environment.Directory, "publications"));
        var first = await service.EnsureCurrentAsync(project.Id);
        await repository.SaveResultSnapshotAsync(project.Id, new([], [], [],
            [new("publication-claim", project.Id, "Die Lösung lautet $x=2$.", "calculated", "verified", 1, "{}", DateTimeOffset.UtcNow)]));

        var next = await service.EnsureCurrentAsync(project.Id);

        Assert.NotNull(first);
        Assert.NotNull(next);
        Assert.Equal(first.Revision, next.Revision);
        Assert.NotEqual(first.ContentHash, next.ContentHash);
        Assert.NotEqual(first.PdfPath, next.PdfPath);
        Assert.True(File.Exists(first.PdfPath));
        Assert.Contains("$x=2$", await File.ReadAllTextAsync(next.MarkdownPath));
    }

    [Fact]
    public async Task StaleAndCancelledRenderCannotPublishAnOutdatedPdf()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        var directory = Path.Combine(environment.Directory, "publications");
        using var stale = new ScientificPublicationService(repository, async (path, token) =>
        {
            var pdf = await FakePdfAsync(path, token);
            await repository.UpsertProjectAsync(project with { Revision = project.Revision + 1, UpdatedAt = project.UpdatedAt.AddSeconds(1) }, token);
            return pdf;
        }, directory);
        Assert.Null(await stale.EnsureCurrentAsync(project.Id));
        Assert.Empty(Directory.GetFiles(directory, "*.pdf", SearchOption.AllDirectories));

        using var cancellation = new CancellationTokenSource();
        using var cancelled = new ScientificPublicationService(repository, async (path, token) =>
        {
            var pdf = await FakePdfAsync(path, token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return pdf;
        }, directory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.EnsureCurrentAsync(project.Id, cancellation.Token));
        Assert.Empty(Directory.GetFiles(directory, "*.pdf", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(directory, ".pending-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidRendererOutputDoesNotReplaceUsablePreviousRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        var directory = Path.Combine(environment.Directory, "publications");
        using var good = new ScientificPublicationService(repository, FakePdfAsync, directory);
        var previous = await good.EnsureCurrentAsync(project.Id);
        Assert.NotNull(previous);
        var previousBytes = await File.ReadAllBytesAsync(previous.PdfPath);
        await repository.UpsertProjectAsync(project with { Revision = project.Revision + 1 });
        using var broken = new ScientificPublicationService(repository, async (path, token) =>
        {
            var target = Path.ChangeExtension(path, ".pdf");
            await File.WriteAllTextAsync(target, "render failed", token);
            return target;
        }, directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => broken.EnsureCurrentAsync(project.Id));
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(previous.PdfPath));
    }

    [Fact]
    public async Task ManuscriptUsesExactResearchRunAndRefreshesDuringStreamingWithoutPreviousAnswers()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        _ = await chats.AddMessageAsync(project.SessionId, ChatRole.Assistant, "PREVIOUSRUNANSWER", MessageStatus.Completed);
        var manuscript = await chats.AddMessageAsync(project.SessionId, ChatRole.Assistant, "CURRENTMANUSCRIPT $x=2$", MessageStatus.Streaming);
        _ = await chats.AddMessageAsync(project.SessionId, ChatRole.Assistant, "UNRELATEDLATERANSWER", MessageStatus.Completed);
        var now = DateTimeOffset.UtcNow;
        var run = new MissumAiRunRecord(Guid.NewGuid(), project.SessionId, manuscript.Id, null, Guid.NewGuid().ToString("N"),
            "publication-run", 0, "running", "fixture", null, now, now);
        await runs.CreateAsync(run);
        var manifest = JsonSerializer.Serialize(new { localRunId = run.Id, runId = run.ServerRunId, runStartedAt = run.CreatedAt });
        await repository.UpsertProjectAsync(project with { Status = "verified" });
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}", [], [], "bound-manuscript-report",
            "scientificMarkdown", "verified", "DOSSIERONLY", manifest, "research.result.persisted", "{}", run.ServerRunId!, 1, now));
        using var service = new ScientificPublicationService(repository, FakePdfAsync, Path.Combine(environment.Directory, "publications"), chats, runs);
        var draft = await service.EnsureCurrentAsync(project.Id);
        Assert.NotNull(draft);
        Assert.True(draft.IsDraft);
        var draftText = await File.ReadAllTextAsync(draft.MarkdownPath);
        Assert.Contains("CURRENTMANUSCRIPT", draftText);
        Assert.DoesNotContain("PREVIOUSRUNANSWER", draftText);
        Assert.DoesNotContain("UNRELATEDLATERANSWER", draftText);
        Assert.DoesNotContain("DOSSIERONLY", draftText);

        await chats.UpdateMessageAsync(manuscript.Id, manuscript.Content + "\n\nFINALSCIENTIFICRESULT", MessageStatus.Completed);
        var final = await service.EnsureCurrentAsync(project.Id);
        Assert.NotNull(final);
        Assert.False(final.IsDraft);
        Assert.Equal(draft.Revision, final.Revision);
        Assert.NotEqual(draft.ContentHash, final.ContentHash);
        Assert.Contains("FINALSCIENTIFICRESULT", await File.ReadAllTextAsync(final.MarkdownPath));

        await runs.UpdateAsync(run.Id, "retried-publication-run", 0, "running");
        var differentAttempt = await service.EnsureCurrentAsync(project.Id);
        Assert.NotNull(differentAttempt);
        Assert.DoesNotContain("CURRENTMANUSCRIPT", await File.ReadAllTextAsync(differentAttempt.MarkdownPath));
    }

    [Fact]
    public async Task AppendOnlyStreamingCanPublishAnHonestDraftWithoutStarvingPdfUpdates()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var manuscript = await chats.AddMessageAsync(project.SessionId, ChatRole.Assistant, "INITIALPARAGRAPH", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var run = new MissumAiRunRecord(Guid.NewGuid(), project.SessionId, manuscript.Id, null, Guid.NewGuid().ToString("N"),
            "continuous-publication-run", 0, "running", "fixture", null, now, now);
        await runs.CreateAsync(run);
        var manifest = JsonSerializer.Serialize(new { localRunId = run.Id, runId = run.ServerRunId, runStartedAt = run.CreatedAt });
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}", [], [], "continuous-manuscript-report",
            "researchProgress", "unresolved", "Zwischenstand", manifest, "research.progress.persisted", "{}", run.ServerRunId!, 1, now));
        using var service = new ScientificPublicationService(repository, async (path, token) =>
        {
            await chats.UpdateMessageAsync(manuscript.Id, manuscript.Content + "\n\nLATERPARAGRAPH", MessageStatus.Streaming, cancellationToken: token);
            return await FakePdfAsync(path, token);
        }, Path.Combine(environment.Directory, "publications"), chats, runs);
        var result = await service.EnsureCurrentAsync(project.Id);
        Assert.NotNull(result);
        Assert.True(result.IsDraft);
        Assert.Contains("INITIALPARAGRAPH", await File.ReadAllTextAsync(result.MarkdownPath));
        Assert.DoesNotContain("LATERPARAGRAPH", await File.ReadAllTextAsync(result.MarkdownPath));
    }

    [Fact]
    public void CompletedArticleBecomesMainPublicationWithoutDuplicatedDossierOrLongTaskPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("clean-paper", Guid.NewGuid(), "mathematicalInvestigation",
            "[MISSUM_WEB_RESEARCH_REQUEST]\nRechercheauftrag:\nUntersuche den gedämpften Oszillator.\n\n"
            + string.Join(' ', Enumerable.Repeat("Erzeuge danach Python-Skripte und PNG-Artefakte unter artifacts. BENCHMARKINSTRUCTION", 30)),
            "[MISSUM_WEB_RESEARCH_REQUEST] " + new string('x', 900), "codingWorkspaceResearch", "multiPath", "verified", 1, 4, now, now);
        var manuscript = new ChatMessage(Guid.NewGuid(), project.SessionId, ChatRole.Assistant,
            "Ich recherchiere zuerst die Grundlagen und erstelle dann den Artikel.\n\n"
            + "Ein erster Befund ergibt eine Periode von 6,4 Sekunden.\n\n"
            + "# Gedämpfte Schwingungen\n\n## Zusammenfassung\n\nEine analytische und numerische Untersuchung.\n\n"
            + "## Modell\n\n$$m\\ddot{x}+c\\dot{x}+kx=0$$\n\n## Ergebnisse\n\nERGEBNISMARKER Die Energie nimmt monoton ab.\n\n"
            + "## Literatur\n\n[Originalquelle](https://example.org/oscillator)", MessageStatus.Completed, now, now);
        var report = new ResearchStoredReport("dossier", project.Id, "scientificMarkdown", "verified", "DOSSIERMARKER", "{}", now);
        var text = ScientificPublicationService.FormatPublication(project, new([], [], [], []),
            [new("source", project.Id, "Oszillator", "https://example.org/oscillator", "published", "{}", "included", "verifiedExcerpt", now)], [], report, manuscript).Replace("\r", "", StringComparison.Ordinal);

        Assert.StartsWith("# Gedämpfte Schwingungen\n", text);
        Assert.Single(text.Split('\n'), line => line.StartsWith("# ", StringComparison.Ordinal));
        Assert.Single(text.Split('\n'), line => line == "## Zusammenfassung");
        Assert.DoesNotContain("## 1. Fragestellung", text);
        Assert.DoesNotContain("## 3. Ergebnisse", text);
        Assert.DoesNotContain("MISSUM_WEB_RESEARCH_REQUEST", text);
        Assert.DoesNotContain("BENCHMARKINSTRUCTION", text);
        Assert.DoesNotContain("DOSSIERMARKER", text);
        Assert.DoesNotContain("Ich recherchiere zuerst", text);
        Assert.Contains("Ein erster Befund ergibt eine Periode von 6,4 Sekunden.", text);
        Assert.Contains("$$m\\ddot{x}+c\\dot{x}+kx=0$$", text);
        Assert.Contains("ERGEBNISMARKER", text);
        Assert.DoesNotContain("Ergänzende Originalquellen", text);
        Assert.Contains("Dokumentationsstand:", text);
    }

    [Fact]
    public void EarlyDraftHasBoundedQuestionAndTitleWithoutInternalPromptMarkers()
    {
        var now = DateTimeOffset.UtcNow;
        var question = "Wie entwickelt sich die Amplitude eines gedämpften Oszillators bei verschiedenen Reibungswerten?";
        var project = new ScientificResearchProject("clean-draft", Guid.NewGuid(), "mathematicalInvestigation",
            "[MISSUM_WEB_RESEARCH_REQUEST]\nRechercheauftrag:\n" + question + "\n\n"
            + string.Join(' ', Enumerable.Repeat("PYTHONEXECUTIONDETAILS Erstelle umfangreiche Skripte, Plots und Testartefakte.", 40)),
            "[MISSUM_WEB_RESEARCH_REQUEST] " + new string('y', 1200), "codingWorkspaceResearch", "multiPath", "active", 1, 1, now, now);
        var text = ScientificPublicationService.FormatPublication(project, new([], [], [], []), [], [], null).Replace("\r", "", StringComparison.Ordinal);
        Assert.StartsWith("# " + question, text);
        Assert.InRange(text.Split('\n')[0].Length, 1, 144);
        Assert.DoesNotContain("MISSUM_WEB_RESEARCH_REQUEST", text);
        Assert.DoesNotContain("PYTHONEXECUTIONDETAILS", text);
        Assert.Contains("Arbeitsfassung", text);
        Assert.Contains("## 1. Fragestellung\n\n" + question, text);
        Assert.Contains("Es wird noch kein Ergebnis behauptet.", text);
    }

    [Fact]
    public void PublicationRetainsMathematicsAndClearlySeparatesSourcesFromConfirmedClaims()
    {
        var now = DateTimeOffset.Parse("2026-09-30T13:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        var project = new ScientificResearchProject("paper", Guid.NewGuid(), "mathematicalInvestigation",
            "Welche Lösung hat $x^2=4$?", "Untersuchung von $x^2=4$", "codingWorkspaceResearch", "multiPath", "verified", 1, 2, now, now);
        var results = new ResearchResultSnapshot([], [], [],
            [new("claim", project.Id, "Die reellen Lösungen sind $x=\\pm2$.", "calculated", "verified", 1, "{}", now)]);
        var report = new ResearchStoredReport("report", project.Id, "scientificMarkdown", "verified",
            "# Deep Research\n\n**Status:** `verified`\n\n## Herleitung\n\n$$\\int_0^1 x^2\\,dx=\\frac{1}{3}$$", "{}", now);
        var text = ScientificPublicationService.FormatPublication(project, results,
            [new("source", project.Id, "Analysis [Einführung]", "https://example.org/source", "published", "{}", "included", "verifiedExcerpt", now)], [], report);

        Assert.DoesNotContain("# Deep Research", text);
        Assert.DoesNotContain("Arbeitsfassung", text);
        Assert.Contains("30.09.2026 11:00 UTC", text);
        Assert.Contains("$$\\int_0^1 x^2\\,dx=\\frac{1}{3}$$", text);
        Assert.Contains("### Herleitung", text);
        Assert.Contains("[Originalquelle](https://example.org/source)", text);
        Assert.Contains("Prüfstatus:** bestätigt", text);
        Assert.Contains("kein Beweis", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullDerivationAndUnitLegendRemainIntactInBothReportAndArticlePublication(bool article)
    {
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("unit-derivation", Guid.NewGuid(), "mathematicalInvestigation",
            "Berechne die kinetische Energie mit vollständigem Rechenweg.", "Kinetische Energie", "sandboxResearch",
            "multiPath", "verified", 1, 2, now, now);
        var report = new ResearchStoredReport("unit-report", project.Id, "scientificMarkdown", "verified",
            article ? "VERALTETESDOSSIER" : UnitDerivationMarkdown, "{}", now);
        var manuscript = article
            ? new ChatMessage(Guid.NewGuid(), project.SessionId, ChatRole.Assistant,
                "# Kinetische Energie\n\n" + UnitDerivationMarkdown, MessageStatus.Completed, now, now)
            : null;

        var text = ScientificPublicationService.FormatPublication(project, new([], [], [], []), [], [], report, manuscript)
            .Replace("\r", "", StringComparison.Ordinal);

        // Neither manuscript selection nor fallback heading conversion may drop
        // individual calculation steps, repeated units or the compact legend.
        foreach (var line in UnitDerivationMarkdown.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')))
            Assert.Contains(line, text);
        Assert.DoesNotContain("VERALTETESDOSSIER", text);
        Assert.DoesNotContain("| Symbol |", text);
        Assert.Equal(4, text.Split('\n').Count(line => line.StartsWith("- $", StringComparison.Ordinal)));
        Assert.Single(text.Split('\n'), line => line.StartsWith("# ", StringComparison.Ordinal));
        Assert.Contains("dimensionslos", text);
        Assert.Contains(@"&=9\,\mathrm{J}.", text);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealScientificPublicationRendersMathematicsIntoReadablePdf()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENTIFIC_PUBLICATION_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        await repository.UpsertProjectAsync(project with { Status = "verified", Revision = 2 });
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}", [], [], "scientific-publication-live",
            "scientificMarkdown", "verified",
            "## Analytische Lösung\n\nPUBLICATIONMATHCHECK Die Gleichung beschreibt einen gedämpften Oszillator.\n\n"
            + "$$m\\ddot{x}+c\\dot{x}+kx=0$$\n\nDie Eigenfrequenz lautet $\\omega_0=\\sqrt{k/m}$.\n\n"
            + "## Integration\n\n$$\\int_0^1 x^2\\,dx=\\frac{1}{3}$$\n\n" + UnitDerivationMarkdown + "\n\nPUBLICATIONENDCHECK",
            "{}", "research.result.persisted", "{}", "publication-live", 2, DateTimeOffset.UtcNow));
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var service = new ScientificPublicationService(repository, renderer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var publication = await service.EnsureCurrentAsync(project.Id, Path.Combine(environment.Directory, "publications"), timeout.Token);
        Assert.NotNull(publication);
        Assert.False(publication.IsDraft);
        using var pdf = PdfDocument.Open(publication.PdfPath);
        var content = string.Join('\n', pdf.GetPages().Select(page => page.Text));
        Assert.Contains("PUBLICATIONMATHCHECK", content);
        Assert.Contains("PUBLICATIONENDCHECK", content);
        Assert.Contains("Kilogramm", content);
        Assert.Contains("Joule", content);
        Assert.Contains("dimensionslos", content);
        Assert.DoesNotContain("| Symbol |", content);
        for (var step = 1; step <= 4; step++) Assert.Contains("Rechenschritt " + step, content);
        var markdown = (await File.ReadAllTextAsync(publication.MarkdownPath, timeout.Token)).Replace("\r", "", StringComparison.Ordinal);
        foreach (var line in UnitDerivationMarkdown.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')))
            Assert.Contains(line, markdown);
        Assert.DoesNotContain("\\frac", content);
        Assert.DoesNotContain("\\ddot", content);
        Assert.DoesNotContain("\\mathrm", content);
        Assert.DoesNotContain("\\begin", content);
        Assert.DoesNotContain("A4-Buchformat", content);
        Assert.DoesNotContain("DOKUMENT", content);
        output.WriteLine($"Scientific PDF: {pdf.NumberOfPages} pages, {new FileInfo(publication.PdfPath).Length} bytes.");
        var evidence = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.Copy(publication.PdfPath, Path.Combine(evidence, "scientific-publication.pdf"), overwrite: true);
            File.Copy(publication.MarkdownPath, Path.Combine(evidence, "scientific-publication.md"), overwrite: true);
        }
    }

    private static async Task<(IScientificResearchRepository Repository, ScientificResearchProject Project)> CreateProjectAsync(TestEnvironment environment)
    {
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Publikation", ChatMode.Coding);
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-publication", session.Id, "mathematicalInvestigation",
            "Wie verhält sich ein gedämpfter Oszillator?", "Analyse eines gedämpften Oszillators", "codingWorkspaceResearch",
            "multiPath", "active", 1, 1, now, now, environment.Directory);
        var repository = environment.Get<IScientificResearchRepository>();
        await repository.UpsertProjectAsync(project);
        return (repository, project);
    }

    private static async Task<string?> FakePdfAsync(string source, CancellationToken token)
    {
        var path = Path.ChangeExtension(source, ".pdf");
        // Unit tests exercise publication atomicity, not typesetting. The opt-in test above uses the real renderer.
        await File.WriteAllTextAsync(path, "%PDF-1.7\n" + new string(' ', 1200), new UTF8Encoding(false), token);
        return path;
    }
}
