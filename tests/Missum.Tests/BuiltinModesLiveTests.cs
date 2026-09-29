using System.Collections.Concurrent;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Missum.Tests;

public sealed class BuiltinModesLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    [Trait("Category", "Live")]
    public async Task TranslationAndPersistentPlanModeUseRealCoordinatorAndModelWithoutMutatingTheProject()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_BUILTIN_MODES_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = deadline.Token;
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var model = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "MISSUM_AI_LIVE_GENERAL_MODEL must select an installed general-role model.");
        var codingModel = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_CODING_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(codingModel), "MISSUM_AI_LIVE_CODING_MODEL must select an installed coding/ model.");
        PortableToolAcceptance.AssertDeepSeekReasoningOff(model!);
        PortableToolAcceptance.AssertDeepSeekReasoningOff(codingModel!);
        await settings.UpdateAsync(value => value with
        {
            MissumAiServerUrl = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080",
            IsAutomaticSpeechEnabled = false,
            SelectedModel = model, SelectedCodingModel = codingModel,
            ReasoningEffort = PortableToolAcceptance.ReasoningEffort(),
            ReasoningEffortsByModel = PortableToolAcceptance.ReasoningSelections((model!, "general"), (codingModel!, "coding")),
        });
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        var profile = AssistantRuntimeProfile.Resolve() with { DataDirectory = environment.Directory,
            NativeStateDirectory = Path.Combine(environment.Directory, "native-state") };
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), documents,
            artifacts, blobs, chats, environment.Get<IDocumentFileCodec>(), exporter);
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        var broker = new LocalToolBroker(connection, documents, documentTools, chats, catalog);
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            artifacts, environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(), blobs,
            documents, new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats), broker,
            null!, microphone, settings, recent, NullLogger<MissumAiAssistantService>.Instance, extensionActions: catalog);
        var coordinator = new AssistantCoordinator(chats, documents,
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            artifacts, environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone, profile, catalog);
        var events = new ConcurrentQueue<(string Type, JsonElement Payload)>();
        var receipts = new List<object>();
        try
        {
            await Command("mode.switch", new { chatMode = "general" });
            var general = await SessionId();
            await Command("action.invoke", new { sessionId = general, actionId = BuiltInActionIds.Translate, enabled = true });
            await Command("chat.send", new { sessionId = general, extensionActionId = BuiltInActionIds.Translate,
                prompt = "Übersetze ausschließlich diesen Satz ins Englische: Der rote Apfel liegt auf dem Tisch." });
            var translation = (await chats.ListMessagesAsync(general, ct)).Last(item => item.Role == ChatRole.Assistant);
            Assert.True(translation.Status == MessageStatus.Completed, translation.Error);
            Assert.Contains("red apple", translation.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("table", translation.Content, StringComparison.OrdinalIgnoreCase);
            receipts.Add(new { kind = "translation", sessionId = general, translation.Content, translation.Status });

            var workspace = Path.Combine(environment.Directory, "plan-workspace");
            Directory.CreateDirectory(workspace);
            var marker = "PLAN-" + Guid.NewGuid().ToString("N");
            var original = $"# Projekt {marker}\nEine Additionsfunktion soll später um eine Eingabeprüfung ergänzt werden.\n";
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), original, ct);
            await Command("session.projectCreate", new { workspacePath = workspace, chatMode = "coding" });
            var coding = await SessionId();
            await Command("action.invoke", new { sessionId = coding, actionId = BuiltInActionIds.PlanMode, enabled = true });
            Assert.Equal(BuiltInActionIds.PlanMode, (await chats.GetSessionAsync(coding, ct))!.PersistentExtensionActionId);
            await Command("chat.send", new { sessionId = coding,
                prompt = "Lies README.md mit coding.read. Erstelle einen kurzen Umsetzungsplan für die dort genannte Eingabeprüfung mit Testfällen. "
                    + "Nenne die Projektkennung aus der Datei. Dies ist ausschließlich Planung: keine Datei ändern, keine Befehle ausführen und nichts implementieren." });
            var plan = (await chats.ListMessagesAsync(coding, ct)).Last(item => item.Role == ChatRole.Assistant);
            Assert.True(plan.Status == MessageStatus.Completed, plan.Error);
            Assert.Contains(marker, plan.Content, StringComparison.Ordinal);
            Assert.Contains(plan.ToolSteps ?? [], item => ReadProjectMarker(item, marker));
            Assert.DoesNotContain(plan.ToolSteps ?? [], item => item.Status == "completed" && item.Tool is
                ClientToolNames.CodingWrite or ClientToolNames.CodingEdit or ClientToolNames.CodingCommand);
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(workspace, "README.md"), ct));
            Assert.Equal(["README.md"], Directory.EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(workspace, path))
                .Where(path => !path.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(BuiltInActionIds.PlanMode, (await chats.GetSessionAsync(coding, ct))!.PersistentExtensionActionId);
            await Command("session.open", new { sessionId = general });
            Assert.Null((await chats.GetSessionAsync(general, ct))!.PersistentExtensionActionId);
            await Command("session.open", new { sessionId = coding });
            var restored = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(ct), Json);
            Assert.Equal(BuiltInActionIds.PlanMode, restored.GetProperty("selectedExtensionActionId").GetString());
            await Command("action.invoke", new { sessionId = coding, actionId = BuiltInActionIds.PlanMode, enabled = false });
            Assert.Null((await chats.GetSessionAsync(coding, ct))!.PersistentExtensionActionId);
            Assert.False(microphone.Current.IsRecording);
            receipts.Add(new { kind = "plan", sessionId = coding, marker, plan.Content, plan.Status, plan.ToolSteps,
                sourceUnchanged = true, persistentModeRestored = true, modeDisabled = true });
            output.WriteLine(JsonSerializer.Serialize(receipts, Json));
        }
        finally
        {
            if (service.IsRunning)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await service.CancelCurrentAsync(cleanup.Token);
            }
            var directory = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "builtin-modes-" + Guid.NewGuid().ToString("N") + ".json"),
                    JsonSerializer.Serialize(new { receipts, events = events.Select(item => new { item.Type, item.Payload }) }, Json));
            }
        }

        async Task<Guid> SessionId() => JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(ct), Json)
            .GetProperty("activeSessionId").GetGuid();
        Task Command(string type, object payload) => coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, type,
            Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload, Json)), Emit, ct);
        Task Emit(string type, object payload, string? requestId)
        {
            var value = JsonSerializer.SerializeToElement(payload, Json);
            events.Enqueue((type, value));
            if (type == "host.error") throw new InvalidOperationException(value.GetProperty("message").GetString());
            if (type is "chat.started" or "chat.completed" or "chat.failed") output.WriteLine(type + " " + value);
            return Task.CompletedTask;
        }
    }

    private static bool ReadProjectMarker(AssistantToolStep step, string marker)
    {
        if (step.Tool != ClientToolNames.CodingRead || step.Status != "completed" || step.OutputJson is null) return false;
        using var result = JsonDocument.Parse(step.OutputJson);
        return result.RootElement.TryGetProperty("path", out var path) && path.GetString() == "README.md"
            && result.RootElement.TryGetProperty("content", out var content)
            && content.GetString()?.Contains(marker, StringComparison.Ordinal) == true;
    }
}
