using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.Services.Webdesign;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Repositories;
using Missum.Infrastructure.Settings;
using Missum.Infrastructure.Storage;

namespace Missum.Tests;

/// <summary>
/// The former Webdesign mode is the Codex tool "Webdesign": a per-session switch beside the selected
/// tool, migrated from old data, offered to the local AI automatically for web work, and switched on by
/// the model's first webdesign.* call.
/// </summary>
[Collection(WebdesignTestGroup.Name)]
public sealed class WebdesignToolTests
{
    private const string Model = "fixture/webdesign-tool";
    private const int ContextTokens = 65_536;
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    // ----- catalog --------------------------------------------------------------------------

    [Fact]
    public void WebdesignIsASelectableToolOnlyInCodex()
    {
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        Assert.True(catalog.TryGetAction(BuiltInActionIds.Webdesign, out var action));
        Assert.Equal("builtin.webdesign/website", BuiltInActionIds.Webdesign);
        Assert.Equal("Webdesign", action!.DisplayName);
        Assert.Equal("web", action.IconKey);
        Assert.Equal(BuiltInExtensionCatalog.GroupIntegrations, action.GroupId);
        Assert.Equal(ExtensionActionKind.SelectableTool, action.ActionKind);
        Assert.Equal([ExtensionChatMode.Coding], action.SupportedChatModes);
        Assert.Contains(catalog.GetActions(ExtensionChatMode.Coding), item => item.ActionId == BuiltInActionIds.Webdesign);
        foreach (var mode in new[] { ExtensionChatMode.General, ExtensionChatMode.ClaudeScience, ExtensionChatMode.ClaudeMeetings,
                     ExtensionChatMode.Webdesign, ExtensionChatMode.ModelHub })
            Assert.DoesNotContain(catalog.GetActions(mode), item => item.ActionId == BuiltInActionIds.Webdesign);
        // Placed among the program integrations, before Computersteuerung.
        var computer = catalog.GetActions(ExtensionChatMode.Coding).Single(item => item.ActionId == BuiltInActionIds.ComputerUse);
        Assert.True(action.ItemOrder < computer.ItemOrder);
        Assert.False(catalog.TryGetTools(BuiltInActionIds.Webdesign, out _));
    }

    [Fact]
    public void ComputerObserveOffersOnlyTheDesktop()
    {
        Assert.True(ExtensionActionCatalog.CreateWithBuiltIns().TryGetTools(BuiltInActionIds.ComputerUse, out var tools));
        var observe = tools!.Single(tool => tool.ModelToolName == "computer.observe");
        var scopes = observe.InputSchema.GetProperty("properties").GetProperty("scope").GetProperty("enum")
            .EnumerateArray().Select(static value => value.GetString()!).ToArray();
        Assert.Equal(["desktop", "window"], scopes);
        Assert.DoesNotContain("webview", observe.InputSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Webseite", observe.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Webdesign", ExtensionActionCatalog.CreateWithBuiltIns().GetActions(ExtensionChatMode.Coding)
            .Single(item => item.ActionId == BuiltInActionIds.ComputerUse).Description, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(
            ComputerProposal(new { scope = "webview" })));
    }

