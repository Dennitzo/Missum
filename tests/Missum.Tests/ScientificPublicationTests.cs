using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendererUpgradeRegeneratesUnchangedManuscriptAndPreservesOldPdfAndResearchRevisions(bool canonical)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        var states = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        ResearchWorkingState? state = null;
        if (canonical)
        {
            project = project with { ProtocolVersion = 2 };
            await repository.UpsertProjectAsync(project);
            var updated = await states.ApplyWorkingUpdateAsync(project.Id, "renderer-upgrade-fixture", null, "Oszillator",
                [new("section-model", "section", 0, JsonSerializer.SerializeToElement(new
                {
                    title = "Modell und Voraussetzungen", contentMarkdown = "Die lineare Modellgleichung lautet $m\\ddot{x}+c\\dot{x}+kx=0$.",
                    status = "draft", order = 1,
                }))]);
            Assert.True(updated.Success);
            state = updated.State;
        }
        var manuscript = state is null
            ? ScientificPublicationService.FormatPublication(project, new([], [], [], []), [], [], null)
            : ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [], [], []));
        // This is the persisted v8 cache format, deliberately independent of the
        // current renderer version. Empty canonical dependencies have no review,
        // cited sources or executed image experiments in this fixture.
        var dependencyHash = state is null ? "" : PublicationCacheHash("{\"reviewed\":false,\"sources\":[],\"experiments\":[]}");
        var previousHash = PublicationCacheHash("scientific-publication-v8-single-column\n" + manuscript
            + (dependencyHash.Length == 0 ? "" : "\n" + dependencyHash));
        var outputDirectory = Path.Combine(environment.Directory, "publications");
        var previousDirectory = Path.Combine(outputDirectory, PublicationCacheHash(project.Id)[..24],
            $"r{state?.PublicationRevision ?? project.Revision}-{previousHash[..24]}");
        Directory.CreateDirectory(previousDirectory);
        var previousSource = Path.Combine(previousDirectory, "Publikation.md");
        await File.WriteAllTextAsync(previousSource, manuscript, new UTF8Encoding(false));
        var previousPdf = await FakePdfAsync(previousSource, CancellationToken.None);
        Assert.NotNull(previousPdf);
        var previousBytes = await File.ReadAllBytesAsync(previousPdf);
        var beforeState = JsonSerializer.Serialize(await states.LoadWorkingStateAsync(project.Id));
        var renderCalls = 0;
        using var service = new ScientificPublicationService(repository, async (source, token) =>
        {
            renderCalls++;
            return await FakePdfAsync(source, token);
        }, outputDirectory);

        var current = await service.EnsureCurrentAsync(project.Id);
        var cached = await service.EnsureCurrentAsync(project.Id);

        Assert.NotNull(current);
        Assert.Equal(current, cached);
        Assert.Equal(1, renderCalls);
        Assert.NotEqual(previousPdf, current.PdfPath);
        Assert.Equal(state?.PublicationRevision ?? project.Revision, current.Revision);
        Assert.Equal(manuscript, await File.ReadAllTextAsync(current.MarkdownPath));
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(previousPdf));
        Assert.Equal(project, await repository.GetProjectAsync(project.Id));
        Assert.Equal(beforeState, JsonSerializer.Serialize(await states.LoadWorkingStateAsync(project.Id)));
    }

    [Fact]
    public async Task RendererUpgradeFailureRetainsOldEditionWithoutCachingItAsTheCurrentRenderer()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        var manuscript = ScientificPublicationService.FormatPublication(project, new([], [], [], []), [], [], null);
        var hash = PublicationCacheHash("scientific-publication-v8-single-column\n" + manuscript);
        var outputDirectory = Path.Combine(environment.Directory, "publications");
        var previousDirectory = Path.Combine(outputDirectory, PublicationCacheHash(project.Id)[..24], $"r{project.Revision}-{hash[..24]}");
        Directory.CreateDirectory(previousDirectory);
        var source = Path.Combine(previousDirectory, "Publikation.md");
        await File.WriteAllTextAsync(source, manuscript);
        var previousPdf = await FakePdfAsync(source, CancellationToken.None);
        var renderCalls = 0;
        using var service = new ScientificPublicationService(repository, async (path, token) =>
        {
            if (++renderCalls == 1) throw new IOException("Temporary renderer failure.");
            return await FakePdfAsync(path, token);
        }, outputDirectory);

        var retained = await service.EnsurePublicationAsync(project.Id);
        var current = await service.EnsureCurrentAsync(project.Id);

        Assert.NotNull(retained);
        Assert.Equal(previousPdf, retained.PdfPath);
        Assert.NotNull(current);
        Assert.NotEqual(previousPdf, current.PdfPath);
        Assert.Equal(2, renderCalls);
        Assert.Equal(project.Revision, current.Revision);
        Assert.Equal(project, await repository.GetProjectAsync(project.Id));
        Assert.Empty(Directory.GetDirectories(outputDirectory, ".pending-*", SearchOption.AllDirectories));
    }

    private static string PublicationCacheHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

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
        Assert.DoesNotContain("Ein erster Befund ergibt eine Periode von 6,4 Sekunden.", text);
        Assert.Contains("$$m\\ddot{x}+c\\dot{x}+kx=0$$", text);
        Assert.Contains("ERGEBNISMARKER", text);
        Assert.DoesNotContain("Ergänzende Originalquellen", text);
        Assert.Contains("Dokumentationsstand:", text);
    }

    [Fact]
    public void EarlyDraftWaitsForAnAuthoredTitleInsteadOfCopyingTheUserPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        var question = "Wie entwickelt sich die Amplitude eines gedämpften Oszillators bei verschiedenen Reibungswerten?";
        var project = new ScientificResearchProject("clean-draft", Guid.NewGuid(), "mathematicalInvestigation",
            "[MISSUM_WEB_RESEARCH_REQUEST]\nRechercheauftrag:\n" + question + "\n\n"
            + string.Join(' ', Enumerable.Repeat("PYTHONEXECUTIONDETAILS Erstelle umfangreiche Skripte, Plots und Testartefakte.", 40)),
            "[MISSUM_WEB_RESEARCH_REQUEST] " + new string('y', 1200), "codingWorkspaceResearch", "multiPath", "active", 1, 1, now, now);
        var text = ScientificPublicationService.FormatPublication(project, new([], [], [], []), [], [], null).Replace("\r", "", StringComparison.Ordinal);
        Assert.StartsWith("# Wissenschaftliche Untersuchung\n", text);
        Assert.InRange(text.Split('\n')[0].Length, 1, 144);
        Assert.DoesNotContain("MISSUM_WEB_RESEARCH_REQUEST", text);
        Assert.DoesNotContain("PYTHONEXECUTIONDETAILS", text);
        Assert.Contains("Arbeitsfassung", text);
        Assert.DoesNotContain(question, text);
        Assert.Contains("Es wird noch kein Ergebnis behauptet.", text);
    }

    [Fact]
    public void LegacyQuantumGravityArticleExcludesSurroundingChatAndOperationsButKeepsScientificLimits()
    {
        // Structure of the actual r20 Quantumgravity publication: the first H1
        // precedes chat/research diagnostics; the article is a later H2 section.
        var content = """
            # Quantengravitation: prüfbare Modellannahmen und Grenzen

            Ich recherchiere die Ausgangsgleichungen.

            SearXNG meldet brave: HTTP 404 und Network is unreachable (host.docker.internal:8081).

            ## Publikation – Arbeitsfassung (Entwurf, Stand nach erstem Forschungszyklus)

            **Entwurf:** Die PDF-Darstellung wird von Missum erzeugt.

            ### Kurzfassung

            Untersucht wird die Konsistenz der Modellannahmen, keine bestätigte Vereinheitlichung.

            ### Herleitungen und Rechenschritte

            HERLEITUNG

            ### Ergebnisse

            Eine allgemeine Lösung ist nicht hergeleitet. Der Geltungsbereich bleibt auf die angegebenen Voraussetzungen beschränkt.

            ### Diskussion und Grenzen

            Die Quellenprüfung mit SearXNG war nicht erfolgreich; es wurde keine Originalquelle gelesen.

            Network is unreachable. HTTP 503. HttpRequestException: request failed.

            brave: too many requests.

            Die Gültigkeit außerhalb des betrachteten Grenzfalls ist offen.

            ### Literatur

            Es liegen noch keine unabhängig geprüften Originalbelege vor.

            ## Abschluss des ersten Forschungszyklus

            ABSCHLUSSPROTOKOLL Bitte research.code.write verwenden; SHA256: 12345.

            **Fortschrittsmeldung:** Fortsetzung folgt.
            """.Replace("HERLEITUNG", string.Join('\n', UnitDerivationMarkdown.Split('\n')
                .Select(line => line.StartsWith('#') ? "#" + line : line)), StringComparison.Ordinal);
        var text = FormatManuscript(content);

        Assert.StartsWith("# Quantengravitation: prüfbare Modellannahmen und Grenzen\n", text);
        Assert.Contains("## Kurzfassung", text);
        Assert.Contains("Eine allgemeine Lösung ist nicht hergeleitet.", text);
        Assert.Contains("Die Gültigkeit außerhalb des betrachteten Grenzfalls ist offen.", text);
        Assert.Contains("Die unabhängige Quellenprüfung", text);
        Assert.Contains(@"&=9\,\mathrm{J}.", text);
        Assert.DoesNotContain("SearXNG", text);
        Assert.DoesNotContain("Network is unreachable", text);
        Assert.DoesNotContain("HTTP 503", text);
        Assert.DoesNotContain("too many requests", text);
        Assert.DoesNotContain("host.docker.internal", text);
        Assert.DoesNotContain("PDF-Darstellung", text);
        Assert.DoesNotContain("ABSCHLUSSPROTOKOLL", text);
        Assert.DoesNotContain("Fortschrittsmeldung", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestManuscriptSnapshotExcludesOtherChatAndPreservesMathAndActualFigure(bool streaming)
    {
        var content = """
            Ein außerhalb des Manuskripts genannter Wert ist kein Publikationstext: CHATBEFUND.

            <!-- MISSUM_PUBLICATION_BEGIN -->
            # Vorheriger Artikel

            VERALTETERBEFUND
            <!-- MISSUM_PUBLICATION_END -->

            Ich werde nun die Auswertung überarbeiten.

            <!-- MISSUM_PUBLICATION_BEGIN -->
            # Kinetische Energie unter expliziten Voraussetzungen

            ## Kurzfassung

            Das Rechenbeispiel prüft die Einheiten im nichtrelativistischen Modell.

            HERLEITUNG

            ## Abbildung

            ![Kinetische Energie als Funktion der Geschwindigkeit](artifacts/energie-v2.png)

            Die waagerechte Achse zeigt die Geschwindigkeit in Metern je Sekunde, die senkrechte die Energie in Joule. Dargestellt sind Modellwerte, keine Messdaten.
            """.Replace("HERLEITUNG", UnitDerivationMarkdown, StringComparison.Ordinal);
        if (!streaming) content += "\n<!-- MISSUM_PUBLICATION_END -->\n\nCHATFORTSETZUNG: Weiterer Arbeitsauftrag.";
        var text = FormatManuscript(content, streaming ? MessageStatus.Streaming : MessageStatus.Completed);

        Assert.StartsWith("# Kinetische Energie unter expliziten Voraussetzungen\n", text);
        Assert.DoesNotContain("CHATBEFUND", text);
        Assert.DoesNotContain("VERALTETERBEFUND", text);
        Assert.DoesNotContain("CHATFORTSETZUNG", text);
        Assert.DoesNotContain("MISSUM_PUBLICATION", text);
        Assert.Contains("![Kinetische Energie als Funktion der Geschwindigkeit](artifacts/energie-v2.png)", text);
        Assert.Contains("Modellwerte, keine Messdaten", text);
        foreach (var line in UnitDerivationMarkdown.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')))
            Assert.Contains(line.TrimEnd('\r'), text);
    }

    [Fact]
    public void MarkerExamplesInsideCodeCannotTruncateTheArticleAndEmptyNewSnapshotKeepsPreviousArticle()
    {
        var content = """
            ````text
            <!-- MISSUM_PUBLICATION_BEGIN -->
            # Falscher Titel im Codebeispiel
            <!-- MISSUM_PUBLICATION_END -->
            ````

            <!-- MISSUM_PUBLICATION_BEGIN -->
            # Einheitenprüfung

            ## Herleitung

            ```text
            <!-- MISSUM_PUBLICATION_END -->

            DIAGNOSEBEISPIEL
            ```

            $$E=9\,\mathrm{J}$$

            - $E$ ist die Energie in Joule.
            <!-- MISSUM_PUBLICATION_END -->

            <!-- MISSUM_PUBLICATION_BEGIN -->
            # Neuer Artikel

            ## Kurzfassung
            """;
        var text = FormatManuscript(content, MessageStatus.Streaming);

        Assert.StartsWith("# Einheitenprüfung\n", text);
        Assert.Contains(@"$$E=9\,\mathrm{J}$$", text);
        Assert.Contains("- $E$ ist die Energie in Joule.", text);
        Assert.DoesNotContain("DIAGNOSEBEISPIEL", text);
        Assert.DoesNotContain("```", text);
        Assert.DoesNotContain("Neuer Artikel", text);
        Assert.DoesNotContain("Falscher Titel", text);
    }

    [Fact]
    public void DossierFallbackDoesNotPublishOperationalClaimsOrRawExperimentOutput()
    {
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("diagnostics", Guid.NewGuid(), "mathematicalInvestigation",
            "NUTZERAUFTRAG Erstelle eine Publikation.", "NUTZERAUFTRAG", "codingWorkspaceResearch", "multiPath", "active", 1, 1, now, now);
        var report = new ResearchStoredReport("report", project.Id, "scientificMarkdown", "unresolved",
            "## Grenzen\n\nDie Modellannahme wurde nicht unabhängig bestätigt.\n\nSearXNG war nicht erfolgreich.", "{}", now);
        var results = new ResearchResultSnapshot([], [new ResearchExperiment("experiment", project.Id,
            "{}", "[]", "[]", "{}", "python calculation.py", "{}", "RAWSTDOUT", "RAWSTDERR", "[]", "unresolved", now, now)], [],
            [new("technical", project.Id, "SearXNG meldet brave: too many requests.", "observed", "unresolved", 1, "{}", now),
             new("scientific", project.Id, "Die Modellannahme gilt nur im betrachteten Grenzfall.", "calculated", "unresolved", 1, "{}", now)]);
        var text = ScientificPublicationService.FormatPublication(project, results, [], [], report);

        Assert.Contains("Die Modellannahme wurde nicht unabhängig bestätigt.", text);
        Assert.Contains("Die Modellannahme gilt nur im betrachteten Grenzfall.", text);
        Assert.Contains("Die unabhängige Quellenprüfung", text);
        Assert.DoesNotContain("SearXNG", text);
        Assert.DoesNotContain("RAWSTDOUT", text);
        Assert.DoesNotContain("RAWSTDERR", text);
        Assert.DoesNotContain("NUTZERAUFTRAG", text);
    }

    private static string FormatManuscript(string content, MessageStatus status = MessageStatus.Completed)
    {
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("manuscript", Guid.NewGuid(), "mathematicalInvestigation",
            "NUTZERAUFTRAG Erstelle ein Paper mit Simulation.", "NUTZERAUFTRAG", "codingWorkspaceResearch", "multiPath", "verified", 1, 1, now, now);
        var report = new ResearchStoredReport("report", project.Id, "scientificMarkdown", "verified", "ALTESDOSSIER", "{}", now);
        var manuscript = new ChatMessage(Guid.NewGuid(), project.SessionId, ChatRole.Assistant, content, status, now, now);
        return ScientificPublicationService.FormatPublication(project, new([], [], [], []), [], [], report, manuscript)
            .Replace("\r", "", StringComparison.Ordinal);
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
    public async Task CanonicalSectionsPreserveEveryDerivationStepAndUnitLegendThroughPublicationSource()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment);
        await repository.UpsertProjectAsync(project with { ProtocolVersion = 2 });
        var states = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var kinetic = string.Join('\n', UnitDerivationMarkdown.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n').Skip(1)).Trim() + "\n\nKINETICDERIVATIONEND";
        const string potential = """
            Im homogenen Schwerefeld ist die Masse konstant und die Fallbeschleunigung unabhängig von der Höhe. Wir wählen die Bezugshöhe $h=0$ mit $U(0)=0$; außerhalb dieser Näherung ist die folgende Formel nicht allgemein gültig. Die Zahlenwerte sind ein illustratives Rechenbeispiel.

            Rechenschritt 1: Gegen die Gewichtskraft wird quasistatisch Arbeit verrichtet. Daher ist die Änderung der potentiellen Energie das Integral der konstanten Kraft $mg$ über die Höhe. Die Integrationsvariable $z$ besitzt die Einheit Meter.

            $$\begin{aligned}
            U(h)-U(0)&=\int_0^h mg\,\mathrm{d}z\\
            &=mg[z]_0^h\\
            &=mg(h-0)\\
            U(h)&=mgh,\qquad [U]=\mathrm{kg}\,\mathrm{m}^2\,\mathrm{s}^{-2}=\mathrm{J}.
            \end{aligned}$$

            Rechenschritt 2: Wir setzen $m=2\,\mathrm{kg}$, $g=9{,}81\,\mathrm{m}\,\mathrm{s}^{-2}$ und $h=1{,}5\,\mathrm{m}$ ein. Zuerst werden die Faktoren einschließlich ihrer Einheiten eingesetzt, dann die Zahlen multipliziert und schließlich die zusammengesetzte SI-Einheit als Joule geschrieben.

            $$\begin{aligned}
            U&=(2\,\mathrm{kg})(9{,}81\,\mathrm{m}\,\mathrm{s}^{-2})(1{,}5\,\mathrm{m})\\
            &=29{,}43\,\mathrm{kg}\,\mathrm{m}^2\,\mathrm{s}^{-2}\\
            &=29{,}43\,\mathrm{J}.
            \end{aligned}$$

            Rechenschritt 3: Die Rückableitung $\mathrm{d}U/\mathrm{d}h=mg$ ergibt die eingesetzte Kraft in Newton. Das positive Vorzeichen bedeutet, dass beim Anheben Energie zugeführt wird. Diese Kontrolle gilt unter den genannten Annahmen und ist kein Nachweis für ein beliebiges Gravitationsfeld.

            POTENTIALDERIVATIONEND
            """;
        var update = await states.ApplyWorkingUpdateAsync(project.Id, "complete-canonical-derivations", null,
            "Mechanische Energie mit vollständigen Rechenwegen",
            // Submit both sections in reverse order and use IDs that would also sort incorrectly.
            [new("a-potential", "section", 0, JsonSerializer.SerializeToElement(new
            {
                title = "Potentielle Energie", contentMarkdown = potential, status = "draft", order = 20,
                units = new[]
                {
                    new { symbol = "U", meaning = "potentielle Energie", unit = @"\mathrm{J}" },
                    new { symbol = "h", meaning = "Höhe über der Bezugshöhe", unit = @"\mathrm{m}" },
                    new { symbol = "g", meaning = "Fallbeschleunigung", unit = @"\mathrm{m}\,\mathrm{s}^{-2}" },
                },
            })), new("z-kinetic", "section", 0, JsonSerializer.SerializeToElement(new
            {
                title = "Kinetische Energie", contentMarkdown = kinetic, status = "draft", order = 10,
                units = new[]
                {
                    new { symbol = "E", meaning = "kinetische Energie", unit = @"\mathrm{J}" },
                    new { symbol = "v", meaning = "Geschwindigkeit", unit = @"\mathrm{m/s}" },
                },
            }))]);
        Assert.True(update.Success);
        Assert.Empty(update.Conflicts);

        var persisted = await states.LoadWorkingStateAsync(project.Id);
        Assert.Equal(kinetic, persisted.Items.Single(item => item.Id == "z-kinetic").Data.GetProperty("contentMarkdown").GetString());
        Assert.Equal(potential, persisted.Items.Single(item => item.Id == "a-potential").Data.GetProperty("contentMarkdown").GetString());
        var formatted = ScientificPublicationService.FormatCanonicalPublication(persisted, [], new([], [], [], []))
            .Replace("\r", "", StringComparison.Ordinal);
        Assert.Contains(kinetic, formatted);
        Assert.Contains(potential.Replace("\r", "", StringComparison.Ordinal), formatted);
        Assert.True(formatted.IndexOf("KINETICDERIVATIONEND", StringComparison.Ordinal)
            < formatted.IndexOf("## Potentielle Energie", StringComparison.Ordinal));
        Assert.Contains(@"- $v$ ist Geschwindigkeit in $\mathrm{m/s}$.", formatted);
        Assert.Contains(@"- $g$ ist Fallbeschleunigung in $\mathrm{m}\,\mathrm{s}^{-2}$.", formatted);
        Assert.DoesNotContain("| Symbol |", formatted);

        // The fake renderer checks the exact source handed to PDF generation, without claiming mathematical correctness.
        using var service = new ScientificPublicationService(repository, FakePdfAsync, Path.Combine(environment.Directory, "publications"));
        var publication = await service.EnsureCurrentAsync(project.Id);
        Assert.NotNull(publication);
        Assert.True(publication.SectionDelta);
        Assert.Equal(persisted.PublicationRevision, publication.Revision);
        Assert.Equal(formatted, (await File.ReadAllTextAsync(publication.MarkdownPath)).Replace("\r", "", StringComparison.Ordinal));
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
            + "## Integration\n\n$$\\int_0^1 x^2\\,dx=\\frac{1}{3}$$\n\n" + UnitDerivationMarkdown
            + "\n\n## Mehrseitiger Render-Test\n\n"
            + string.Join("\n\n", Enumerable.Range(1, 6).Select(index => "### Prüffall " + index + "\n\n" + UnitDerivationMarkdown))
            + "\n\nPUBLICATIONENDCHECK",
            "{}", "research.result.persisted", "{}", "publication-live", 2, DateTimeOffset.UtcNow));
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var service = new ScientificPublicationService(repository, renderer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var publication = await service.EnsureCurrentAsync(project.Id, Path.Combine(environment.Directory, "publications"), timeout.Token);
        Assert.NotNull(publication);
        Assert.False(publication.IsDraft);
        using var pdf = PdfDocument.Open(publication.PdfPath);
        Assert.True(pdf.NumberOfPages >= 2, "The native continuous viewer fixture must contain multiple rendered pages.");
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
