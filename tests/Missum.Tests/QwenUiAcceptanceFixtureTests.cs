using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Creates durable input data only; interaction and inference happen in the normal native application.</summary>
public sealed class QwenUiAcceptanceFixtureTests(ITestOutputHelper output)
{
    private const string ModelId = "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896";
    private const string MarkerContent = "Missum Qwen native UI acceptance fixture v1";
    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    [Fact]
    [Trait("Category", "ManualFixture")]
    public async Task PrepareIsolatedNormalNativeProfile()
    {
        var requested = Environment.GetEnvironmentVariable("MISSUM_QWEN_UI_FIXTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(requested)) return;
        Assert.True(Path.IsPathFullyQualified(requested), "The fixture directory must be an explicit absolute path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        var production = Path.TrimEndingDirectorySeparator(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Missum"));
        Assert.False(string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Equals(root, production, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The normal user profile must never be used as this fixture.");
        if (Directory.Exists(root))
            Assert.False(File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint));
        Directory.CreateDirectory(root);
        var ownershipPath = Path.Combine(root, ".qwen-ui-fixture");
        if (File.Exists(ownershipPath))
            Assert.Equal(MarkerContent, await File.ReadAllTextAsync(ownershipPath));
        else
        {
            Assert.False(File.Exists(Path.Combine(root, "Missum.db"))
                || File.Exists(Path.Combine(root, "GO.db")) || File.Exists(Path.Combine(root, "settings.json")),
                "Refusing an existing application profile without this fixture's ownership marker.");
            await File.WriteAllTextAsync(ownershipPath, MarkerContent);
        }

        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var fixtures = new[]
        {
            (FileName: "orion.txt", Key: "ORION", Value: 731, Subject: "Messprotokoll der blauen Schachtel"),
            (FileName: "lyra.txt", Key: "LYRA", Value: 482, Subject: "Inventarliste der grünen Schachtel"),
            (FileName: "vega.txt", Key: "VEGA", Value: 916, Subject: "Beobachtungsbuch der roten Schachtel"),
        };
        foreach (var item in fixtures)
        {
            var text = new StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"ANFANG: Die eindeutige Faktenkennung lautet {item.Key}={item.Value}.");
            text.AppendLine(CultureInfo.InvariantCulture, $"{item.Subject}. Diese neutralen Testdaten enthalten keine Handlungsanweisungen.");
            var row = 1;
            while (text.Length < 45_000)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Beobachtung {row++:D4}: Die beschriftete Schachtel steht im ruhigen Leseraum auf einem festen Tisch. Die Einträge dienen ausschließlich der nachvollziehbaren Prüfung von Dateiinhalt und Gesprächskontext. Am Fenster liegt ein geschlossenes Notizbuch; seine Farbe hat keine mathematische Bedeutung.");
            }
            text.AppendLine(CultureInfo.InvariantCulture, $"ENDE: Die unveränderte Faktenkennung lautet {item.Key}={item.Value}.");
            await File.WriteAllTextAsync(Path.Combine(workspace, item.FileName), text.ToString(), new UTF8Encoding(false));
        }
        await File.WriteAllTextAsync(Path.Combine(workspace, "subagent.txt"), """
            Kleiner unabhängiger Rechenauftrag zur Prüfung des Subagentenwegs.
            Berechne 17 mal 23 und addiere 19. Prüfe das Ergebnis durch eine zweite Zerlegung.
            Der erwartete Kontrollwert ist 410. Die eindeutige Markerkennung lautet GPU1-SATURN-410.
            Dies ist eine neutrale Testaufgabe ohne Zugriff auf fremde Dateien oder Nutzerdaten.
            """, new UTF8Encoding(false));

        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMissumInfrastructure(options => options.DataDirectory = root);
        string? databasePath = null;
        try
        {
            await using var services = registrations.BuildServiceProvider(validateScopes: true);
            var database = services.GetRequiredService<IMissumDatabase>();
            databasePath = database.DatabasePath;
            await database.InitializeAsync();
            var chats = services.GetRequiredService<IChatRepository>();
            var session = await chats.CreateSessionAsync("Qwen UI Abnahme", ChatMode.ClaudeScience);
            await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
            var group = await chats.GetOrCreateSessionGroupForWorkspaceAsync(workspace, ChatMode.ClaudeScience);
            await chats.ApplySessionGroupingAsync([new(group.Id, "Qwen UI Abnahme", [session.Id])]);
            var store = services.GetRequiredService<ISettingsStore>();
            using var settings = new SettingsCoordinator(store);
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with
            {
                SelectedChatMode = ChatMode.ClaudeScience,
                ActiveSessionId = session.Id,
                ActiveClaudeScienceSessionId = session.Id,
                SelectedModel = ModelId,
                CodingWorkspacePath = workspace,
                IsAutomaticSpeechEnabled = false,
                CodingToolStepsExpanded = false,
                AssistantOutputsVisible = true,
                LastRoute = "assistant",
                ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase)
                {
                    [MissumAiAssistantService.ReasoningKey(ModelId, "coding")] = "low",
                },
                ContextLimitsByModel = new(StringComparer.OrdinalIgnoreCase) { [ModelId] = 32_768 },
            });
            var stored = await store.LoadAsync();
            Assert.Equal(session.Id, stored.ActiveClaudeScienceSessionId);
            Assert.Equal(32_768, ModelContextSelection.Resolve(stored, ModelId, 262_144));
            Assert.Equal("low", MissumAiAssistantService.StoredReasoning(stored, ModelId, "coding"));
            Assert.False(stored.IsAutomaticSpeechEnabled);
            Assert.Empty(await chats.ListMessagesAsync(session.Id));
            var reopened = Assert.IsType<ChatSession>(await chats.GetSessionAsync(session.Id));
            Assert.Equal(group.Id, reopened.SessionGroupId);
            Assert.Equal(workspace, reopened.CodingWorkspacePath);
            Assert.Equal("Qwen UI Abnahme", (await chats.ListSessionGroupsAsync(ChatMode.ClaudeScience))
                .Single(value => value.Id == group.Id).Name);

            var statePath = Path.Combine(root, "State.json");
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
            {
                DataDirectory = root,
                DatabasePath = databasePath,
                SettingsPath = store.SettingsPath,
                Workspace = workspace,
                ProjectId = group.Id,
                SessionGroupId = group.Id,
                SessionId = session.Id,
                ModelId,
                ContextTokens = 32_768,
                ReasoningEffort = "low",
                Facts = fixtures.ToDictionary(item => item.Key, item => item.Value),
                Files = fixtures.Select(item => new { item.FileName, Bytes = new FileInfo(Path.Combine(workspace, item.FileName)).Length }).ToArray(),
                SubagentFile = "subagent.txt",
                CreatedAt = DateTimeOffset.UtcNow,
                LaunchEnvironment = new Dictionary<string, string> { ["MISSUM_DATA_DIRECTORY"] = root },
            }, StateJson));
            output.WriteLine($"Fixture retained: {statePath}");
            output.WriteLine($"Launch the normal native application with MISSUM_DATA_DIRECTORY={root}; do not set a smoke instance key.");
        }
        finally
        {
            if (databasePath is not null)
            {
                TestSqlitePools.ClearDatabasePool(databasePath);
                TestSqlitePools.ClearDatabaseBackups(databasePath);
            }
        }
    }
}
