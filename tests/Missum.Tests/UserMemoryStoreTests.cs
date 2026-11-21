using Missum.Core.Memory;
using Missum.Infrastructure.Memory;

namespace Missum.Tests;

public sealed class UserMemoryStoreTests
{
    private static UserMemoryDraft Global(
        string content,
        UserMemoryStatus status = UserMemoryStatus.Active,
        string? sourceMessageId = null)
        => new(UserMemoryKind.Preference, UserMemoryScopeKind.Global, content, "test", status,
            SourceMessageId: sourceMessageId, SessionId: "session-global");

    private static UserMemoryDraft Project(
        string content,
        string projectKey,
        UserMemoryStatus status = UserMemoryStatus.Active,
        string? projectId = "project-9")
        => new(UserMemoryKind.ProjectRule, UserMemoryScopeKind.Project, content, "test", status,
            projectKey, projectId, SessionId: "session-project");

    [Fact]
    public async Task LegacyMigrationCarriesOnlyManualProjectRulesIntoTheNewStoreOnce()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        await store.InitializeAsync();

        var scope = ProjectMemoryScope.FromWorkspace(Path.Combine(fixture.Directory, "Bauakte"), "project-77");
        await store.CreateAsync(new ProjectMemoryDraft(
            scope,
            ProjectMemoryKind.Requirement,
            "Die Bauakte bleibt im Projektordner.",
            "manual",
            DateTimeOffset.UtcNow));
        await store.CreateAsync(new ProjectMemoryDraft(
            scope,
            ProjectMemoryKind.Decision,
            "Zuerst wird gemessen, dann gezeichnet.",
            "manual"));
        await store.CreateAsync(new ProjectMemoryDraft(
            scope,
            ProjectMemoryKind.Requirement,
            "Automatisch übernommener Satz ohne sichere Nutzerquelle.",
            "legacy-client/legacy-session",
            DateTimeOffset.UtcNow));

        Assert.Equal(2, await store.MigrateLegacyEntriesAsync());
        Assert.Equal(0, await store.MigrateLegacyEntriesAsync());

        var rules = await store.ListAsync(new UserMemoryQuery(ProjectKey: scope.ProjectKey));
        Assert.Equal(2, rules.Count);
        Assert.All(rules, rule =>
        {
            Assert.Equal(UserMemoryScopeKind.Project, rule.Scope);
            Assert.Equal(scope.ProjectKey, rule.ProjectKey);
            Assert.Equal("legacy_project_memory", rule.Origin);
            Assert.StartsWith("project-memory:", rule.SourceMessageId, StringComparison.Ordinal);
        });

        // The confirmed statement stays confirmed, the unconfirmed one stays a suggestion.
        Assert.Contains(rules, rule => rule.Status == UserMemoryStatus.Active);
        Assert.Contains(rules, rule => rule.Status == UserMemoryStatus.Suggested);

