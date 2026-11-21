using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Memory;
using Missum.Core.Models;
using Missum.Infrastructure.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace Missum.Tests;

public sealed class UserProfileAndRecognitionTests
{
    private static UserMemoryEntry Entry(
        string content,
        UserMemoryScopeKind scope = UserMemoryScopeKind.Global,
        string? projectKey = null,
        UserMemoryStatus status = UserMemoryStatus.Active,
        UserMemoryKind kind = UserMemoryKind.Preference,
        int evidence = 1,
        DateTimeOffset? createdAt = null,
        Guid? id = null,
        string? sourceMessageId = "message-0f0f0f0f")
        => new(
            id ?? Guid.NewGuid(),
            kind,
            scope,
            status,
            content,
            UserMemoryKey.ContentKey(content),
            projectKey,
            scope == UserMemoryScopeKind.Project ? "project-1" : null,
            sourceMessageId,
            "session-1",
            "test",
            1,
            null,
            evidence,
            createdAt ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            createdAt ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            status == UserMemoryStatus.Active ? createdAt ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) : null);

    [Fact]
    public void TheSameSetOfMemoriesAlwaysRendersTheIdenticalProfile()
    {
        var entries = new[]
        {
            Entry("Antworte auf Deutsch."),
            Entry("Keine Emojis.", createdAt: new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)),
            Entry("Nur dieses Projekt: Build vor jedem Test.", UserMemoryScopeKind.Project, "workspace:C:/Projekte/API"),
        };

        var first = UserProfileFormatter.Build(entries, "workspace:C:/Projekte/API");
        var shuffled = UserProfileFormatter.Build(entries.Reverse().ToArray(), "workspace:C:/Projekte/API");
        Assert.Equal(first.Text, shuffled.Text);
        Assert.Equal(first.Fingerprint, shuffled.Fingerprint);
        Assert.Equal(first.Included.Select(entry => entry.Id), shuffled.Included.Select(entry => entry.Id));

        // Project rules come first, the rest stays in the order the user gave them.
        Assert.StartsWith("Nur dieses Projekt", first.Included[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileForOneProjectNeverShowsTheRulesOfAnother()
    {
        var api = Entry("Build vor jedem Test.", UserMemoryScopeKind.Project, "workspace:C:/Projekte/API");
        var other = Entry("Kein Debugging im Produktionszweig.", UserMemoryScopeKind.Project, "workspace:C:/Projekte/Andere");
        var global = Entry("Erkläre in ganzen Sätzen.");

        var forApi = UserProfileFormatter.Build([api, other, global], "workspace:C:/Projekte/API");
        Assert.Contains("Build vor jedem Test.", forApi.Text, StringComparison.Ordinal);
        Assert.Contains("Erkläre in ganzen Sätzen.", forApi.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Produktionszweig", forApi.Text, StringComparison.Ordinal);

        var withoutProject = UserProfileFormatter.Build([api, other, global], projectKey: null);
        Assert.DoesNotContain("Build vor jedem Test.", withoutProject.Text, StringComparison.Ordinal);
        Assert.Contains("Erkläre in ganzen Sätzen.", withoutProject.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProfileLineNamesTheSessionAndMessageItCameFrom()
    {
        var result = UserProfileFormatter.Build([Entry("Tabellen statt Fließtext.", sourceMessageId: "message-12345678")]);
        Assert.Contains("(Quelle: Sitzung session1, Nachricht message1", result.Text, StringComparison.Ordinal);
        Assert.Contains("[global; Arbeitsweise]", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProfileStaysInsideItsTokenBudgetAndNeverShowsHalfARule()
    {
        var entries = Enumerable.Range(0, 60)
            .Select(index => Entry(
                $"Vorgabe {index}: " + new string('x', 180),
                createdAt: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(index)))
            .ToArray();

        var result = UserProfileFormatter.Build(entries, tokenBudget: 1_000);
        Assert.True(result.EstimatedTokens <= 1_000, $"Profil schätzt {result.EstimatedTokens} Token.");
        Assert.True(result.Included.Count < entries.Length);
        Assert.All(result.Included, entry => Assert.Contains(entry.Content, result.Text, StringComparison.Ordinal));
        Assert.All(result.ExcludedByBudget, entry => Assert.DoesNotContain(entry.Content, result.Text, StringComparison.Ordinal));
        Assert.Contains("NUTZERPROFIL", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedAndCoveredStatementsAreFoldedIntoTheMorePreciseOne()
    {
        var shortRule = Entry("Kurze Antworten.");
        var longRule = Entry("Kurze Antworten mit Quellenangabe.");
        var exception = Entry("Kurze Antworten, außer bei Code.");

        var result = UserProfileFormatter.Build([shortRule, longRule, exception]);
        // The long rule and the exception remain; the covered short one is folded away.
        Assert.Equal(2, result.Text.Split('\n')
            .Count(line => line.StartsWith("- [global; Arbeitsweise] Kurze Antworten", StringComparison.Ordinal)));
        Assert.Contains("Kurze Antworten mit Quellenangabe.", result.Text, StringComparison.Ordinal);
        Assert.Contains("außer bei Code", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestionsAreStoredButNotLoadedIntoTheProfile()
    {
        var result = UserProfileFormatter.Build([Entry("Vielleicht lieber Listen.", status: UserMemoryStatus.Suggested)]);
        Assert.DoesNotContain("Vielleicht lieber Listen.", result.Text, StringComparison.Ordinal);
        Assert.Empty(result.Included);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void UnicodeAndSmallRequestedBudgetsCannotSplitInstructions()
    {
        var entries = Enumerable.Range(0, 12).Select(i => Entry($"{i}. " + string.Concat(Enumerable.Repeat("🧬実μ", 34))));
        var profile = UserProfileFormatter.Build(entries);
        Assert.InRange(Encoding.UTF8.GetByteCount(profile.Text), 1, 1_000);
        Assert.NotEmpty(profile.ExcludedByBudget);
        Assert.Empty(UserProfileFormatter.Build(entries, tokenBudget: 10).Text);
    }

    [Theory]
    [InlineData("Künftig bitte immer ausführlicher antworten.", true, true, false, false)]
    [InlineData("Ab jetzt keine Tabellen in Antworten.", true, true, false, false)]
    [InlineData("In diesem Projekt gilt: erst Test, dann Code.", true, false, false, true)]
    [InlineData("Vergiss diese Vorgabe bitte.", true, false, true, false)]
    [InlineData("Was hast du dir zu meiner Arbeitsweise gemerkt?", true, false, false, false)]
    [InlineData("Nur für diese Aufgabe darfst du kürzen.", false, false, false, false)]
    [InlineData("Du hast es wieder falsch gemacht, stattdessen bitte so.", true, false, false, false)]
    [InlineData("Bitte merke dir: Nutze Markdown-Tabellen für Vergleiche.", true, true, false, false)]
    [InlineData("Ich bevorzuge Dezimalzahlen mit Komma.", true, true, false, false)]
    [InlineData("Lösche die temporäre Datei im Explorer.", false, false, false, false)]
    public void TheScannerRecognisesLastingStatementsAndLetsPlainQuestionsPass(
        string text,
        bool worthReviewing,
        bool explicitPermanence,
        bool forget,
        bool projectOnly)
    {
        var intent = UserMemoryIntentScanner.Scan(text);
        Assert.Equal(worthReviewing, intent.WorthReviewing);
        Assert.Equal(explicitPermanence, intent.ExplicitPermanence);
        Assert.Equal(forget, intent.ForgetRequest);
        Assert.Equal(projectOnly, intent.ProjectOnly);
    }

    [Fact]
    public void TheScannerTreatsProfileQuestionsAndCorrectionsAsWorthReviewing()
    {
        Assert.True(UserMemoryIntentScanner.Scan("Was hast du dir zu meiner Arbeitsweise gemerkt?").AsksForStoredProfile);
        Assert.True(UserMemoryIntentScanner.Scan("Was hast du dir zu meiner Arbeitsweise gemerkt?").RequiresImmediateReview);
        Assert.True(UserMemoryIntentScanner.Scan("Du hast es wieder falsch gemacht, stattdessen bitte so.").RepeatedCorrection);
        Assert.True(UserMemoryIntentScanner.Scan("Nur für diese Aufgabe darfst du kürzen.").TemporaryOnly);
        Assert.False(UserMemoryIntentScanner.Scan("Wie wird das Wetter?").WorthReviewing);
        Assert.False(UserMemoryIntentScanner.Scan("").WorthReviewing);
    }

    [Fact]
    public void ProjectOnlyAndTemporarySignalsAreSeparate()
    {
        var project = UserMemoryIntentScanner.Scan("Nur in diesem Projekt keine Screenshots.");
        Assert.True(project.ProjectOnly);
        Assert.False(project.TemporaryOnly);

        var once = UserMemoryIntentScanner.Scan("Nur diesmal darfst du die Datei löschen.");
        Assert.True(once.TemporaryOnly);
    }

    [Fact]
    public void TheRecognitionAnswerIsParsedEvenWhenTheModelWrapsItInFences()
    {
        var raw = """
            ```json
            {"memories":[{"content":"Antworte in ganzen Sätzen.","kind":"preference","scope":"global","certainty":"confirmed","replaces":null}],
             "forget":["message-alt"],"scopeChanges":[{"id":"deadbeef","scope":"project"}],"summary":"gemerkt"}
            ```
            """;
        var parsed = UserMemoryRecognitionService.Parse(raw);
        var candidate = Assert.Single(parsed.Candidates);
        Assert.Equal("Antworte in ganzen Sätzen.", candidate.Content);
        Assert.Equal(UserMemoryKind.Preference, candidate.Kind);
        Assert.Equal(UserMemoryScopeKind.Global, candidate.Scope);
        Assert.True(candidate.Confirmed);
        Assert.Single(parsed.Forget);
        var change = Assert.Single(parsed.ScopeChanges);
        Assert.Equal("deadbeef", change.Reference);
        Assert.Equal(UserMemoryScopeKind.Project, change.Scope);
        Assert.Equal("gemerkt", parsed.Summary);
    }

    [Fact]
    public void AnEmptyOrUnreadableRecognitionAnswerStoresNothing()
    {
        var empty = UserMemoryRecognitionService.Parse("""{"memories":[],"forget":[],"scopeChanges":[],"summary":null}""");
        Assert.Empty(empty.Candidates);
        Assert.Empty(empty.Forget);
        Assert.Throws<InvalidOperationException>(() => UserMemoryRecognitionService.Parse("das Modell redet weiter"));
    }

    [Fact]
    public void AmbiguousCertaintyBecomesASuggestionNotAnActiveRule()
    {
        var parsed = UserMemoryRecognitionService.Parse("""
            {"memories":[{"content":"Vielleicht weniger Aufzählungen.","kind":"preference","scope":"global","certainty":"ambiguous"}]}
            """);
        Assert.False(Assert.Single(parsed.Candidates).Confirmed);
    }

    [Fact]
    public void CleanStatementRemovesQuotesAndRefusesPassagesThatAreTooShort()
    {
        Assert.Equal("Antworte knapp.", UserMemoryRecognitionService.CleanStatement("  „Antworte knapp.“  "));
        Assert.Equal(string.Empty, UserMemoryRecognitionService.CleanStatement("ja"));
        Assert.Empty(UserMemoryRecognitionService.CleanStatement(new string('x', 600)));
    }

    [Fact]
    public async Task BackfillResumesWithinTheSameSessionAfterItsCpuBatchLimit()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        await using var memory = new SqliteProjectMemoryStore(new ProjectMemoryOptions { SharedDataDirectory = environment.Directory });
        environment.TrackDatabase(Path.Combine(environment.Directory, new ProjectMemoryOptions().DatabaseFileName));
        var chats = environment.Get<IChatRepository>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        var recognition = new UserMemoryRecognitionService(new UserMemoryService(memory, chats), memory);
        var backfill = new UserMemoryBackfillService(chats, memory, recognition, connection);
        var session = await chats.CreateSessionAsync("Archiv", ChatMode.General);
        var limit = UserMemoryBackfillService.MaximumMessagesPerBatch;
        for (var index = 0; index <= limit; index++)
        {
            await chats.AddTurnAsync(session.Id, $"Wie spät ist es? Nummer {index}");
        }

        var first = await backfill.RunBatchAsync(null, CancellationToken.None);
        Assert.False(first.Finished);
        Assert.Equal(limit, first.ScannedMessages);
        var second = await backfill.RunBatchAsync(null, CancellationToken.None);
        Assert.True(second.Finished);
        Assert.Equal(1, second.ScannedMessages);
        Assert.Equal(limit + 1, (await memory.GetBackfillCursorAsync())?.ProcessedMessages);
    }
}
