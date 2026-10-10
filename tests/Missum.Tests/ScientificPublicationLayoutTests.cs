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
    public void MissingOrderInMixedOutlineReportsExactSectionInsteadOfInsertingAtBeginning()
    {
        var missing = new ResearchWorkingItem("sec-new", "section", 3, null,
            JsonSerializer.SerializeToElement(new { title = "Neues Thema", contentMarkdown = "Neue fachliche Ergänzung.", status = "completed" }),
            DateTimeOffset.UnixEpoch);
        var state = State(Section("sec-intro", "1 Einführung", order: 1), missing);
        var error = Assert.Throws<ScientificPublicationContentException>(() => ScientificPublicationService.OrderedPublicationSections(state));
        var target = Assert.Single(error.Sections);
        Assert.Equal("sec-new", target.Id);
        Assert.Equal(3, target.Revision);
        Assert.Contains("order", error.Message);
        Assert.Contains("fehlende Werte gelten nicht als erste Position", error.Message);
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
    public void UnassignedMeasuredPlotEntersPublicationWithoutInventingScientificConclusion()
    {
        var state = State(Section("sec-model", "Modell"));
        var results = new ResearchResultSnapshot([], [Experiment("simulation-real", "artifacts/polumkehr_plot.png", "ProcessSucceeded")], [], []);
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], results);

        Assert.Contains("## Simulationsabbildungen", markdown);
        Assert.Contains("![polumkehr plot · Experiment simulation-real](artifacts/polumkehr_plot.png)", markdown);
        Assert.Contains("für sich allein kein Beweis", markdown);
        Assert.Contains("Vorläufiger Forschungsstand", markdown);
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
        var state = State(Section("sec-model", "Modell"));
        var experiments = Enumerable.Range(1, 20).Select(index => Experiment("simulation-" + index,
            "artifacts/plot_" + index + ".png", "ProcessSucceeded")).ToArray();
        var markdown = ScientificPublicationService.FormatCanonicalPublication(state, [], new([], experiments, [], []));
        foreach (var experiment in experiments) Assert.Contains("Experiment " + experiment.Id + "]", markdown);
        var tooMany = Enumerable.Range(1, 65).Select(index => Experiment("simulation-" + index,
            "artifacts/plot_" + index + ".png", "ProcessSucceeded")).ToArray();
        var error = Assert.Throws<ScientificPublicationContentException>(() =>
            ScientificPublicationService.FormatCanonicalPublication(state, [], new([], tooMany, [], [])));
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
        var markdown = ScientificPublicationService.FormatCanonicalPublication(State(oldSection, Section("sec-end", "Einordnung", order: 1)),
            [], new([], [old, latest], [], []));
        Assert.Contains("Experiment simulation-latest](work/fig1_time_series.png)", markdown);
        Assert.DoesNotContain("Experiment simulation-old]", markdown);
        Assert.Equal(1, Regex.Count(markdown, Regex.Escape("work/fig1_time_series.png")));
    }

    [Fact]
    public void ExplicitFigureAssignmentPreventsDuplicateAutomaticFigure()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Ergebnisse", contentMarkdown = "Der Modellbereich ist eingeschränkt.",
            status = "completed", experimentIds = MeasuredExperimentIds,
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
        Assert.Contains("research.update(title)", diagnostic);
        Assert.DoesNotContain("konnte nicht eindeutig zugeordnet", diagnostic);
    }

    [Fact]
    public void OnlyProtectedReviewForExactCurrentStateRemovesPreliminaryLabel()
    {
        var state = State(Section("sec-model", "Modell"));
        var receipt = new ResearchVerification("review", state.ProjectId, "publication-review", state.ProjectId, "publication",
            ScientificResearchReview.Method, "ReviewedWithEvidence", JsonSerializer.Serialize(new
            {
                protocol = ScientificResearchReview.Protocol, projectId = state.ProjectId, revision = state.Revision,
                publicationRevision = state.PublicationRevision, stateSha256 = ScientificResearchReview.Fingerprint(state), ready = true,
            }), DateTimeOffset.UnixEpoch);
        var results = new ResearchResultSnapshot([], [], [receipt], []);

        Assert.Contains("Vorläufiger Forschungsstand", ScientificPublicationService.FormatCanonicalPublication(state, [], results));
        var trusted = ScientificPublicationService.FormatCanonicalPublication(state, [], results, [receipt]);
        Assert.Contains("Geprüfter Forschungsstand", trusted);
        Assert.DoesNotContain("Vorläufiger Forschungsstand", trusted);
        Assert.Contains("Vorläufiger Forschungsstand", ScientificPublicationService.FormatCanonicalPublication(
            state with { Title = "Überarbeitetes Modell" }, [], results, [receipt]));
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
            var first = Assert.Single(chapters, bookmark => bookmark.Title == "1.1 Modell");
            var second = Assert.Single(chapters, bookmark => bookmark.Title == "1.2 Modell");
            Assert.True(second.PageNumber > first.PageNumber, "Repeated titles still navigate to their distinct chapters.");
            Assert.Contains(chapters, bookmark => bookmark.Title == "1.1.1 Prüfung");
            Assert.Contains(chapters, bookmark => bookmark.Title == "1.2.1 Prüfung");
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
            Assert.Contains("1.2.1 Geometrie", text);
            Assert.Contains("1.2.1.1 Abgrenzung", text);
            Assert.Contains("Kap. 1.2.1", text);
            Assert.Contains("Abschn. 1.2.1", text);
            Assert.DoesNotContain("2a.1", text);
            Assert.DoesNotContain("1.2.1 2a", text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ResearchWorkingState State(params ResearchWorkingItem[] items) =>
        new("project", 1, 1, "Erdpolumkehr", items, DateTimeOffset.UnixEpoch);

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
