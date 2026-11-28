using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

/// <summary>
/// ChatGPT (General) is limited to text answers, research and memory: no file changes, no project
/// tools and no computer control, while Codex keeps every tool.
/// </summary>
public sealed class ChatGptModeTests
{
    private const string Model = "fixture/chatgpt-mode";
    private const int ContextTokens = 65_536;
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    private static readonly string[] ChatGptActions =
    [
        BuiltInActionIds.AttachFilesAndFolders, BuiltInActionIds.ExportChatPdf, BuiltInActionIds.WebSearch,
        BuiltInActionIds.DeepResearch, BuiltInActionIds.BrowserUse, BuiltInActionIds.ImageAnalysis,
        BuiltInActionIds.VideoAnalysis, BuiltInActionIds.AudioAnalysis, BuiltInActionIds.Translate,
        BuiltInActionIds.ReadAloud, BuiltInActionIds.LiveCaptions, BuiltInActionIds.CreateAudiobook,
    ];

    private static readonly string[] CodexOnlyActions =
    [
        BuiltInActionIds.CreateDocument, BuiltInActionIds.GenerateImage,
        BuiltInActionIds.ComputerUse, BuiltInActionIds.SelfImprovement, BuiltInActionIds.Lean, BuiltInActionIds.PlanMode,
    ];

    private static readonly string[] WriteOrWorkspaceCapabilities =
        ["documentIo", "visual-tools", "coding", "coding.evidence", "coding.process", "workspace", "workspace.open", "math.lean", "computer.use"];

    [Fact]
    public void ChatGptMenuOffersTextResearchAndAnalysisButNoCreatingOrControllingTools()
    {
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        var chatGpt = catalog.GetActions(ExtensionChatMode.General).Select(static action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        foreach (var actionId in ChatGptActions) Assert.Contains(actionId, chatGpt);
        foreach (var actionId in CodexOnlyActions) Assert.DoesNotContain(actionId, chatGpt);

        // Codex loses nothing.
        var codex = catalog.GetActions(ExtensionChatMode.Coding).Select(static action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        foreach (var actionId in ChatGptActions.Concat(CodexOnlyActions)) Assert.Contains(actionId, codex);
        Assert.DoesNotContain(ExtensionChatMode.General, BuiltInExtensionCatalog.ModesWithoutChatGpt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatGptRunInsideAProjectSendsOnlyReadOnlyToolsAndNoWorkspace(bool memoryEnabled)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment, memoryEnabled);
        var session = await fixture.CreateProjectSessionAsync("Projektchat", ChatMode.General);

        var request = await fixture.BuildAsync(session.Id, "Fasse den Stand des Projekts zusammen.", null);

        Assert.Null(request.WorkspacePath);
        Assert.Null(request.CodingOptions);
        Assert.Contains("documents", request.ClientCapabilities!);
        Assert.Contains("subagents", request.ClientCapabilities!);
        foreach (var capability in WriteOrWorkspaceCapabilities) Assert.DoesNotContain(capability, request.ClientCapabilities!);
        Assert.Equal(MissumAiAssistantService.ChatGptServerTools, request.AllowedServerTools);
        Assert.DoesNotContain("image.generate", request.AllowedServerTools!);
        Assert.DoesNotContain("speech.synthesize", request.AllowedServerTools!);
        if (memoryEnabled)
        {
            var memory = Assert.Single(request.ClientTools!);
            Assert.Equal(SessionMemoryToolContract.Name, memory.Name);
            Assert.Equal(ToolRiskClass.ReadOnly, memory.RiskClass);
        }
        else Assert.Null(request.ClientTools);
        // The project grouping itself is untouched.
        Assert.NotNull((await environment.Get<IChatRepository>().GetSessionAsync(session.Id))!.CodingWorkspacePath);
    }

    [Fact]
    public async Task ChatGptKeepsBrowserUseNextToTheMemorySearchButRefusesComputerControl()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment, memoryEnabled: true);
        var session = await fixture.CreateProjectSessionAsync("Recherche", ChatMode.General);

        var browser = await fixture.BuildAsync(session.Id, "Lies die Preisliste.",
            AssistantCoordinator.CreateExtensionMatch(BuiltInActionIds.BrowserUse, "Lies die Preisliste."));
        Assert.Equal([BuiltInExtensionCatalog.BrowserUseToolName, SessionMemoryToolContract.Name],
            browser.ClientTools!.Select(static tool => tool.Name));
        Assert.Null(browser.WorkspacePath);

        var computer = AssistantCoordinator.CreateExtensionMatch(BuiltInActionIds.ComputerUse, "Öffne den Editor.");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.BuildAsync(session.Id, "Öffne den Editor.", computer));
        Assert.Contains("Codex", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatGptAudiobookInsideAProjectWritesTextOnlyAndNeverTouchesTheProjectFolder()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment, memoryEnabled: true);
        var session = await fixture.CreateProjectSessionAsync("Hörbuch im Projekt", ChatMode.General);
        const string prompt = "Erzähle ein Kapitel über einen Leuchtturmwärter.";