    [Fact]
    public async Task ComputerObserveWithoutScopeUsesTheDesktopEvenWithTheWebdesignToolOn()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Portfolio", ChatMode.Coding);
        await chats.SetWebdesignEnabledAsync(session.Id, true);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = "http://127.0.0.1:65000", SelectedModel = Model });
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            static () => new CatalogHandler());
        var surface = new RecordingSurface(Path.Combine(environment.Directory, "site"));
        using var desktop = new ComputerDesktopBroker(new AllowingApproval(), new DesktopProbe(),
            webViewFactory: () => new WebViewComputerPlatform(surface));
        var broker = new LocalToolBroker(connection, environment.Get<IDocumentIngestor>(), null!, chats, computerDesktop: desktop);

        var result = await broker.ExecuteForAgentAsync(ComputerProposal(new { }), session.Id, null,
            runAction: PromptTriggerAction.Coding, computerUseEnabled: true);

        Assert.Equal("failed", result.Status);
        Assert.Contains(DesktopProbe.Marker, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, surface.Captures);
    }

    // ----- migration ------------------------------------------------------------------------

    [Fact]
    public async Task MigrationMovesWebdesignSessionsAndProjectsIntoCodexWithTheToolOn()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var shared = Directory.CreateDirectory(Path.Combine(environment.Directory, "Gemeinsames Projekt")).FullName;
        var portfolio = Directory.CreateDirectory(Path.Combine(environment.Directory, "Portfolio Ü")).FullName;
        var codex = await chats.CreateSessionAsync("Codex bleibt", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(codex.Id, shared, activateCoding: true);
        var codexGroup = Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.Coding));
        var joinsCodex = Guid.NewGuid();
        var becomesCodex = Guid.NewGuid();
        var withoutProject = Guid.NewGuid();
        var sharedWebGroup = Guid.NewGuid();
        var portfolioGroup = Guid.NewGuid();
        var updated = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = environment.DatabasePath,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Data as the Webdesign mode left it: its own projects (one for a folder Codex also uses,
            // compared case-insensitively) and sessions, one with Plan mode selected, one without project.
            command.CommandText = """
                INSERT INTO chat_session_groups(id,name,is_collapsed,created_at,workspace_path,chat_mode)
                VALUES($sharedGroup,'Gemeinsames Projekt',0,$updated,$sharedUpper,'webdesign'),
                      ($portfolioGroup,'Portfolio Ü',1,$updated,$portfolio,'webdesign');
                INSERT INTO chat_sessions(id,title,created_at,updated_at,chat_mode,draft,persistent_extension_action_id,coding_workspace_path,session_group_id)
                VALUES($joins,'Webseite im Codex-Projekt',$updated,$updated,'webdesign','Entwurf bleibt',$plan,$shared,$sharedGroup),
                      ($becomes,'Portfolio',$updated,$updated,'webdesign','',NULL,$portfolio,$portfolioGroup),
                      ($loose,'Ohne Projekt',$updated,$updated,'webdesign','',NULL,NULL,NULL);
                DELETE FROM schema_migrations WHERE version=62;
                """;
            command.Parameters.AddWithValue("$sharedGroup", sharedWebGroup.ToString("D"));
            command.Parameters.AddWithValue("$portfolioGroup", portfolioGroup.ToString("D"));
            command.Parameters.AddWithValue("$updated", updated);
            command.Parameters.AddWithValue("$sharedUpper", codexGroup.WorkspacePath!.ToUpperInvariant());
            command.Parameters.AddWithValue("$shared", codexGroup.WorkspacePath!);
            command.Parameters.AddWithValue("$portfolio", portfolio);
            command.Parameters.AddWithValue("$joins", joinsCodex.ToString("D"));
            command.Parameters.AddWithValue("$becomes", becomesCodex.ToString("D"));
            command.Parameters.AddWithValue("$loose", withoutProject.ToString("D"));
            command.Parameters.AddWithValue("$plan", BuiltInActionIds.PlanMode);
            await command.ExecuteNonQueryAsync();
        }
        var message = await chats.AddMessageAsync(joinsCodex, ChatRole.User, "Mach den Header blau.", MessageStatus.Completed);
        var before = (await chats.ListSessionsAsync()).ToDictionary(session => session.Id);
        TestSqlitePools.ClearDatabasePool(environment.DatabasePath);

        await using var reopened = new SqliteDatabase(new MissumInfrastructureOptions { DataDirectory = environment.Directory },
            NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        var restored = new SqliteChatRepository(reopened);

        Assert.Empty(await restored.ListSessionsAsync(ChatMode.Webdesign));
        Assert.Empty(await restored.ListSessionGroupsAsync(ChatMode.Webdesign));
        var sessions = (await restored.ListSessionsAsync()).ToDictionary(session => session.Id);
        var groups = (await restored.ListSessionGroupsAsync(ChatMode.Coding)).ToDictionary(group => group.Id);
        foreach (var id in new[] { joinsCodex, becomesCodex, withoutProject })
        {
            Assert.Equal(ChatMode.Coding, sessions[id].ChatMode);
            Assert.True(sessions[id].WebdesignEnabled);
            Assert.Equal(before[id].UpdatedAt, sessions[id].UpdatedAt);
            Assert.Equal(before[id].Title, sessions[id].Title);
        }
        // Joined the Codex project of the same folder; the emptied Webdesign project is gone.
        Assert.Equal(codexGroup.Id, sessions[joinsCodex].SessionGroupId);
        Assert.DoesNotContain(sharedWebGroup, groups.Keys);
        Assert.Equal(BuiltInActionIds.PlanMode, sessions[joinsCodex].PersistentExtensionActionId);
        Assert.Equal("Entwurf bleibt", sessions[joinsCodex].Draft);
        Assert.Equal(message.Content, (await restored.GetMessageAsync(message.Id))!.Content);
        // Without a Codex project for its folder, the Webdesign project itself became one.
        Assert.Equal(portfolioGroup, sessions[becomesCodex].SessionGroupId);
        Assert.Equal(ChatMode.Coding, groups[portfolioGroup].ChatMode);
        Assert.True(groups[portfolioGroup].IsCollapsed);
        Assert.Equal("Portfolio Ü", groups[portfolioGroup].Name);
        Assert.Null(sessions[withoutProject].SessionGroupId);
        Assert.False(sessions[codex.Id].WebdesignEnabled);
        Assert.Equal(codexGroup.Id, sessions[codex.Id].SessionGroupId);
        Assert.Equal(2, groups.Count);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(environment.Directory, "DatabaseBackups"), "*.pre-v62-*.bak"));
        Assert.True(await reopened.CheckIntegrityAsync());
        // The migration runs once; a later start changes nothing.
        await reopened.InitializeAsync();
        Assert.Equal(3, (await restored.ListSessionsAsync()).Count(session => session.WebdesignEnabled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsOfTheWebdesignModeOpenTheSameChatInCodex(bool codexAlreadyHadAChat)
    {
        var directory = Directory.CreateTempSubdirectory("missum-webdesign-settings-").FullName;
        try
        {
            var webdesignSession = Guid.NewGuid();
            var codexSession = Guid.NewGuid();
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["selectedChatMode"] = "webdesign",
                ["activeWebdesignSessionId"] = webdesignSession,
                ["activeCodingSessionId"] = codexAlreadyHadAChat ? codexSession : null,
                ["activeSessionId"] = webdesignSession,
            }));
            using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = directory });

            var settings = await store.LoadAsync();

            Assert.Equal(ChatMode.Coding, settings.SelectedChatMode);
            Assert.Equal(codexAlreadyHadAChat ? codexSession : webdesignSession, settings.ActiveCodingSessionId);
            Assert.Null(settings.ActiveWebdesignSessionId);
            Assert.Equal(webdesignSession, settings.ActiveSessionId);
            // Written back at once, so older readers never see the legacy mode again.
            var written = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
            Assert.Equal("coding", written.GetProperty("selectedChatMode").GetString());
            Assert.Equal(JsonValueKind.Null, written.GetProperty("activeWebdesignSessionId").ValueKind);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ClientViewsOfTheWebdesignModeOpenCodex()
    {
        var directory = Directory.CreateTempSubdirectory("missum-webdesign-views-").FullName;
        try
        {
            var webdesignSession = Guid.NewGuid();
            var codexSession = Guid.NewGuid();
            var path = Path.Combine(directory, "ClientState", "views.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                // The view cache stores the mode as its number; 4 was Webdesign.
                ["browser-a"] = new { mode = 4, webdesignSession, activeSession = webdesignSession },
                ["browser-b"] = new { mode = 4, webdesignSession, codingSession = codexSession },
            }));
            using var store = new AssistantClientStateStore(directory);
            var defaults = new AppSettings();

            var first = store.Resolve("browser-a", defaults);
            var second = store.Resolve("browser-b", defaults);

            Assert.Equal(ChatMode.Coding, first.SelectedChatMode);
            Assert.Equal(webdesignSession, first.ActiveCodingSessionId);
            Assert.Null(first.ActiveWebdesignSessionId);
            Assert.Equal(webdesignSession, first.ActiveSessionId);
            Assert.Equal(ChatMode.Coding, second.SelectedChatMode);
            Assert.Equal(codexSession, second.ActiveCodingSessionId);
        }
        finally { Directory.Delete(directory, true); }
    }

    // ----- selection ------------------------------------------------------------------------

    [Fact]
    public async Task TheSwitchPersistsPerSessionBesidePlanModeAndOnlyInCodex()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Portfolio", ChatMode.Coding);
        var other = await chats.CreateSessionAsync("Anderes Projekt", ChatMode.Coding);
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "Portfolio")).FullName;
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: true);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            ActiveSessionId = session.Id, ActiveCodingSessionId = session.Id, SelectedChatMode = ChatMode.Coding,
        });
        var coordinator = CreateCoordinator(environment, settings);
        var events = new List<(string Type, JsonElement Payload)>();
        Task Record(string type, object payload, string? _) { events.Add((type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web))); return Task.CompletedTask; }

        var offered = (await SnapshotAsync(coordinator)).GetProperty("actionDescriptors").EnumerateArray()
            .Select(static action => action.GetProperty("actionId").GetString()).ToArray();
        Assert.Contains(BuiltInActionIds.Webdesign, offered);
        Assert.False((await SnapshotAsync(coordinator)).GetProperty("webdesignEnabled").GetBoolean());

        await coordinator.HandleAsync(Envelope("action.invoke", new { sessionId = session.Id, actionId = BuiltInActionIds.Webdesign, enabled = true }), Record);
        Assert.Contains(events, item => item.Type == "action.completed" && item.Payload.GetProperty("toolToggle").GetString() == "webdesign");
        Assert.True(events.Last(item => item.Type == "session.changed").Payload.GetProperty("webdesignEnabled").GetBoolean());
        Assert.True((await chats.GetSessionAsync(session.Id))!.WebdesignEnabled);
        Assert.False((await chats.GetSessionAsync(other.Id))!.WebdesignEnabled);

        // Plan mode becomes the selected tool; Webdesign stays switched on next to it.
        await coordinator.HandleAsync(Envelope("action.invoke", new { sessionId = session.Id, actionId = BuiltInActionIds.PlanMode, enabled = true }), Record);
        var both = await SnapshotAsync(coordinator);
        Assert.Equal(BuiltInActionIds.PlanMode, both.GetProperty("selectedExtensionActionId").GetString());
        Assert.True(both.GetProperty("webdesignEnabled").GetBoolean());
        // Browser-Use as a one-time tool leaves the switch untouched as well.
        await coordinator.HandleAsync(Envelope("action.invoke", new { sessionId = session.Id, actionId = BuiltInActionIds.BrowserUse, enabled = true }), Record);
        Assert.True((await chats.GetSessionAsync(session.Id))!.WebdesignEnabled);

        // Survives a restart: a fresh repository on the same database reads it back.
        TestSqlitePools.ClearDatabasePool(environment.DatabasePath);
        await using (var reopened = new SqliteDatabase(new MissumInfrastructureOptions { DataDirectory = environment.Directory },
            NullLogger<SqliteDatabase>.Instance))
        {
            var stored = await new SqliteChatRepository(reopened).GetSessionAsync(session.Id);
            Assert.True(stored!.WebdesignEnabled);
            Assert.Equal(BuiltInActionIds.PlanMode, stored.PersistentExtensionActionId);
        }
        TestSqlitePools.ClearDatabasePool(environment.DatabasePath);

        // Removing the chip switches it off; Plan mode stays.
        await coordinator.HandleAsync(Envelope("action.invoke", new { sessionId = session.Id, actionId = BuiltInActionIds.Webdesign, enabled = false }), Record);
        var off = await chats.GetSessionAsync(session.Id);
        Assert.False(off!.WebdesignEnabled);
        Assert.Equal(BuiltInActionIds.PlanMode, off.PersistentExtensionActionId);

        // ChatGPT never offers or accepts it.
        var chatGpt = await chats.CreateSessionAsync("ChatGPT", ChatMode.General);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(Envelope("action.invoke",
            new { sessionId = chatGpt.Id, actionId = BuiltInActionIds.Webdesign, enabled = true }), Record));
        Assert.False((await chats.GetSessionAsync(chatGpt.Id))!.WebdesignEnabled);
    }

    // ----- runs -----------------------------------------------------------------------------

    [Fact]
    public async Task AnActiveToolGivesTheRunTheWebdesignToolsAndWorkflow()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment);
        var session = await fixture.CreateProjectSessionAsync("Portfolio");
        await environment.Get<IChatRepository>().SetWebdesignEnabledAsync(session.Id, true);
        const string prompt = "Korrigiere den Fehler.";

        var request = await fixture.BuildAsync(session.Id, prompt, AssistantCoordinator.CreateToolMatch("coding", prompt));

        Assert.Contains("webdesign", request.ClientCapabilities!);
        Assert.Contains("workspace", request.ClientCapabilities!);
        var text = UserText(request);
        Assert.Contains("Du arbeitest in Codex mit dem Werkzeug Webdesign", text, StringComparison.Ordinal);
        Assert.Contains("media.analyze", text, StringComparison.Ordinal);
        Assert.EndsWith("Auftrag: " + prompt, text, StringComparison.Ordinal);
        Assert.DoesNotContain(MissumAiAssistantService.WebdesignToolHint, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanModeKeepsTheWebdesignWorkflowReadOnly()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment);
        var session = await fixture.CreateProjectSessionAsync("Portfolio");
        await environment.Get<IChatRepository>().SetWebdesignEnabledAsync(session.Id, true);
        const string prompt = "Wie bauen wir die Startseite um?";

        var request = await fixture.BuildAsync(session.Id, prompt, AssistantCoordinator.CreateExtensionMatch(BuiltInActionIds.PlanMode, prompt));

        Assert.Contains("webdesign", request.ClientCapabilities!);
        var text = UserText(request);
        Assert.Contains("Du arbeitest in Codex mit dem Werkzeug Webdesign", text, StringComparison.Ordinal);
        Assert.Contains("Im Planmodus prüfst du nur und änderst keine Dateien.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("public")]
    [InlineData("vite")]
    [InlineData("prompt")]
    [InlineData("history")]
    public async Task WebProjectsAndWebPromptsGetTheToolsWithAShortHint(string signal)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment);
        var session = await fixture.CreateProjectSessionAsync("Projekt");
        var workspace = session.CodingWorkspacePath!;
        var prompt = signal == "prompt" ? "Baue mir eine Landingpage für das Café." : "Korrigiere den Fehler.";
        switch (signal)
        {
            case "index": await File.WriteAllTextAsync(Path.Combine(workspace, "index.html"), "<!doctype html><title>x</title>"); break;
            case "public":
                Directory.CreateDirectory(Path.Combine(workspace, "public"));
                await File.WriteAllTextAsync(Path.Combine(workspace, "public", "index.html"), "<!doctype html>");
                break;
            case "vite": await File.WriteAllTextAsync(Path.Combine(workspace, "package.json"), """{"name":"x","devDependencies":{"vite":"^6.0.0"}}"""); break;
            case "history":
                await environment.Get<IChatRepository>().AddMessageAsync(session.Id, ChatRole.User, "Die Homepage soll dunkler werden.", MessageStatus.Completed);
                break;
        }

        var request = await fixture.BuildAsync(session.Id, prompt, AssistantCoordinator.CreateToolMatch("coding", prompt));

        Assert.Contains("webdesign", request.ClientCapabilities!);
        var text = UserText(request);
        Assert.StartsWith(MissumAiAssistantService.WebdesignToolHint, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Du arbeitest in Codex mit dem Werkzeug Webdesign", text, StringComparison.Ordinal);
        // Offering never switches the tool on or starts a server; only a real call does.
        Assert.False((await environment.Get<IChatRepository>().GetSessionAsync(session.Id))!.WebdesignEnabled);
        Assert.False(Directory.Exists(Path.Combine(workspace, ".missum")));
    }

    [Fact]
    public async Task APlainProjectGetsNeitherTheToolsNorTheHint()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment);
        var session = await fixture.CreateProjectSessionAsync("Parser");
        await File.WriteAllTextAsync(Path.Combine(session.CodingWorkspacePath!, "package.json"), """{"name":"cli","dependencies":{"express":"^5.0.0"}}""");
        await File.WriteAllTextAsync(Path.Combine(session.CodingWorkspacePath!, "parser.py"), "print('x')");
        const string prompt = "Korrigiere den Fehler im Parser.";

        var request = await fixture.BuildAsync(session.Id, prompt, AssistantCoordinator.CreateToolMatch("coding", prompt));

        Assert.DoesNotContain("webdesign", request.ClientCapabilities!);
        Assert.DoesNotContain("Webdesign", UserText(request), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Gestalte die Webseite neu", true)]
    [InlineData("Build a website for my band", true)]
    [InlineData("Die Homepage lädt langsam", true)]
    [InlineData("Neue Landing-Page", true)]
    [InlineData("Landing Page mit Formular", true)]
    [InlineData("Passe das CSS an", true)]
    [InlineData("Validiere das HTML", true)]
    [InlineData("Das Frontend zeigt nichts an", true)]
    [InlineData("Die UI der Seite ist zu eng", true)]
    [InlineData("Unsere Firmenwebseite braucht ein Kontaktformular", true)]
    [InlineData("Korrigiere den Fehler im Parser", false)]
    [InlineData("Schreibe ein Python-Skript für die Auswertung", false)]
    [InlineData("Access control for classes", false)]
    [InlineData("", false)]
    public void WebPromptsAreRecognised(string prompt, bool expected) =>
        Assert.Equal(expected, WebdesignProjectDetector.IsWebPrompt(prompt));

    [Fact]
    public void WebProjectsAreRecognisedByPagesAndFrameworks()
    {
        var root = Directory.CreateTempSubdirectory("missum-webdesign-detect-").FullName;
        try
        {
            string Project(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            Assert.False(WebdesignProjectDetector.IsWebProject(Project("empty")));
            Assert.False(WebdesignProjectDetector.IsWebProject(Path.Combine(root, "missing")));
            Assert.False(WebdesignProjectDetector.IsWebProject(null));
            foreach (var folder in new[] { "", "public", "src", "dist" })
            {
                var project = Project("page-" + (folder.Length == 0 ? "root" : folder));
                Directory.CreateDirectory(Path.Combine(project, folder));
                File.WriteAllText(Path.Combine(project, folder, "index.html"), "<!doctype html>");
                Assert.True(WebdesignProjectDetector.IsWebProject(project), folder);
            }
            foreach (var package in new[] { "vite", "next", "react", "vue", "svelte", "astro", "@angular/core", "nuxt", "gatsby", "remix",
                         "@remix-run/react", "parcel", "webpack-dev-server" })
            {
                var project = Project("package-" + package.Replace('/', '-').Replace('@', '-'));
                File.WriteAllText(Path.Combine(project, "package.json"), JsonSerializer.Serialize(new { dependencies = new Dictionary<string, string> { [package] = "1.0.0" } }));
                Assert.True(WebdesignProjectDetector.IsWebProject(project), package);
            }
            var server = Project("server");
            File.WriteAllText(Path.Combine(server, "package.json"), """{"dependencies":{"express":"^5.0.0"},"devDependencies":{"typescript":"^5"}}""");
            Assert.False(WebdesignProjectDetector.IsWebProject(server));
            var broken = Project("broken");
            File.WriteAllText(Path.Combine(broken, "package.json"), "{ not json");
            Assert.False(WebdesignProjectDetector.IsWebProject(broken));
            var array = Project("array");
            File.WriteAllText(Path.Combine(array, "package.json"), "[]");
            Assert.False(WebdesignProjectDetector.IsWebProject(array));
        }
        finally { Directory.Delete(root, true); }
    }

    // ----- automatic activation -------------------------------------------------------------

    [Fact]
    public async Task TheModelsFirstWebdesignCallSwitchesTheToolOnForTheSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Projekt", ChatMode.Coding);
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "Projekt")).FullName;
        await using var serve = new WebdesignServeService(NullLogger<WebdesignServeService>.Instance);
        var broker = new LocalToolBroker(null!, null!, null!, chats);
        var raised = new List<Guid>();
        void OnActivated(Guid id) { lock (raised) raised.Add(id); }
        WebdesignToolActivation.Activated += OnActivated;
        try
        {
            var result = await broker.ExecuteAsync(WebdesignProposal(WorkspaceTools.WebdesignPreview, new { operation = "annotations" }),
                session.Id, null, workspace);

            Assert.Equal("completed", result.Status);
            Assert.True((await chats.GetSessionAsync(session.Id))!.WebdesignEnabled);
            lock (raised) Assert.Equal([session.Id], raised.Where(id => id == session.Id));
            // Already on: a second call neither writes nor notifies again.
            await broker.ExecuteAsync(WebdesignProposal(WorkspaceTools.WebdesignPreview, new { operation = "annotations" }), session.Id, null, workspace);
            lock (raised) Assert.Single(raised, id => id == session.Id);
            // An invalid call does not switch anything on.
            var other = await chats.CreateSessionAsync("Anderes", ChatMode.Coding);
            var invalid = await broker.ExecuteAsync(WebdesignProposal(WorkspaceTools.WebdesignServe, new { operation = "delete" }), other.Id, null, workspace);
            Assert.Equal("failed", invalid.Status);
            Assert.False((await chats.GetSessionAsync(other.Id))!.WebdesignEnabled);
        }
        finally { WebdesignToolActivation.Activated -= OnActivated; }
    }

    [Fact]
    public async Task APageCallWaitsUntilTheNewTabRegisteredItsSurface()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Projekt", ChatMode.Coding);
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "Seite")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "index.html"), "<!doctype html><title>Seite</title>");
        await using var serve = new WebdesignServeService(NullLogger<WebdesignServeService>.Instance);
        var broker = new LocalToolBroker(null!, null!, null!, chats);
        var surface = new RecordingSurface(workspace) { ConsoleLines = ["[HTTP 404] http://127.0.0.1/fehlt.js"] };
        // Like the page: the activation refreshes the session, the tab appears a moment later.
        void OnActivated(Guid id)
        {
            if (id == session.Id) _ = Task.Delay(300).ContinueWith(_ => WebdesignPreviewRegistry.Register(surface), TaskScheduler.Default);
        }
        WebdesignToolActivation.Activated += OnActivated;
        try
        {
            Assert.Null(WebdesignPreviewRegistry.TryGet(workspace));
            var result = await broker.ExecuteAsync(WebdesignProposal(WorkspaceTools.WebdesignPreview, new { operation = "console" }),
                session.Id, null, workspace);

            Assert.Equal("completed", result.Status);
            Assert.Equal(1, result.Result.GetProperty("count").GetInt32());
            Assert.Same(surface, WebdesignPreviewRegistry.TryGet(workspace));
            Assert.True((await chats.GetSessionAsync(session.Id))!.WebdesignEnabled);
        }
        finally
        {
            WebdesignToolActivation.Activated -= OnActivated;
            WebdesignPreviewRegistry.Unregister(surface);
            await serve.StopAsync(workspace);
        }
    }

    // ----- helpers --------------------------------------------------------------------------

    private static string UserText(RunRequest request) =>
        request.Messages[^1].Content.First(static part => part.Type == "text").Text!;

    private static ToolProposal ComputerProposal(object arguments) => new(
        "observe-" + Guid.NewGuid().ToString("N"), "run-webdesign", "computer.observe", JsonSerializer.SerializeToElement(arguments),
        ToolRiskClass.ReadOnly, "Desktop beobachten", DateTimeOffset.UtcNow.AddMinutes(5));

    private static ToolProposal WebdesignProposal(string name, object arguments) => new(
        "webdesign-" + Guid.NewGuid().ToString("N"), "run-webdesign", name, JsonSerializer.SerializeToElement(arguments),
        name == WorkspaceTools.WebdesignServe ? ToolRiskClass.Process : ToolRiskClass.ReadOnly, "Webseite prüfen",
        DateTimeOffset.UtcNow.AddMinutes(5));

    private static WebBridgeEnvelope Envelope(string type, object payload) =>
        new(AssistantProtocol.ProtocolVersion, type, Guid.NewGuid().ToString("D"), JsonSerializer.SerializeToElement(payload));

    private static async Task<JsonElement> SnapshotAsync(AssistantCoordinator coordinator) =>
        JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);

    private static AssistantCoordinator CreateCoordinator(TestEnvironment environment, SettingsCoordinator settings)
    {
        var recentActivity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        recentActivity.Restore();
        return new AssistantCoordinator(
            environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(),
            environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(),
            null,
            settings,
            recentActivity);
    }

    /// <summary>The real request builder against a catalog-only gateway fixture; no inference runs.</summary>
    private sealed class RequestFixture : IDisposable
    {
        private readonly TestEnvironment _environment;
        private readonly SettingsCoordinator _settings;
        private readonly MissumAiConnectionService _connection;
        private readonly MissumAiAssistantService _service;

        private RequestFixture(TestEnvironment environment, SettingsCoordinator settings, MissumAiConnectionService connection,
            MissumAiAssistantService service)
        {
            _environment = environment;
            _settings = settings;
            _connection = connection;
            _service = service;
        }

        public static async Task<RequestFixture> CreateAsync(TestEnvironment environment)
        {
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with { MissumAiServerUrl = "http://127.0.0.1:65000", SelectedModel = Model });
            var chats = environment.Get<IChatRepository>();
            var documents = environment.Get<IDocumentIngestor>();
            var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
                static () => new CatalogHandler());
            var broker = new LocalToolBroker(connection, documents, null!, chats);
            var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
            var service = new MissumAiAssistantService(connection, chats,
                environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
                environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(),
                environment.Get<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
                new SessionContextPreparationService(chats), broker, null!, null!, settings, recent,
                NullLogger<MissumAiAssistantService>.Instance, extensionActions: ExtensionActionCatalog.CreateWithBuiltIns());
            return new RequestFixture(environment, settings, connection, service);
        }

        public async Task<ChatSession> CreateProjectSessionAsync(string title)
        {
            var chats = _environment.Get<IChatRepository>();
            var session = await chats.CreateSessionAsync(title, ChatMode.Coding);
            var workspace = Directory.CreateDirectory(Path.Combine(_environment.Directory, "workspace-" + session.Id.ToString("N"))).FullName;
            await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: true);
            return (await chats.GetSessionAsync(session.Id))!;
        }

        public async Task<RunRequest> BuildAsync(Guid sessionId, string prompt, PromptTriggerMatch? trigger)
        {
            var chats = _environment.Get<IChatRepository>();
            var history = await chats.ListMessagesAsync(sessionId);
            var assistant = await chats.AddMessageAsync(sessionId, ChatRole.Assistant, "", MessageStatus.Streaming);
            using var client = await _connection.CreateClientAsync();
            var builder = typeof(MissumAiAssistantService).GetMethod("BuildRunRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var uploadedType = builder.GetParameters()[6].ParameterType.GenericTypeArguments.Single();
            return await (Task<RunRequest>)builder.Invoke(_service,
            [
                client, sessionId, prompt, trigger, Array.Empty<AssistantAttachment>(), history,
                Array.CreateInstance(uploadedType, 0), assistant,
                (Func<MissumAiAssistantUpdate, Task>)(static _ => Task.CompletedTask), CancellationToken.None,
                null, // memoryNote: reflection does not fill optional parameters
            ])!;
        }

        public void Dispose()
        {
            _service.Dispose();
            _connection.Dispose();
            _settings.Dispose();
        }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            ModelRuntimeStatus[] models =
            [
                new(Model, "general", true, true, "loaded", ContextTokens),
                new(Model, "coding", true, true, "loaded", ContextTokens),
            ];
            object value = request.RequestUri!.AbsolutePath switch
            {
                "/v1/models/status" => new ModelStatusSnapshot(true, "fixture", models, now),
                "/v1/models/coding" => new CodingModelCatalogResponse(models.Where(static model => model.Role == "coding").ToArray(),
                    "fixture", true, null, now),
                "/v1/capabilities" => new CapabilitySnapshot(MissumAiProtocol.Version, "fixture", [],
                    ["coding.updatePlan", "subagent.spawn"], [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence],
                    new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize, SupportsCodingSessionContext: true),
                _ => throw new InvalidOperationException("No inference is allowed in this fixture: " + request.RequestUri),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A website tab that records captures; computer control must never reach it.</summary>
    private sealed class RecordingSurface(string workspace) : IWebdesignPreviewSurface
    {
        public int Captures { get; private set; }
        public IReadOnlyList<string> ConsoleLines { get; init; } = [];
        public string? Workspace { get; } = WebdesignServeService.Normalize(workspace);
        public string? CurrentUrl => "http://127.0.0.1:5555/";
        public string Device => "free";
        public Task ShowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NavigateAsync(string url, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<WebdesignCapture> CaptureAsync(string? device, CancellationToken cancellationToken)
        {
            Captures++;
            throw new InvalidOperationException("Der Tab Webseite darf hier nicht aufgenommen werden.");
        }
        public Task<IReadOnlyList<string>> ConsoleAsync(CancellationToken cancellationToken) => Task.FromResult(ConsoleLines);
        public Task<JsonElement> InspectAsync(string selector, CancellationToken cancellationToken) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> ElementAtAsync(double cssX, double cssY, CancellationToken cancellationToken) => Task.FromResult(default(JsonElement));
        public Task<(double X, double Y)> ScrollAsync(CancellationToken cancellationToken) => Task.FromResult((0d, 0d));
        public Task DispatchAsync(WebdesignInput input, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AllowingApproval : IComputerActionApproval
    {
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ComputerActionDecision.Cancel);
    }

    /// <summary>Stands for the Windows desktop: reaching it proves the scope that was used.</summary>
    private sealed class DesktopProbe : IComputerDesktopPlatform
    {
        public const string Marker = "Desktop wurde beobachtet.";
        public Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken) => throw new InvalidOperationException(Marker);
        public Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}

/// <summary>Webdesign tests share process-wide state (serve instance, preview registry, annotations).</summary>
internal static class WebdesignTestGroup
{
    internal const string Name = "Webdesign shared state";
}
