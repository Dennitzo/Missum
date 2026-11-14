using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Prepares an isolated normal-app profile; it does not launch the app, a model, or inject input.</summary>
public sealed class ComputerPortableAcceptanceFixtureTests(ITestOutputHelper output)
{
    private const string Marker = "Missum computer portable acceptance fixture v1";
    private const string DefaultModel = "coding/Qwen3VL-30B-A3B-Instruct-Q4_K_M~db0da1303aa5";
    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    [Fact]
    [Trait("Category", "ManualFixture")]
    public async Task PrepareIsolatedComputerCodingSession()
    {
        var requested = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_PORTABLE_FIXTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(requested)) return;
        Assert.True(Path.IsPathFullyQualified(requested), "The fixture directory must be absolute.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        var production = Path.TrimEndingDirectorySeparator(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Missum"));
        Assert.False(string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Equals(root, production, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The normal user profile must never be used for the fixture.");
        if (Directory.Exists(root))
            Assert.False(File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint));
        Directory.CreateDirectory(root);
        var ownershipPath = Path.Combine(root, ".computer-portable-fixture");
        if (File.Exists(ownershipPath))
        {
            Assert.Equal(Marker, await File.ReadAllTextAsync(ownershipPath));
            Assert.False(File.Exists(Path.Combine(root, "Missum.db")),
                "A fixture must use a fresh directory rather than replacing an existing session.");
        }
        else
        {
            Assert.False(File.Exists(Path.Combine(root, "Missum.db"))
                || File.Exists(Path.Combine(root, "GO.db")) || File.Exists(Path.Combine(root, "settings.json")),
                "Refusing to use an existing application profile.");
            await File.WriteAllTextAsync(ownershipPath, Marker);
        }

        var gatewayText = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_PORTABLE_GATEWAY") ?? "http://127.0.0.1:18080";
        Assert.True(Uri.TryCreate(gatewayText, UriKind.Absolute, out var gateway) && gateway.IsLoopback
            && gateway.Scheme == Uri.UriSchemeHttp, "The fixture requires a loopback HTTP gateway.");
        var model = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_PORTABLE_MODEL") ?? DefaultModel;
        Assert.False(string.IsNullOrWhiteSpace(model));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.txt"),
            "Isoliertes Missum-Testprojekt. Die Computersteuerung darf nur das eigens gestartete Testfenster bedienen.\n");
        var windowTitle = "Missum Portable Computer Test " + Guid.NewGuid().ToString("N");
        var prompt = $"Benutze die Computersteuerung für das sichtbare Fenster '{windowTitle}'. "
            + "Klicke in das leere Textfeld, schreibe exakt 'Missum Test' hinein und kontrolliere das Ergebnis sichtbar. "
            + "Bediene ausschließlich dieses Testfenster.";

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
            var session = await chats.CreateSessionAsync("Computersteuerung Portable-Abnahme", ChatMode.Coding);
            await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
            var group = await chats.GetOrCreateSessionGroupForWorkspaceAsync(workspace, ChatMode.Coding);
            await chats.ApplySessionGroupingAsync([new(group.Id, "Computersteuerung Portable-Abnahme", [session.Id])]);
            var store = services.GetRequiredService<ISettingsStore>();
            using var settings = new SettingsCoordinator(store);
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with
            {
                MissumAiServerUrl = gateway!.AbsoluteUri.TrimEnd('/'),
                SelectedChatMode = ChatMode.Coding,
                ActiveSessionId = session.Id,
                ActiveCodingSessionId = session.Id,
                SelectedModel = model,
                SelectedCodingModel = model,
                CodingWorkspacePath = workspace,
                IsAutomaticSpeechEnabled = false,
                AssistantOutputsVisible = true,
                LastRoute = "assistant",
                ContextLimitsByModel = new(StringComparer.OrdinalIgnoreCase) { [model] = 32_768 },
                ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase)
                {
                    [MissumAiAssistantService.ReasoningKey(model, "coding")] = "none",
                },
            });
            var stored = await store.LoadAsync();
            Assert.Equal(session.Id, stored.ActiveCodingSessionId);
            Assert.Equal(model, stored.SelectedModel);
            Assert.Equal(gateway!.AbsoluteUri.TrimEnd('/'), stored.MissumAiServerUrl);
            Assert.Empty(await chats.ListMessagesAsync(session.Id));
            var reopened = Assert.IsType<ChatSession>(await chats.GetSessionAsync(session.Id));
            Assert.Equal(group.Id, reopened.SessionGroupId);
            Assert.Equal(workspace, reopened.CodingWorkspacePath);

            var statePath = Path.Combine(root, "State.json");
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
            {
                DataDirectory = root, DatabasePath = databasePath, SettingsPath = store.SettingsPath,
                Workspace = workspace, SessionId = session.Id, GroupId = group.Id, ModelId = model,
                Gateway = gateway.AbsoluteUri.TrimEnd('/'), WindowTitle = windowTitle, Prompt = prompt,
                ExpectedText = "Missum Test", ContextTokens = 32_768,
            }, StateJson));
            output.WriteLine($"Prepared isolated portable fixture: {statePath}");
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
