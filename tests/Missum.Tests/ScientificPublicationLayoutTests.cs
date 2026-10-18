using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.App.Services;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Outline;

namespace Missum.Tests;

public sealed class ScientificPublicationLayoutTests
{
    private static readonly string[] MeasuredExperimentIds = ["simulation-real"];
    private static readonly string[] OlderExperimentIds = ["simulation-old"];
    [Fact]
    public void HistoricalNumberedSectionsUseNaturalOrderIncludingLetteredAdditions()
    {
        var state = State(Section("aa-ten", "10 Neues Thema"), Section("ab-ten-a", "10a Ergänzung"),
            Section("z-one", "1 Grundlagen"), Section("ab-two-a", "2a Zusatz"), Section("ac-two", "2 Modell"),
            Section("a-twelve", "12 Schlussfolgerungen"));

        Assert.Equal(["1 Grundlagen", "2 Modell", "2a Zusatz", "10 Neues Thema", "10a Ergänzung", "12 Schlussfolgerungen"],
            ScientificPublicationService.OrderedPublicationSections(state).Select(item => item.Data.GetProperty("title").GetString()));
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [], [], []));
        Assert.True(markdown.IndexOf("1 Grundlagen", StringComparison.Ordinal) < markdown.IndexOf("10 Neues Thema", StringComparison.Ordinal));
    }

    [Fact]
    public void UnnumberedSemanticOutlineRetainsAuthoredOrderWithoutAlphabetizing()
    {
        var state = State(Section("a-conclusion", "Ausblick", order: 90), Section("z-intro", "Einführung", order: 0),
            Section("m-method", "Verfahren", order: 20));
        Assert.Equal(["z-intro", "m-method", "a-conclusion"], ScientificPublicationService.OrderedPublicationSections(state).Select(item => item.Id));
    }

    [Fact]
    public void ExplicitUniqueOrderRemainsAuthoritativeEvenWhenHeadingNumbersDiffer()
    {
        var state = State(Section("sec-extension", "10 Ergänzung", order: 1), Section("sec-intro", "1 Einführung", order: 2));
        Assert.Equal(["sec-extension", "sec-intro"], ScientificPublicationService.OrderedPublicationSections(state).Select(item => item.Id));
    }

    [Fact]
    public void HistoricalMissingSectionOrderDoesNotPreventPublicationImport()
    {
        var missing = new ResearchWorkingItem("sec-new", "section", 3, null,
            JsonSerializer.SerializeToElement(new { title = "Neues Thema", contentMarkdown = "Neue fachliche Ergänzung.", status = "completed" }),
            DateTimeOffset.UnixEpoch);
        var state = State(Section("sec-intro", "1 Einführung", order: 1), missing);
        Assert.Equal(["sec-intro", "sec-new"], ScientificPublicationService.OrderedPublicationSections(state).Select(item => item.Id));
    }

    [Fact]
    public void HistoricalFullyNumberedOutlineWithoutOrderHasNaturalFallback()
    {
        ResearchWorkingItem Missing(string id, string title) => new(id, "section", 1, null,
            JsonSerializer.SerializeToElement(new { title, contentMarkdown = "Fachlicher Inhalt.", status = "completed" }), DateTimeOffset.UnixEpoch);
        Assert.Equal(["sec-one", "sec-ten"], ScientificPublicationService.OrderedPublicationSections(
            State(Missing("sec-ten", "10 Ergänzung"), Missing("sec-one", "1 Einführung"))).Select(item => item.Id));
    }

    [Fact]
    public void LegacyImportKeepsUnassignedPlotsOutOfUnrelatedPublicationTopics()
    {
        var state = State(Section("sec-model", "Modell"));
        var results = new ResearchResultSnapshot([], [Experiment("simulation-real", "artifacts/polumkehr_plot.png", "ProcessSucceeded")], [], []);
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], results);
        Assert.Contains("## Modell", markdown);
        Assert.Contains("Das Modell und seine Annahmen", markdown);
        Assert.DoesNotContain("artifacts/polumkehr_plot.png", markdown);
        Assert.DoesNotContain("## Simulationsabbildungen", markdown);
        Assert.DoesNotContain("Arbeitsfassung", markdown);
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("ProcessSucceeded", false)]
    public void FailedOrUnmeasuredFilesDoNotBecomePublicationFigures(string status, bool measured)
    {
        var results = new ResearchResultSnapshot([], [Experiment("simulation-untrusted", "artifacts/untrusted.png", status, measured)], [], []);
        var markdown = ScientificPublicationService.FormatCanonicalPublication(State(Section("sec-model", "Modell")), [], results);
        Assert.DoesNotContain("untrusted.png", markdown);
    }

    [Fact]
    public void MoreThanTwelveMeasuredPlotsRemainPresentAndOverflowIsExplicit()
    {
        var experiments = Enumerable.Range(1, 20).Select(index => Experiment("simulation-" + index,
            "artifacts/plot_" + index + ".png", "ProcessSucceeded")).ToArray();
        var state = State(SimulationSection(experiments));
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], experiments, [], []));
        foreach (var experiment in experiments) Assert.Contains("Experiment " + experiment.Id + "]", markdown);
        var tooMany = Enumerable.Range(1, 65).Select(index => Experiment("simulation-" + index,
            "artifacts/plot_" + index + ".png", "ProcessSucceeded")).ToArray();
        var error = Assert.Throws<ScientificPublicationContentException>(() =>
            ScientificPublicationService.FormatCanonicalPublication(State(SimulationSection(tooMany)), [], new([], tooMany, [], [])));
        Assert.Contains("64", error.Message);
        Assert.Contains("still ausgelassen", error.Message);
    }

    [Fact]
    public void NewestWorkPlotIsSelectedGloballyBeforeAnOlderSectionCanReserveItsFilename()
    {
        var oldSection = Section("sec-old", "Früheres Modell", order: 0) with
        {
            Data = JsonSerializer.SerializeToElement(new { title = "Früheres Modell", contentMarkdown = "Alte Annahmen.",
                status = "completed", order = 0, experimentIds = OlderExperimentIds }),
        };
        var old = Experiment("simulation-old", "work/fig1_time_series.png", "ProcessSucceeded");
        var latest = Experiment("simulation-latest", "work/fig1_time_series.png", "ProcessSucceeded") with
        {
            UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
            StdoutEvidence = old.StdoutEvidence.Replace(new string('a', 64), new string('c', 64), StringComparison.Ordinal),
        };
        var state = State(oldSection, Section("sec-end", "Einordnung", order: 1));
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [old, latest], [], []));
        Assert.Contains("## Früheres Modell", markdown);
        Assert.Contains("Alte Annahmen.", markdown);
        Assert.DoesNotContain("work/fig1_time_series.png", markdown);
        Assert.DoesNotContain("Experiment simulation-old", markdown);
        Assert.DoesNotContain("## Simulationsabbildungen", markdown);
    }

    [Fact]
    public void ExplicitFigureAssignmentPreventsDuplicateAutomaticFigure()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Ergebnisse", contentMarkdown = "Der Modellbereich ist eingeschränkt.",
            status = "completed", experimentIds = MeasuredExperimentIds,
            simulationDescriptions = new[] { new { experimentId = "simulation-real", equations = "$x(t)=t$",
                description = "Die Simulation zeigt die angenommene konstante Geschwindigkeit im gewählten Zeitbereich." } },
            figureCaptions = new[] { new { experimentId = "simulation-real", artifactPath = "artifacts/plot.png", caption = "Fachlich zugeordneter Plot" } } });
        var section = new ResearchWorkingItem("sec-results", "section", 1, null, data, DateTimeOffset.UnixEpoch);
        var markdown = ScientificPublicationService.FormatCanonicalPublication(State(section), [],
            new([], [Experiment("simulation-real", "artifacts/plot.png", "ProcessSucceeded")], [], []));
        Assert.Equal(1, Regex.Count(markdown, Regex.Escape("artifacts/plot.png")));
        Assert.Contains("Fachlich zugeordneter Plot", markdown);
        Assert.DoesNotContain("## Simulationsabbildungen", markdown);
    }

    [Fact]
    public void RenumberedHeadingOverflowStillLocatesExactOriginalSection()
    {
        var state = State(Section("sec-ten", "10a Neues Thema"), Section("sec-one", "1 Grundlagen"));
        var targets = ScientificPublicationService.SectionRepairTargets(state,
            "Die Überschrift ist zu lang. Abschnitt \"1.2 Neues Thema\": Kürzen.");
        Assert.Equal("sec-ten", Assert.Single(targets).Id);
        var diagnostic = ScientificPublicationService.SectionRepairDiagnostic(state,
            "Publikationstitel passt nicht vollständig in die Seitenmarke.");
        Assert.Contains("Publikation.md", diagnostic);
        Assert.Contains("Kürze nur den fachlichen Publikationstitel", diagnostic);
        Assert.DoesNotContain("research.update", diagnostic);
        Assert.DoesNotContain("konnte nicht eindeutig zugeordnet", diagnostic);
    }

    [Fact]
    public void LegacyReviewMetadataCannotInjectOrChangeAuthoredPublicationStatus()
    {
        var state = State(Section("sec-model", "Modell"));
        var receipt = new ResearchVerification("review", state.ProjectId, "publication-review", state.ProjectId, "publication",
            ScientificResearchReview.Method, "ReviewedWithEvidence", JsonSerializer.Serialize(new
            {
                protocol = ScientificResearchReview.Protocol, projectId = state.ProjectId, revision = state.Revision,
                publicationRevision = state.PublicationRevision, stateSha256 = ScientificResearchReview.Fingerprint(state), ready = true,
                evidencePolicy = ScientificResearchReview.EvidencePolicy,
            }), DateTimeOffset.UnixEpoch);
        var results = new ResearchResultSnapshot([], [], [receipt], []);

        var imported = ScientificPublicationService.FormatCanonicalPublication(state, [], results);
        var trusted = ScientificPublicationService.FormatCanonicalPublication(state, [], results, [receipt]);
        Assert.Equal(imported, trusted);
        Assert.Contains("Das Modell und seine Annahmen", trusted);
        Assert.DoesNotContain("Vorläufiger Forschungsstand", trusted);
        Assert.DoesNotContain("Geprüfter Forschungsstand", trusted);
        Assert.DoesNotContain("Revision", trusted);
        var renamed = ScientificPublicationService.FormatCanonicalPublication(state with { Title = "Überarbeitetes Modell" }, [], results, [receipt]);
        Assert.StartsWith("# Überarbeitetes Modell", renamed);
        Assert.Contains("Das Modell und seine Annahmen", renamed);
        Assert.DoesNotContain("Vorläufiger Forschungsstand", renamed);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealPublicationUsesFullWidthAndRejectsClippedTitleBeforeReplacingPdf()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_PUBLICATION_LAYOUT_LIVE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "missum-publication-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Publikation.md");
            var paragraph = "FULLWIDTHSTART " + string.Join(" ", Enumerable.Repeat(
                "Das Modell beschreibt einen eingeschränkten Bereich der Dynamik und seine fachlichen Annahmen werden gesondert geprüft.", 6));
            await File.WriteAllTextAsync(source, "# Erdpolumkehr\n\n**Missum · Claude Science**\n\n## Modell\n\n" + paragraph);
            using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var pdfPath = await exporter.EnsureCurrentAsync(source, sourceChanged: true, scientificPublication: true, cancellationToken: timeout.Token);
            Assert.NotNull(pdfPath);
            var before = await File.ReadAllBytesAsync(pdfPath, timeout.Token);
            using (var pdf = PdfDocument.Open(pdfPath))
            {
                var words = pdf.GetPages().Single(page => page.GetWords().Any(word => word.Text == "FULLWIDTHSTART")).GetWords().ToArray();
                var marker = Assert.Single(words, word => word.Text == "FULLWIDTHSTART");
                var line = words.Where(word => Math.Abs(word.BoundingBox.Bottom - marker.BoundingBox.Bottom) < 1.5
                    && word.BoundingBox.Left >= marker.BoundingBox.Left).OrderBy(word => word.BoundingBox.Left).ToArray();
                var end = marker.BoundingBox.Right;
                foreach (var word in line)
                {
                    if (word.BoundingBox.Left - end > 12) break;
                    end = Math.Max(end, word.BoundingBox.Right);
                }
                Assert.True(end - marker.BoundingBox.Left > 300,
                    $"The body paragraph used only {end - marker.BoundingBox.Left:0.0} PDF points rather than the full 169 mm print width.");
            }
            await File.WriteAllTextAsync(source, "# " + string.Join(" ", Enumerable.Repeat("Ein langer fachlicher Publikationstitel", 8))
                + "\n\n## Modell\n\nDer fachliche Inhalt bleibt erhalten.");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.EnsureCurrentAsync(source,
                sourceChanged: true, scientificPublication: true, cancellationToken: timeout.Token));
            Assert.Contains("Publikationstitel", error.Message);
            Assert.Contains("nicht vollständig", error.Message);
            Assert.Equal(before, await File.ReadAllBytesAsync(pdfPath, timeout.Token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealPublicationHasInternalContentsLinksAndChapterBookmarks()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_PUBLICATION_LAYOUT_LIVE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "missum-publication-contents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Publikation.md");
            var body = string.Join("\n\n", Enumerable.Repeat(
                "Die Prüfung beschreibt Annahmen, Gültigkeitsgrenzen und nachvollziehbare Belege des Modells.", 35));
            await File.WriteAllTextAsync(source, "# Erdpolumkehr\n\n## Zusammenfassung\n\nÜberblick über die Untersuchung."
                + "\n\n## Modell\n\n### Prüfung\n\n" + body
                + "\n\n## Modell\n\n### Prüfung\n\n" + body
                + "\n\n## Geltung $R/R_c$\n\nDie Annahmen sind eingeschränkt."
                + "\n\n## Quellen\n\nDie Originalquellen bleiben überprüfbar.");
            using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var pdfPath = await exporter.EnsureCurrentAsync(source, sourceChanged: true, scientificPublication: true, cancellationToken: timeout.Token);
            Assert.NotNull(pdfPath);
            using var pdf = PdfDocument.Open(pdfPath);
            var contentsPage = pdf.GetPages().Single(page => page.GetWords().Any(word => word.Text == "Inhaltsverzeichnis"));
            var destinations = contentsPage.GetAnnotations().Select(annotation => annotation.Action).OfType<GoToAction>().ToArray();
            Assert.Equal(7, destinations.Length);
            Assert.DoesNotContain("$R/R_c$", string.Join(' ', contentsPage.GetWords().Select(word => word.Text)));
            Assert.All(destinations, action => Assert.InRange(action.Destination.PageNumber, 1, pdf.NumberOfPages));
            Assert.Contains(destinations, action => action.Destination.PageNumber > contentsPage.Number);
            Assert.True(pdf.TryGetBookmarks(out var bookmarks));
            var chapters = bookmarks.GetNodes().OfType<DocumentBookmarkNode>().ToArray();
            var first = Assert.Single(chapters, bookmark => bookmark.Title == "1 Modell");
            var second = Assert.Single(chapters, bookmark => bookmark.Title == "2 Modell");
            Assert.True(second.PageNumber > first.PageNumber, "Repeated titles still navigate to their distinct chapters.");
            Assert.Contains(chapters, bookmark => bookmark.Title == "1.1 Prüfung");
            Assert.Contains(chapters, bookmark => bookmark.Title == "2.1 Prüfung");
            Assert.Contains(chapters, bookmark => bookmark.Title == "4 Quellen");
            Assert.Contains(destinations, action => action.Destination.PageNumber == second.PageNumber);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealPublicationRenumbersLetteredSubsectionsAndRetainsSemanticTitleNumber()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_PUBLICATION_LAYOUT_LIVE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "missum-publication-lettered-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var extension = Section("sec-extension", "2a Erweiterung", order: 2) with
            {
                Data = JsonSerializer.SerializeToElement(new { title = "2a Erweiterung", status = "completed", order = 2,
                    contentMarkdown = "### 2a.1 Geometrie\n\nSiehe Kap. 2a.1 und Abschn. 2a.1.\n\n#### 2a.1.1 Abgrenzung\n\nDer Geltungsbereich bleibt eingeschränkt." }),
            };
            var state = State(Section("sec-intro", "1 Grundlagen", order: 1), extension) with { Title = "3 Dimensionen" };
            var source = Path.Combine(root, "Publikation.md");
            await File.WriteAllTextAsync(source, ScientificPublicationService.FormatCanonicalPublication(state, [], new([], [], [], [])));
            using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var pdfPath = await exporter.EnsureCurrentAsync(source, sourceChanged: true, scientificPublication: true, cancellationToken: timeout.Token);
            Assert.NotNull(pdfPath);
            using var pdf = PdfDocument.Open(pdfPath);
            var text = string.Join(' ', pdf.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
            Assert.Contains("3 Dimensionen", text);
            Assert.Contains("2.1 Geometrie", text);
            Assert.Contains("2.1.1 Abgrenzung", text);
            Assert.Contains("Kap. 2.1", text);
            Assert.Contains("Abschn. 2.1", text);
            Assert.DoesNotContain("2a.1", text);
            Assert.DoesNotContain("1.2.1 2a", text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DirectManuscriptNumbersTopicsAndBibliographyHierarchyWithoutChangingNumericSubjects()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_PUBLICATION_LAYOUT_LIVE") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "missum-publication-bibliography", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Publikation.md");
            const string manuscript = """
                # 3 Dimensionen

                ## Zusammenfassung
                Eine kurze Übersicht.

                ## **1.** Grundlagen
                Siehe Abschnitt 12.2 für den Datensatz. Die Energie beträgt 1.5 eV.

                ### 1.1 Ansatz
                Die Voraussetzungen bleiben ausdrücklich festgehalten.

                $$x=1.$$

                ![Erster Plot](FIGUREPATH)

                #### 1.1.1 Geltung
                Der Beweis gilt im angegebenen Bereich.

                ##### 1.1.1.1 Voraussetzung
                Eine weitere Voraussetzung.

                ###### 1.1.1.1.1 Folgerung
                Eine ausdrücklich begrenzte Folgerung.

                ### 1.5 eV
                Auch unter dem alten Kapitel 1 bleibt dieser Zahlenwert erhalten.

                ## 1. Wiederholte Nummer
                Doppelte alte Nummern bleiben als Verweise unverändert: Abschnitt 1.

                $$y=2.$$

                ![Zweiter Plot](FIGUREPATH)

                #### Direkter Unterpunkt
                Ein übersprungener Markdown-Level erzeugt keine Phantomkapitel.

                ### 1.1 Wiederverwendete Unterkapitelnummer
                Eine falsche alte Elternnummer ändert die Gliederung nicht.

                ## 3 Dimensionen
                Die Zahl gehört zum Titel.

                $$z=3.\tag{A}$$

                ### 1.5 eV
                Dieser Zahlenwert ist keine Abschnittsnummer.

                ## 12. Literatur und Daten
                ### 12.1 Fachliteratur
                - **[Bohm1952a]** D. Bohm, *Hidden Variables I*. [DOI](https://doi.org/10.1103/PhysRev.85.166).

                ### 12.2 Forschungsdaten
                - **[HensenData2015]** B. Hensen et al. Offene Ereignisdaten. [Datensatz](https://doi.org/10.4121/uuid:6e19e9b2-4a2d-40b5-8dd3-a660bf3c0a31).
                """;
            var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var imageName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant() + ".png";
            Directory.CreateDirectory(Path.Combine(root, "figures"));
            await File.WriteAllBytesAsync(Path.Combine(root, "figures", imageName), image);
            var authored = manuscript.Replace("FIGUREPATH", "figures/" + imageName, StringComparison.Ordinal);
            await File.WriteAllTextAsync(source, authored);
            using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var path = await exporter.EnsureCurrentAsync(source, sourceChanged: true, scientificPublication: true, cancellationToken: timeout.Token);
            Assert.NotNull(path);
            using var pdf = PdfDocument.Open(path);
            Assert.True(pdf.TryGetBookmarks(out var bookmarks));
            var chapters = bookmarks.GetNodes().OfType<DocumentBookmarkNode>().ToArray();
            string[] expected = ["1 Grundlagen", "1.1 Ansatz", "1.1.1 Geltung", "1.1.1.1 Voraussetzung", "1.1.1.1.1 Folgerung", "1.2 1.5 eV", "2 Wiederholte Nummer",
                "2.1 Direkter Unterpunkt", "2.2 Wiederverwendete Unterkapitelnummer", "3 3 Dimensionen", "3.1 1.5 eV", "4 Literatur und Daten",
                "4.1 Fachliteratur", "4.2 Forschungsdaten"];
            foreach (var caption in expected) Assert.Single(chapters, bookmark => bookmark.Title == caption);
            Assert.DoesNotContain(chapters, bookmark => bookmark.Title.StartsWith("1 3 Dimensionen", StringComparison.Ordinal));
            var contents = pdf.GetPages().Single(page => page.GetWords().Any(word => word.Text == "Inhaltsverzeichnis"));
            var links = contents.GetAnnotations().Select(annotation => annotation.Action).OfType<GoToAction>().ToArray();
            Assert.Equal(expected.Length + 1, links.Length); // Unnumbered abstract remains navigable.
            foreach (var chapter in chapters.Where(bookmark => expected.Contains(bookmark.Title)))
                Assert.Contains(links, link => link.Destination.PageNumber == chapter.PageNumber);
            var text = string.Join(' ', pdf.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
            Assert.Contains("Abschnitt 4.2", text);
            Assert.Contains("Abschnitt 1.", text); // Ambiguous old numbering must not silently redirect.
            Assert.Contains("[Bohm1952a]", text);
            Assert.Contains("[HensenData2015]", text);
            Assert.Contains("(1.1)", text);
            Assert.Contains("(2.1)", text);
            Assert.Contains("(A)", text);
            Assert.Contains("Abbildung 1.1", text);
            Assert.Contains("Abbildung 2.1", text);
            var externalLinks = pdf.GetPages().SelectMany(page => page.GetAnnotations()).Select(annotation => annotation.Action)
                .OfType<UriAction>().Select(action => action.Uri).ToArray();
            Assert.Contains("https://doi.org/10.1103/PhysRev.85.166", externalLinks);
            Assert.Contains("https://doi.org/10.4121/uuid:6e19e9b2-4a2d-40b5-8dd3-a660bf3c0a31", externalLinks);
            Assert.Equal(authored, await File.ReadAllTextAsync(source));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ResearchWorkingState State(params ResearchWorkingItem[] items) =>
        new("project", 1, 1, "Erdpolumkehr", items, DateTimeOffset.UnixEpoch);

    private static ResearchWorkingItem SimulationSection(ResearchExperiment[] experiments) =>
        new("sec-model", "section", 1, null, JsonSerializer.SerializeToElement(new
        {
            title = "Modell", contentMarkdown = "Die Versuche prüfen das beschriebene Modell.", status = "completed", order = 0,
            experimentIds = experiments.Select(item => item.Id).ToArray(),
            simulationDescriptions = experiments.Select(item => new { experimentId = item.Id, equations = "$x(t)=t$",
                description = "Die gewählten Parameter und der begrenzte Zeitbereich definieren den Vergleich." }).ToArray(),
        }), DateTimeOffset.UnixEpoch);

    private static ResearchWorkingItem Section(string id, string title, int order = 0) =>
        new(id, "section", 1, null, JsonSerializer.SerializeToElement(new
        {
            title, contentMarkdown = "Das Modell und seine Annahmen werden im angegebenen Geltungsbereich beschrieben.", status = "completed", order,
        }), DateTimeOffset.UnixEpoch);

    private static ResearchExperiment Experiment(string id, string path, string status, bool measured = true) =>
        new(id, "project", "", "[]", "[]", "{}", "research.code.execute model.py", "{}",
            measured ? JsonSerializer.Serialize(new
            {
                runs = new[] { new { exitCode = 0, runId = "real-run", snapshotId = "real-snapshot",
                    inputHashes = new Dictionary<string, string> { ["work/model.py"] = new string('b', 64) },
                    outputHashes = new Dictionary<string, string> { [path] = new string('a', 64) } } },
            }) : "{}", "", "[]", status, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