        // Nothing of the original project memory was moved away.
        Assert.Equal(3, (await store.ListAsync(scope)).Count);
        Assert.True(await store.CheckIntegrityAsync());
    }

    [Fact]
    public async Task SavedMemoryRecordsContentKindScopeStatusSourceAndRevision()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var result = await store.SaveAsync(new UserMemoryDraft(
            UserMemoryKind.Instruction,
            UserMemoryScopeKind.Project,
            "Prüfungen immer zuerst bauen.",
            "chat",
            UserMemoryStatus.Active,
            "workspace:C:/Projekte/Bauakte",
            "project-9",
            "message-4711",
            "session-1"));

        var entry = result.Entry;
        Assert.True(result.Created);
        Assert.False(result.MergedWithExisting);
        Assert.Equal(UserMemoryKind.Instruction, entry.Kind);
        Assert.Equal(UserMemoryScopeKind.Project, entry.Scope);
        Assert.Equal(UserMemoryStatus.Active, entry.Status);
        Assert.Equal("Prüfungen immer zuerst bauen.", entry.Content);
        Assert.Equal("workspace:C:/Projekte/Bauakte", entry.ProjectKey);
        Assert.Equal("project-9", entry.ProjectId);
        Assert.Equal("message-4711", entry.SourceMessageId);
        Assert.Equal("session-1", entry.SessionId);
        Assert.Equal(1, entry.Revision);
        Assert.Equal(1, entry.EvidenceCount);
        Assert.Null(entry.SupersedesId);
        Assert.NotNull(entry.LastConfirmedAt);
        Assert.Equal(entry.CreatedAt, entry.UpdatedAt);
        Assert.Equal(UserMemoryKey.ContentKey(entry.Content), entry.ContentKey);

        var readBack = await ((IUserMemoryStore)store).GetAsync(entry.Id);
        Assert.NotNull(readBack);
        Assert.Equal(entry.ContentKey, readBack.ContentKey);
        Assert.Equal(entry.Revision, readBack.Revision);
    }

    [Fact]
    public async Task RepeatingTheSameStatementAddsEvidenceInsteadOfACopy()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var first = await store.SaveAsync(Global("Antworte in ganzen Sätzen.", UserMemoryStatus.Suggested, "message-a"));
        var second = await store.SaveAsync(Global("Antworte in ganzen Sätzen", UserMemoryStatus.Suggested, "message-b"));
        var replay = await store.SaveAsync(Global("Antworte in ganzen Sätzen.", UserMemoryStatus.Suggested, "message-b"));

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.True(second.MergedWithExisting);
        Assert.True(second.EvidenceAdded);
        Assert.False(replay.EvidenceAdded);
        Assert.Equal(first.Entry.Id, second.Entry.Id);
        Assert.Equal(2, second.Entry.EvidenceCount);
        Assert.Null(second.Entry.LastConfirmedAt);
        Assert.Equal(second.Entry.Revision, replay.Entry.Revision);
        Assert.Equal(second.Entry.EvidenceCount, replay.Entry.EvidenceCount);
        Assert.Single(await store.ListAsync(new UserMemoryQuery()));
    }

    [Fact]
    public async Task AnUnclearStatementStaysSuggestedUntilALaterMessageSettlesIt()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var saved = await store.SaveAsync(Global("Tabellen mögen kurz sein.", UserMemoryStatus.Suggested));
        var profileBefore = UserProfileFormatter.Build(await store.ListAsync(new UserMemoryQuery()));
        Assert.DoesNotContain("Tabellen", profileBefore.Text, StringComparison.Ordinal);
        Assert.Empty(await store.SearchAsync("Tabellen"));

        var confirmed = await store.SetStatusAsync(saved.Entry.Id, saved.Entry.Revision, UserMemoryStatus.Active);
        Assert.Equal(UserMemoryStatus.Active, confirmed.Status);
        Assert.NotNull(confirmed.LastConfirmedAt);

        var profileAfter = UserProfileFormatter.Build(await store.ListAsync(new UserMemoryQuery()));
        Assert.Contains("Tabellen mögen kurz sein.", profileAfter.Text, StringComparison.Ordinal);
        Assert.Single(await store.SearchAsync("Tabellen"));
    }

    [Fact]
    public async Task CorrectionReplacesTheOldRuleAndKeepsBothRowsAuditable()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var old = await store.SaveAsync(Global("Antworten kurz halten.", UserMemoryStatus.Active, "message-old"));

        var corrected = await store.ReviseAsync(
            old.Entry.Id,
            old.Entry.Revision,
            new UserMemoryDraft(
                UserMemoryKind.Correction,
                UserMemoryScopeKind.Global,
                "Antworten ausführlicher halten.",
                "chat",
                SourceMessageId: "message-new",
                SessionId: "session-global"));

        Assert.Equal(UserMemoryStatus.Active, corrected.Status);
        Assert.Equal(UserMemoryKind.Correction, corrected.Kind);
        Assert.Equal(old.Entry.Id, corrected.SupersedesId);
        Assert.Equal("message-new", corrected.SourceMessageId);

        var superseded = await ((IUserMemoryStore)store).GetAsync(old.Entry.Id);
        Assert.NotNull(superseded);
        Assert.Equal(UserMemoryStatus.Superseded, superseded.Status);
        Assert.Equal(old.Entry.Revision + 1, superseded.Revision);

        var profile = UserProfileFormatter.Build(await store.ListAsync(new UserMemoryQuery()));
        Assert.Contains("ausführlicher", profile.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Antworten kurz halten.", profile.Text, StringComparison.Ordinal);

        var replay = await store.SaveAsync(Global("Antworten kurz halten.", sourceMessageId: "message-old"));
        Assert.False(replay.Created);
        Assert.Equal(UserMemoryStatus.Superseded, replay.Entry.Status);

        await Assert.ThrowsAsync<ProjectMemoryVersionConflictException>(
            () => store.ReviseAsync(old.Entry.Id, old.Entry.Revision, Global("Nochmal kurz.")));
    }

    [Fact]
    public async Task ReplacingAProjectRuleWithAGlobalRuleUsesTheNewScope()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var old = await store.SaveAsync(Project("Dokumentiere Prüfungen in der Bauakte.", "workspace:example"));
        var corrected = await store.ReviseAsync(old.Entry.Id, old.Entry.Revision,
            Global("Dokumentiere Prüfungen im Änderungsprotokoll."));

        Assert.Equal(UserMemoryScopeKind.Global, corrected.Scope);
        Assert.Null(corrected.ProjectKey);
        Assert.Null(corrected.ProjectId);
        Assert.Equal(corrected.Id, Assert.Single(await store.ListAsync(new UserMemoryQuery())).Id);
    }

    [Fact]
    public async Task ForgettingRemovesTheRuleFromProjectionAndSearchAndBlocksItsReturn()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var saved = await store.SaveAsync(Global("Keine Emojis in Antworten.", UserMemoryStatus.Active, "message-emoji"));
        Assert.Contains(await store.SearchAsync("Emojis"),
            item => item.Content.Contains("Emojis", StringComparison.Ordinal));

        var forgotten = await store.ForgetAsync(saved.Entry.Id, saved.Entry.Revision);
        Assert.Equal(UserMemoryStatus.Forgotten, forgotten.Status);

        var effective = await store.ListAsync(new UserMemoryQuery());
        Assert.DoesNotContain(effective, entry => entry.Id == forgotten.Id);
        Assert.Empty(await store.SearchAsync("Emojis"));
        Assert.DoesNotContain("Keine Emojis",
            UserProfileFormatter.Build(effective).Text, StringComparison.Ordinal);

        // The same instruction from an older chat must not come back.
        var again = await store.SaveAsync(Global("Keine Emojis in Antworten.", UserMemoryStatus.Active, "message-old-2"));
        Assert.True(again.SuppressedByForgetmark);
        Assert.False(again.Created);

        // The tombstone row itself stays, with its revision advanced, so the change is auditable.
        var everything = await store.ListAsync(new UserMemoryQuery(IncludeForgotten: true));
        Assert.Contains(everything, item => item.Id == forgotten.Id && item.Status == UserMemoryStatus.Forgotten);
        Assert.DoesNotContain(await store.ListAsync(new UserMemoryQuery()), item => item.Id == forgotten.Id);
        Assert.True(await store.IsForgottenAsync("message-emoji", saved.Entry.ContentKey));
    }

    [Fact]
    public async Task ScopeChangeMovesOneRuleBetweenProjectAndGlobal()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var saved = await store.SaveAsync(Global("Commitnachrichten auf Deutsch.", UserMemoryStatus.Active));

        var projectScoped = await store.SetScopeAsync(
            saved.Entry.Id, saved.Entry.Revision, UserMemoryScopeKind.Project, "workspace:C:/Projekte/API", "project-5");
        Assert.Equal(UserMemoryScopeKind.Project, projectScoped.Scope);
        Assert.Equal("workspace:C:/Projekte/API", projectScoped.ProjectKey);

        // Another project of the same user does not see it any more.
        Assert.Empty(await store.ListAsync(new UserMemoryQuery(ProjectKey: "workspace:C:/Projekte/Andere")));
        Assert.Single(await store.ListAsync(new UserMemoryQuery(ProjectKey: "workspace:C:/Projekte/API")));

        var globalAgain = await store.SetScopeAsync(
            projectScoped.Id, projectScoped.Revision, UserMemoryScopeKind.Global);
        Assert.Null(globalAgain.ProjectKey);
        Assert.Single(await store.ListAsync(new UserMemoryQuery(ProjectKey: "workspace:C:/Projekte/Andere")));
    }

    [Fact]
    public async Task BackfillCannotRecreateTheOldScopeOfAMovedMemory()
    {
        await using var fixture = new UserMemoryFixture();
        const string rule = "Commitnachrichten auf Deutsch.";
        const string project = "workspace:commit-projekt";
        var past = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        await using (var store = fixture.CreateStore())
        {
            var saved = await store.SaveAsync(Global(rule, sourceMessageId: "first-message") with { ObservedAt = past });
            await store.SetScopeAsync(saved.Entry.Id, saved.Entry.Revision, UserMemoryScopeKind.Project, project);
        }

        await using var reopened = fixture.CreateStore();
        var replay = await reopened.SaveAsync(Global(rule, sourceMessageId: "unseen-old-message") with { ObservedAt = past });
        Assert.False(replay.Created);
        Assert.False(replay.EvidenceAdded);
        Assert.Empty(await reopened.ListAsync(new UserMemoryQuery()));
        Assert.Single(await reopened.ListAsync(new UserMemoryQuery(ProjectKey: project)));
        Assert.True((await reopened.SaveAsync(Global(rule, sourceMessageId: "fresh-message") with
        {
            ObservedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        })).Created);
    }

    [Fact]
    public async Task ListingSearchingAndForgettingRespectProjectBoundaries()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        const string rule = "Protokolltabellen immer im Prüfbericht aufführen.";
        const string firstProject = "workspace:project-one";
        const string secondProject = "workspace:project-two";
        var first = await store.SaveAsync(Project(rule, firstProject));

        Assert.Empty(await store.ListAsync(new UserMemoryQuery()));
        Assert.Empty(await store.SearchAsync("Protokolltabellen"));
        Assert.Empty(await store.SearchAsync("Protokolltabellen", secondProject));
        Assert.Equal(first.Entry.Id, Assert.Single(await store.SearchAsync("Protokolltabellen", firstProject)).Id);

        var forgotten = await store.ForgetAsync(first.Entry.Id, first.Entry.Revision);
        Assert.True((await store.SaveAsync(Project(rule, firstProject))).SuppressedByForgetmark);
        Assert.True((await store.SaveAsync(Project(rule, secondProject))).Created);
        Assert.True((await store.SaveAsync(Global(rule))).Created);
        Assert.Equal(2, (await store.SearchAsync("Protokolltabellen", secondProject)).Count);
        await Assert.ThrowsAsync<ProjectMemoryVersionConflictException>(
            () => store.SetStatusAsync(forgotten.Id, forgotten.Revision, UserMemoryStatus.Active));
    }

    [Fact]
    public async Task ProfileKeepsItsRevisionWhileNothingChangedAndAdvancesAfterAChange()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();
        var scopeKey = "workspace:C:/Projekte/Stabil";
        Assert.Equal(0, (await store.GetProfileAsync(scopeKey)).Revision);

        await store.SaveAsync(Global("Erkläre zuerst den Weg, dann das Ergebnis.", UserMemoryStatus.Active));
        var entries = await store.ListAsync(new UserMemoryQuery(ProjectKey: scopeKey));
        var built = UserProfileFormatter.Build(entries, scopeKey);
        await store.SaveProfileAsync(new UserProfileMemory(scopeKey, built.Text, 1, built.Fingerprint,
            built.EstimatedTokens, DateTimeOffset.UtcNow, built.Included.Select(entry => entry.Id).ToArray()));

        var stored = await store.GetProfileAsync(scopeKey);
        Assert.Equal(1, stored.Revision);
        var entriesAgain = await store.ListAsync(new UserMemoryQuery(ProjectKey: scopeKey));
        Assert.Equal(stored.Fingerprint, UserProfileFormatter.Build(entriesAgain, scopeKey).Fingerprint);

        await store.SaveAsync(Global("Lieber Tabellen statt Fließtext.", UserMemoryStatus.Active));
        var changed = UserProfileFormatter.Build(await store.ListAsync(new UserMemoryQuery(ProjectKey: scopeKey)), scopeKey);
        Assert.NotEqual(stored.Fingerprint, changed.Fingerprint);

        await store.SaveProfileAsync(new UserProfileMemory(scopeKey, changed.Text, stored.Revision + 1,
            changed.Fingerprint, changed.EstimatedTokens, DateTimeOffset.UtcNow,
            changed.Included.Select(entry => entry.Id).ToArray()));
        var secondProfile = await store.GetProfileAsync(scopeKey);
        Assert.Equal(2, secondProfile.Revision);
        Assert.Equal(changed.Fingerprint, secondProfile.Fingerprint);
        Assert.False(await store.SaveProfileAsync(stored with { Revision = 2 }));
        Assert.Equal(changed.Fingerprint, (await store.GetProfileAsync(scopeKey)).Fingerprint);

        // A second scope keeps its own row and is not overwritten by the first one.
        Assert.Equal(0, (await store.GetProfileAsync("workspace:C:/Projekte/Anderes")).Revision);
    }

    [Fact]
    public async Task BackfillPositionAndProcessedMessagesSurviveANewStoreInstance()
    {
        await using var fixture = new UserMemoryFixture();
        var cursorUpdatedAt = new DateTimeOffset(2026, 3, 1, 10, 0, 10, TimeSpan.Zero);
        await using (var store = fixture.CreateStore())
        {
            await store.InitializeAsync();
            await store.MarkMessageProcessedAsync("message-1", "session-1", "no-signal", null);
            await store.SaveBackfillCursorAsync(new UserMemoryBackfillCursor(
                "session-1",
                new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
                "message-1",
                new DateTimeOffset(2026, 3, 1, 10, 0, 5, TimeSpan.Zero),
                7,
                cursorUpdatedAt));
        }

        await using var restarted = fixture.CreateStore();
        var cursor = await restarted.GetBackfillCursorAsync();
        Assert.NotNull(cursor);
        Assert.Equal("session-1", cursor.LastSessionId);
        Assert.Equal("message-1", cursor.LastMessageId);
        Assert.Equal(7, cursor.ProcessedMessages);
        Assert.Equal(cursorUpdatedAt, cursor.UpdatedAt);
        Assert.True(await restarted.IsMessageProcessedAsync("message-1"));
        Assert.False(await restarted.IsMessageProcessedAsync("message-2"));

        // Replaying the same message updates the row instead of adding a second one.
        await restarted.MarkMessageProcessedAsync("message-1", "session-1", "stored", Guid.NewGuid());
        Assert.True(await restarted.IsMessageProcessedAsync("message-1"));
    }

    [Fact]
    public async Task MemoryInputIsCheckedBeforeItReachesTheTable()
    {
        await using var fixture = new UserMemoryFixture();
        await using var store = fixture.CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(new UserMemoryDraft(
                UserMemoryKind.ProjectRule, UserMemoryScopeKind.Project, "Ohne Projektschlüssel.", "test")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(new UserMemoryDraft(
                UserMemoryKind.Preference, UserMemoryScopeKind.Global, "Vergessen geht nicht per Speichern.", "test",
                UserMemoryStatus.Forgotten)));
    }

    private sealed class UserMemoryFixture : IAsyncDisposable
    {
        public UserMemoryFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"LocalAssistant-usermemory-{Guid.NewGuid():N}");
        }

        public string Directory { get; }

        public SqliteProjectMemoryStore CreateStore() => new(new ProjectMemoryOptions
        {
            SharedDataDirectory = Directory,
        });

        public ValueTask DisposeAsync()
        {
            TestSqlitePools.ClearDatabasePool(Path.Combine(Directory, new ProjectMemoryOptions().DatabaseFileName));
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