        var request = await fixture.BuildAsync(session.Id, prompt, AssistantCoordinator.CreateToolMatch("audiobook", prompt));

        Assert.Equal(ConversationProfile.Audiobook, request.ConversationProfile);
        Assert.Equal(RunMode.General, request.Mode);
        Assert.Empty(request.AllowedServerTools!);
        Assert.Null(request.WorkspacePath);
        Assert.Null(request.CodingOptions);
        foreach (var capability in WriteOrWorkspaceCapabilities) Assert.DoesNotContain(capability, request.ClientCapabilities!);
        Assert.All(request.ClientTools ?? [], static tool => Assert.Equal(ToolRiskClass.ReadOnly, tool.RiskClass));
    }

    [Fact]
    public async Task CodexInTheSameSetupKeepsItsWriteAndWorkspaceTools()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var fixture = await RequestFixture.CreateAsync(environment, memoryEnabled: true);
        var session = await fixture.CreateProjectSessionAsync("Codex-Projekt", ChatMode.Coding);
        const string prompt = "Korrigiere den Fehler.";

        var request = await fixture.BuildAsync(session.Id, prompt, AssistantCoordinator.CreateToolMatch("coding", prompt));

        Assert.Equal(RunMode.Coding, request.Mode);
        Assert.NotNull(request.CodingOptions?.WorkspacePath);
        foreach (var capability in new[] { "documentIo", "visual-tools", "coding", "workspace" })
            Assert.Contains(capability, request.ClientCapabilities!);
        Assert.Contains("image.generate", request.AllowedServerTools!);
        Assert.DoesNotContain(request.ClientTools ?? [], static tool => tool.Name == SessionMemoryToolContract.Name);
    }

    [Theory]
    [InlineData("coding.write", ToolRiskClass.LocalMutation)]
    [InlineData("coding.edit", ToolRiskClass.LocalMutation)]
    [InlineData("coding.command", ToolRiskClass.Process)]
    [InlineData(ClientToolNames.DocumentCreate, ToolRiskClass.LocalMutation)]
    [InlineData("computer.act", ToolRiskClass.LocalMutation)]
    [InlineData("computer.observe", ToolRiskClass.ReadOnly)]
    [InlineData("image.input", ToolRiskClass.ReadOnly)]
    [InlineData("coding.read", ToolRiskClass.ReadOnly)]
    [InlineData("selfimprovement.build", ToolRiskClass.Process)]
    [InlineData("com.example.tool.write", ToolRiskClass.LocalMutation)]
    public void ChatGptRejectsFileProcessAndDesktopClientTools(string name, ToolRiskClass risk)
    {
        Assert.False(MissumAiAssistantService.IsClientToolAllowed(null, name, risk, ChatMode.General));
        Assert.False(MissumAiAssistantService.IsClientToolAllowed(PromptTriggerAction.WebSearch, name, risk, ChatMode.General));
        // Codex keeps the same tool.
        Assert.True(MissumAiAssistantService.IsClientToolAllowed(PromptTriggerAction.Coding, name, risk, ChatMode.Coding));
    }

    [Theory]
    [InlineData(ClientToolNames.DocumentsList, ToolRiskClass.ReadOnly)]
    [InlineData(ClientToolNames.DocumentsSearch, ToolRiskClass.ReadOnly)]
    [InlineData(ClientToolNames.DocumentsReadPages, ToolRiskClass.ReadOnly)]
    [InlineData(SessionMemoryToolContract.Name, ToolRiskClass.ReadOnly)]
    [InlineData(BuiltInExtensionCatalog.BrowserUseToolName, ToolRiskClass.Process)]
    public void ChatGptKeepsReadingResearchAndMemoryTools(string name, ToolRiskClass risk) =>
        Assert.True(MissumAiAssistantService.IsClientToolAllowed(null, name, risk, ChatMode.General));

    [Fact]
    public void ChatGptRefusesCodexDesktopAndProjectRunsBeforeAnyPreparation()
    {
        var computer = AssistantCoordinator.CreateExtensionMatch(BuiltInActionIds.ComputerUse, "Öffne Paint");
        Assert.Throws<InvalidOperationException>(() =>
            MissumAiAssistantService.EnsureChatGptRunStaysReadOnly(ChatMode.General, PromptTriggerAction.Extension, computer));
        foreach (var action in new[] { PromptTriggerAction.Coding, PromptTriggerAction.PlanMode, PromptTriggerAction.Lean,
                     PromptTriggerAction.DocumentCreate, PromptTriggerAction.ImageGeneration })
            Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.EnsureChatGptRunStaysReadOnly(ChatMode.General, action, null));
        MissumAiAssistantService.EnsureChatGptRunStaysReadOnly(ChatMode.General, null, null);
        MissumAiAssistantService.EnsureChatGptRunStaysReadOnly(ChatMode.General, PromptTriggerAction.WebSearch, null);
        MissumAiAssistantService.EnsureChatGptRunStaysReadOnly(ChatMode.Coding, PromptTriggerAction.Coding, computer);
        MissumAiAssistantService.EnsureChatGptRunStaysReadOnly(ChatMode.ClaudeMeetings, PromptTriggerAction.Extension, computer);
    }

    [Fact]
    public async Task OlderChatGptChatWithAHoerbuchModeAndProjectStillWorks()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Alter ChatGPT-Chat", ChatMode.General);
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "Altprojekt")).FullName;
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
        await chats.SetPersistentExtensionActionIdAsync(session.Id, BuiltInActionIds.CreateAudiobook);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            ActiveSessionId = session.Id, ActiveGeneralSessionId = session.Id, SelectedChatMode = ChatMode.General,
        });
        var coordinator = CreateCoordinator(environment, settings);

        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal("general", snapshot.GetProperty("chatMode").GetString());
        // Hörbuch is a ChatGPT tool again (text only), so the stored selection stays active.
        Assert.Equal(BuiltInActionIds.CreateAudiobook, snapshot.GetProperty("selectedExtensionActionId").GetString());
        var offered = snapshot.GetProperty("actionDescriptors").EnumerateArray()
            .Select(static action => action.GetProperty("actionId").GetString()).ToArray();
        Assert.DoesNotContain(BuiltInActionIds.ComputerUse, offered);
        Assert.Contains(BuiltInActionIds.BrowserUse, offered);
        Assert.Contains(BuiltInActionIds.CreateAudiobook, offered);

        // Releasing the Hörbuch mode works; enabling a Codex-only tool does not.
        await coordinator.HandleAsync(Envelope("action.invoke",
            new { sessionId = session.Id, actionId = BuiltInActionIds.CreateAudiobook, enabled = false }), static (_, _, _) => Task.CompletedTask);
        Assert.Null((await chats.GetSessionAsync(session.Id))!.PersistentExtensionActionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(Envelope("action.invoke",
            new { sessionId = session.Id, actionId = BuiltInActionIds.ComputerUse, enabled = true }), static (_, _, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(Envelope("chat.send",
            new { sessionId = session.Id, prompt = "Öffne den Rechner.", extensionActionId = BuiltInActionIds.ComputerUse }),
            static (_, _, _) => Task.CompletedTask));
        Assert.Empty(await chats.ListMessagesAsync(session.Id));
    }

    [Fact]
    public void SessionMemoryToolHasAStrictGatewayContract()
    {
        var tool = SessionMemorySearchTool.Descriptor;
        Assert.Equal(SessionMemoryToolContract.Name, tool.ModelToolName);
        // Dynamic client tool names are lowercase dotted segments on the gateway.
        Assert.Matches("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+$", tool.ModelToolName);
        Assert.Equal(ExtensionToolRiskClass.ReadOnly, tool.RiskClass);
        Assert.InRange(tool.Description.Length, 1, 300);
        Assert.Equal(tool.Description.Trim(), tool.Description);
        Assert.InRange(tool.MaximumOutputBytes, 1_024, 64 * 1024);
        var schema = tool.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["query"], schema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(["query", "limit"], schema.GetProperty("properties").EnumerateObject().Select(static item => item.Name));
        Assert.Equal(8, schema.GetProperty("properties").GetProperty("limit").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task SessionMemorySearchFindsAnEarlierChatFromAnotherModeAndProject()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var project = Directory.CreateDirectory(Path.Combine(environment.Directory, "Heizungsprojekt")).FullName;
        var codex = await chats.CreateSessionAsync("Wärmepumpe dimensionieren", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(codex.Id, project, activateCoding: false);
        await chats.AddMessageAsync(codex.Id, ChatRole.User, "Welche Vorlauftemperatur braucht die Fußbodenheizung im Altbau?", MessageStatus.Completed);
        await chats.AddMessageAsync(codex.Id, ChatRole.Assistant,
            "Für die Fußbodenheizung im Altbau genügen meist 35 Grad Vorlauftemperatur, wenn die Räume gedämmt sind.", MessageStatus.Completed);
        var holiday = await chats.CreateSessionAsync("Urlaubsplanung", ChatMode.General);
        await chats.AddMessageAsync(holiday.Id, ChatRole.User, "Welche Strände in Portugal sind im Mai warm?", MessageStatus.Completed);
        var current = await chats.CreateSessionAsync("Neue Frage", ChatMode.General);
        await chats.AddMessageAsync(current.Id, ChatRole.User, "Was hatten wir zur Vorlauftemperatur besprochen?", MessageStatus.Completed);
        var broker = new LocalToolBroker(null!, null!, null!, chats);

        var result = await broker.ExecuteAsync(MemoryProposal(new { query = "Vorlauftemperatur Fußbodenheizung", limit = 3 }), current.Id, null);

        Assert.Equal("completed", result.Status);
        Assert.True(result.Result.GetProperty("isUntrusted").GetBoolean());
        var matches = result.Result.GetProperty("matches").EnumerateArray().ToArray();
        Assert.InRange(matches.Length, 1, 3);
        var first = matches[0];
        Assert.Equal("Wärmepumpe dimensionieren", first.GetProperty("sessionTitle").GetString());
        Assert.Equal("Codex", first.GetProperty("mode").GetString());
        Assert.Equal("Heizungsprojekt", first.GetProperty("project").GetString());
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$"), first.GetProperty("date").GetString()!);
        Assert.Contains("Vorlauftemperatur", first.GetProperty("excerpt").GetString(), StringComparison.Ordinal);
        Assert.All(matches, match => Assert.Equal("Wärmepumpe dimensionieren", match.GetProperty("sessionTitle").GetString()));

        // Capitalized umlaut words are found from a lowercase query (SQLite folds only ASCII).
        await chats.AddMessageAsync(holiday.Id, ChatRole.Assistant, "Die Ölheizung im Ferienhaus wird im Mai abgeschaltet.", MessageStatus.Completed);
        var umlaut = await broker.ExecuteAsync(MemoryProposal(new { query = "ölheizung" }), current.Id, null);
        var found = Assert.Single(umlaut.Result.GetProperty("matches").EnumerateArray());
        Assert.Equal("Urlaubsplanung", found.GetProperty("sessionTitle").GetString());
        Assert.Equal("ChatGPT", found.GetProperty("mode").GetString());
        // A chat without project carries no project field (null values are omitted on the wire).
        Assert.False(found.TryGetProperty("project", out var projectField) && projectField.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task SessionMemorySearchStaysSmallAndOnlyRunsInChatGpt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        for (var index = 0; index < 10; index++)
        {
            var earlier = await chats.CreateSessionAsync("Projekt Alpha Teil " + index, ChatMode.Coding);
            await chats.AddMessageAsync(earlier.Id, ChatRole.Assistant,
                string.Concat(Enumerable.Repeat("Zwischenstand zur Projektalpha-Schnittstelle mit langen Details. ", 60)), MessageStatus.Completed);
        }
        var current = await chats.CreateSessionAsync("Zusammenfassung", ChatMode.General);
        var codex = await chats.CreateSessionAsync("Codex", ChatMode.Coding);
        var broker = new LocalToolBroker(null!, null!, null!, chats);

        var result = await broker.ExecuteAsync(MemoryProposal(new { query = "Projektalpha Schnittstelle", limit = 8 }), current.Id, null);

        Assert.Equal("completed", result.Status);
        Assert.InRange(result.Result.GetProperty("matches").GetArrayLength(), 1, 8);
        Assert.True(Encoding.UTF8.GetByteCount(result.Result.GetRawText()) <= SessionMemorySearchTool.MaximumResultBytes);

        var outsideChatGpt = await broker.ExecuteAsync(MemoryProposal(new { query = "Projektalpha" }), codex.Id, null);
        Assert.Equal("failed", outsideChatGpt.Status);
        var invalid = await broker.ExecuteAsync(MemoryProposal(new { query = "Projektalpha", limit = 20 }), current.Id, null);
        Assert.Equal("failed", invalid.Status);
    }

    [Fact]
    public async Task CrossSessionMemorySettingIsOnByDefaultAndPersists()
    {
        Assert.True(new AppSettings().IsCrossSessionMemoryEnabled);
        // Settings files written before the option existed keep it on.
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.IsCrossSessionMemoryEnabled);
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        using var service = new AssistantSettingsService(settings, connection, environment.Get<IBackupService>());
        var viewModel = new SettingsViewModel(settings, connection,
            environment.Get<IBackupService>(), new ShellViewModel(), new ModelCapabilityRegistry(), service);
        viewModel.Initialize();
        Assert.True(viewModel.IsCrossSessionMemoryEnabled);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.IsCrossSessionMemoryEnabled = false;
        Assert.True(viewModel.HasUnsavedChanges);
        await viewModel.SavePreferencesAsync();

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.False(settings.Current.IsCrossSessionMemoryEnabled);
        using var reopened = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        Assert.False((await reopened.LoadAsync()).IsCrossSessionMemoryEnabled);

        // The browser settings editor reads and writes the same preference.
        var events = new List<(string Type, JsonElement Payload)>();
        await service.HandleAsync(new(2, "settings.update", "memory", JsonSerializer.SerializeToElement(new
        {
            expectedRevision = settings.Current.PreferencesRevision, values = new { isCrossSessionMemoryEnabled = true },
        })), (type, payload, _) =>
        {
            events.Add((type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)));
            return Task.CompletedTask;
        });
        var changed = Assert.Single(events);
        Assert.Equal("settings.changed", changed.Type);
        Assert.True(changed.Payload.GetProperty("values").GetProperty("isCrossSessionMemoryEnabled").GetBoolean());
        Assert.True(settings.Current.IsCrossSessionMemoryEnabled);
    }

    private static ToolProposal MemoryProposal(object arguments) => new(
        "memory-search-" + Guid.NewGuid().ToString("N"), "memory-run", SessionMemoryToolContract.Name,
        JsonSerializer.SerializeToElement(arguments), ToolRiskClass.ReadOnly, "Frühere Chats durchsuchen",
        DateTimeOffset.UtcNow.AddMinutes(5));

    private static WebBridgeEnvelope Envelope(string type, object payload) =>
        new(AssistantProtocol.ProtocolVersion, type, Guid.NewGuid().ToString("D"), JsonSerializer.SerializeToElement(payload));

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

        public static async Task<RequestFixture> CreateAsync(TestEnvironment environment, bool memoryEnabled)
        {
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with
            {
                MissumAiServerUrl = "http://127.0.0.1:65000",
                SelectedModel = Model,
                IsCrossSessionMemoryEnabled = memoryEnabled,
            });
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

        public async Task<ChatSession> CreateProjectSessionAsync(string title, ChatMode mode)
        {
            var chats = _environment.Get<IChatRepository>();
            var session = await chats.CreateSessionAsync(title, mode);
            var workspace = Directory.CreateDirectory(Path.Combine(_environment.Directory, "workspace-" + session.Id.ToString("N"))).FullName;
            await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: mode == ChatMode.Coding);
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
}
