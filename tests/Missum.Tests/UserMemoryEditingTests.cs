using Missum.App.Services;
using Missum.Core.Memory;

namespace Missum.Tests;

public sealed class UserMemoryEditingTests
{
    [Fact]
    public async Task EditingTheMemoryRevisesForgetsAndAddsLines()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        await using var memory = new Missum.Infrastructure.Memory.SqliteProjectMemoryStore(new Missum.Infrastructure.Memory.ProjectMemoryOptions { SharedDataDirectory = environment.Directory });
        environment.TrackDatabase(Path.Combine(environment.Directory, new Missum.Infrastructure.Memory.ProjectMemoryOptions().DatabaseFileName));
        await memory.InitializeAsync();
        var store = memory;
        foreach (var text in new[] { "Antworte auf Deutsch.", "Nutze kurze Absätze.", "Code immer mit Kommentaren." })
            await store.SaveAsync(new UserMemoryDraft(UserMemoryKind.Preference, UserMemoryScopeKind.Global, text, "test", UserMemoryStatus.Active));

        var shown = await UserMemoryEditing.ListGlobalAsync(store, CancellationToken.None);
        Assert.Equal("Antworte auf Deutsch.\r\nNutze kurze Absätze.\r\nCode immer mit Kommentaren.".Replace("\r\n", Environment.NewLine, StringComparison.Ordinal),
            UserMemoryEditing.ToText(shown));

        // Second line changed, third deleted, one new line.
        var plan = UserMemoryEditing.Plan(shown, "Antworte auf Deutsch.\nNutze sehr kurze Absätze.\n\nNenne immer die Quelle.\n");
        Assert.Equal("Nutze sehr kurze Absätze.", Assert.Single(plan.Revised).Content);
        Assert.Equal("Code immer mit Kommentaren.", Assert.Single(plan.Forgotten).Content);
        Assert.Equal(["Nenne immer die Quelle."], plan.Added);
        await UserMemoryEditing.ApplyAsync(store, plan, CancellationToken.None);

        var after = (await UserMemoryEditing.ListGlobalAsync(store, CancellationToken.None)).Select(e => e.Content).ToList();
        Assert.Contains("Antworte auf Deutsch.", after);
        Assert.Contains("Nenne immer die Quelle.", after);
        Assert.Equal(3, after.Count);

        var removeAll = UserMemoryEditing.Plan(await UserMemoryEditing.ListGlobalAsync(store, CancellationToken.None), "Antworte auf Deutsch.");
        Assert.Equal(2, removeAll.Forgotten.Count);
        await UserMemoryEditing.ApplyAsync(store, removeAll, CancellationToken.None);
        Assert.Equal(["Antworte auf Deutsch."], (await UserMemoryEditing.ListGlobalAsync(store, CancellationToken.None)).Select(e => e.Content));
        // Nothing changed: nothing to apply.
        Assert.True(UserMemoryEditing.Plan(await UserMemoryEditing.ListGlobalAsync(store, CancellationToken.None), "Antworte auf Deutsch.\n").IsEmpty);
    }
}
