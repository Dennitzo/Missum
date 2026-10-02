using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;
using Missum.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Missum.Tests;

public sealed class ScienceRestartContextTests
{
    private const string Model = "fixture/science-restart";
    private const string SessionTitle = "Forschungsprojekt: Quantengravitation";
    private const string ContinuePrompt = "Weitermachen";
    private const string InterpretedQuestion = "Prüfe eine effektive Gravitationstheorie mit skalarer Materie und kontrollierter Niedrigenergiegrenze.";
    private const string Report = "# Zwischenstand der Quantengravitation\n\nDie Einsteinschen Feldgleichungen sind hergeleitet. "
        + "Offen bleibt die dimensionskonsistente Ein-Schleifen-Korrektur; sie ist noch nicht bewiesen.";
    private const string Claim = "Die klassische Niedrigenergiegrenze reproduziert die Einsteinschen Feldgleichungen.";
    private const string ExperimentCommand = "python simulations/check_low_energy.py --seed 42";
    private const string ForeignQuestion = "FREMDES_PROJEKT_DARF_NICHT_IM_KONTEXT_AUFTAUCHEN";
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData(MessageStatus.Completed)]
    [InlineData(MessageStatus.Cancelled)]
    [InlineData(MessageStatus.Interrupted)]
    public async Task ContinueAfterSqliteReopenRestoresScientificQuestionReportAndCheckpoint(
        MessageStatus previousStatus)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var repository = environment.Get<IScientificResearchRepository>();
        var session = await chats.CreateSessionAsync(SessionTitle, ChatMode.ClaudeScience);
        var workspace = Path.Combine(environment.Directory, "Claude Science");
        Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        var originalQuestion = CreateOriginalQuestion();
        Assert.Equal(7_919, originalQuestion.Length);
        await chats.AddMessageAsync(session.Id, ChatRole.User, originalQuestion, MessageStatus.Completed);
        var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
            "Einsteins Feldgleichungen und die Quantisierung der skalaren Materie wurden eingeordnet. "
            + "Als Nächstes prüfen wir die Ein-Schleifen-Korrektur.", previousStatus);
        await chats.SaveToolStepAsync(assistant.Id, new AssistantToolStep("science-source", "web.fetch", "completed",
            "Originalquelle zur effektiven Gravitation gelesen",
            InputJson: "{\"url\":\"https://example.org/quantum-gravity\"}",
            OutputJson: "{\"title\":\"Effective quantum gravity\",\"content\":\"low energy limit\"}", ContentOffset: 0));
        var projectId = $"research-{session.Id:N}";
        var researchWorkspace = Path.Combine(workspace, "Science", projectId);
        Directory.CreateDirectory(researchWorkspace);
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertProjectAsync(new ScientificResearchProject(projectId, session.Id,
            "mathematicalInvestigation", originalQuestion, InterpretedQuestion, "sandboxResearch", "multiPath",
            "unresolved", 1, 7, now, now, researchWorkspace));
        await repository.SaveCheckpointAsync(new ResearchCheckpoint("science-restart-checkpoint", projectId,
            "run-before-restart", 7, "verification.pending", "{\"nextStep\":\"one-loop correction\"}", now));
        await repository.SaveResultSnapshotAsync(projectId, new ResearchResultSnapshot([], [new ResearchExperiment(
            "science-restart-experiment", projectId, "python.lock", "[\"simulations/check_low_energy.py\"]", "[42]", "{}",
            ExperimentCommand, "{}", "classical limit verified", "", "[]", "verified", now, now)], [],
            [new ResearchClaim("science-restart-claim", projectId, Claim, "calculated", "provisionallySupported", .8, "{}", now)]));
        await repository.SaveArchiveSnapshotAsync(projectId, new ResearchArchiveSnapshot(1, "{}", [], [],
            "science-restart-report", "scientificMarkdown", "unresolved", Report, "{}", "research.result.persisted", "{}",
            "run-before-restart", 7, now));
        // A second session makes accidental cross-project restoration observable.
        var other = await chats.CreateSessionAsync("Andere Forschung", ChatMode.ClaudeScience);
        await repository.UpsertProjectAsync(new ScientificResearchProject($"research-{other.Id:N}", other.Id,
            "mathematicalInvestigation", ForeignQuestion, ForeignQuestion, "readOnlyResearch", "multiPath",
            "active", 1, 1, now, now));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
        {
            MissumAiServerUrl = "http://127.0.0.1:65000",
            ActiveSessionId = session.Id,
            SelectedModel = Model,
        });

        var before = await CaptureContinuationAsync(environment.Services, session.Id, assistant.Id);
        AssertProjectIdentity(await repository.GetProjectAsync(projectId), originalQuestion, researchWorkspace);
        await environment.Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMissumInfrastructure(options => options.DataDirectory = environment.Directory);
        await using var restarted = registrations.BuildServiceProvider(validateScopes: true);
        await restarted.GetRequiredService<IMissumDatabase>().InitializeAsync();
        var after = await CaptureContinuationAsync(restarted, session.Id, assistant.Id);

        Assert.NotEqual(before.ClientId, after.ClientId);
        Assert.Equal(before.RequestJson, after.RequestJson);
        Assert.Equal(before.HistoryJson, after.HistoryJson);
        Assert.Equal(session.Id.ToString("D"), after.Request.SessionId);
        Assert.True(after.Request.DeepResearch);
        Assert.Equal(projectId, after.Request.ResearchOptions?.ProjectId);
        Assert.Equal(workspace, after.Request.WorkspacePath);
        Assert.Equal(Model, after.Request.PreferredGeneralModelId);
        Assert.Contains(after.Request.Messages, message => message.Role == "user"
            && message.Content.Any(part => part.Text == originalQuestion));
        Assert.Contains(after.Request.Messages, message => message.Role == "assistant"
            && message.Content.Any(part => part.Text?.Contains("Ein-Schleifen-Korrektur", StringComparison.Ordinal) == true));
        Assert.DoesNotContain(ForeignQuestion, after.RequestJson, StringComparison.Ordinal);
        var latestText = after.Request.Messages[^1].Content.Single(part => part.Type == "text").Text!;
        var context = ParseScientificContext(latestText);
        Assert.Equal(originalQuestion, context.GetProperty("originalQuestion").GetString());
        Assert.Equal(InterpretedQuestion, context.GetProperty("interpretedQuestion").GetString());
        Assert.Equal("verification.pending", context.GetProperty("checkpoint").GetProperty("stage").GetString());
        Assert.Equal(7, context.GetProperty("checkpoint").GetProperty("revision").GetInt64());
        Assert.Equal("unresolved", context.GetProperty("report").GetProperty("status").GetString());
        Assert.Equal(Report, context.GetProperty("report").GetProperty("content").GetString());
        Assert.Contains(context.GetProperty("claims").EnumerateArray(), claim =>
            claim.GetProperty("statement").GetString() == Claim
            && claim.GetProperty("status").GetString() == "provisionallySupported");
        Assert.Contains(context.GetProperty("experiments").EnumerateArray(), experiment =>
            experiment.GetProperty("command").GetString() == ExperimentCommand
            && experiment.GetProperty("status").GetString() == "verified");
        Assert.Contains("AKTUELLER NUTZERAUFTRAG\n" + ContinuePrompt, latestText, StringComparison.Ordinal);
        AssertProjectIdentity(await restarted.GetRequiredService<IScientificResearchRepository>().GetProjectAsync(projectId),
            originalQuestion, researchWorkspace);
        Assert.Equal(SessionTitle, (await restarted.GetRequiredService<IChatRepository>().GetSessionAsync(session.Id))?.Title);
        Assert.Equal(2, (await restarted.GetRequiredService<IChatRepository>().ListMessagesAsync(session.Id)).Count);
    }

    private static string CreateOriginalQuestion()
    {
        const string introduction = "Forschungsprojekt: Quantengravitation – von Einsteins Feldgleichungen zu einem überprüfbaren Vereinheitlichungsansatz.\n\n";
        const string requirements = "Leite die Einsteinschen Feldgleichungen aus der Wirkung her und prüfe die Kopplung an quantisierte skalare Materie. "
            + "Dokumentiere vollständige Rechenschritte, Einheiten und Annahmen. Unterscheide überprüfte Aussagen von offenen Fragen.\n";
        const string conclusion = "\nDer nächste offene Schritt ist die Ein-Schleifen-Korrektur in der effektiven Niedrigenergiegrenze.";
        var content = new StringBuilder(introduction);
        while (content.Length < 7_919 - conclusion.Length) content.Append(requirements);
        content.Length = 7_919 - conclusion.Length;
        return content.Append(conclusion).ToString();
    }

    private static void AssertProjectIdentity(ScientificResearchProject? project, string originalQuestion, string workspace)
    {
        Assert.NotNull(project);
        Assert.Equal(originalQuestion, project.OriginalQuestion);
        Assert.Equal(InterpretedQuestion, project.InterpretedQuestion);
        Assert.Equal("science-restart-checkpoint", project.LatestCheckpointId);
        Assert.Equal(workspace, project.WorkspacePath);
    }

    private static JsonElement ParseScientificContext(string text)
    {
        const string start = "[MISSUM_SCIENCE_SESSION_CONTEXT]";
        const string end = "[/MISSUM_SCIENCE_SESSION_CONTEXT]";
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        var endIndex = text.IndexOf(end, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, "The current research request must include its persisted scientific context.");
        var block = text[(startIndex + start.Length)..endIndex];
        var jsonStart = block.IndexOf('{');
        var jsonEnd = block.LastIndexOf('}');
        Assert.True(jsonStart >= 0 && jsonEnd > jsonStart, "The restored context must contain its structured scientific snapshot.");
        using var document = JsonDocument.Parse(block[jsonStart..(jsonEnd + 1)]);
        return document.RootElement.Clone();
    }

    private static async Task<Capture> CaptureContinuationAsync(ServiceProvider provider, Guid sessionId, Guid assistantId)
    {
        var chats = provider.GetRequiredService<IChatRepository>();
        var documents = provider.GetRequiredService<IDocumentIngestor>();
        using var settings = new SettingsCoordinator(provider.GetRequiredService<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(static current => current);
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new CatalogHandler(clientIds));
        var broker = new LocalToolBroker(connection, documents, null!, chats);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats,
            provider.GetRequiredService<IAssistantAttachmentRepository>(), provider.GetRequiredService<IChatArtifactRepository>(),
            provider.GetRequiredService<IMissumAiRunRepository>(), provider.GetRequiredService<IClientToolExecutionRepository>(),
            provider.GetRequiredService<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
            new SessionContextPreparationService(chats), broker, null!, null!, settings, recent,
            NullLogger<MissumAiAssistantService>.Instance,
            scientificResearch: provider.GetRequiredService<IScientificResearchRepository>());
        Assert.Equal(0, await chats.MarkStreamingMessagesInterruptedAsync());
        await service.StopPersistedRunsAtStartupAsync();
        Assert.Equal(0, await chats.DeleteEmptyTerminalMessagesAsync());
        var coordinator = new AssistantCoordinator(chats, documents,
            provider.GetRequiredService<IContextAssembler>(), provider.GetRequiredService<IPromptTriggerRepository>(),
            provider.GetRequiredService<IAssistantAttachmentRepository>(), provider.GetRequiredService<IChatArtifactRepository>(),
            provider.GetRequiredService<IConversationSnapshotRepository>(), service, settings, recent);
        await coordinator.HandleAsync(new WebBridgeEnvelope(AssistantWebBridge.ProtocolVersion, "session.open", "reopen-science",
            JsonSerializer.SerializeToElement(new { sessionId })), static (_, _, _) => Task.CompletedTask);
        Assert.Equal(sessionId, settings.Current.ActiveSessionId);
        Assert.Equal(ChatMode.ClaudeScience, (await chats.GetSessionAsync(sessionId))?.ChatMode);
        var history = await chats.ListMessagesAsync(sessionId);
        var assistant = (await chats.GetMessageAsync(assistantId))!;
        using var client = await connection.CreateClientAsync();
        var trigger = AssistantCoordinator.CreateToolMatch("webSearch", ContinuePrompt) with
        {
            DeepResearch = true,
            DeepResearchProfile = "mathematicalInvestigation",
        };
        // Exercise the production request builder with durable repositories but
        // without adding a turn, running a model, or preparing a Docker sandbox.
        var builder = typeof(MissumAiAssistantService).GetMethod("BuildRunRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The request builder was not found.");
        var uploadedType = builder.GetParameters()[6].ParameterType.GenericTypeArguments.Single();
        var requestTask = (Task<RunRequest>)builder.Invoke(service,
        [
            client, sessionId, ContinuePrompt, trigger, Array.Empty<AssistantAttachment>(), history,
            Array.CreateInstance(uploadedType, 0), assistant,
            (Func<MissumAiAssistantUpdate, Task>)(static _ => Task.CompletedTask), CancellationToken.None,
        ])!;
        var request = await requestTask;
        return new Capture(request, JsonSerializer.Serialize(request, Json), JsonSerializer.Serialize(history, Json), Assert.Single(clientIds));
    }

    private sealed record Capture(RunRequest Request, string RequestJson, string HistoryJson, string ClientId);

    private sealed class CatalogHandler(HashSet<string> clientIds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clientIds.Add(request.Headers.GetValues(MissumAiHeaders.ClientId).Single());
            var now = DateTimeOffset.UtcNow;
            ModelRuntimeStatus[] models = [new(Model, "general", true, true, "loaded", 1_048_576)];
            object value = request.RequestUri!.AbsolutePath switch
            {
                "/v1/models/status" => new ModelStatusSnapshot(true, "fixture", models, now),
                "/v1/capabilities" => new CapabilitySnapshot(MissumAiProtocol.Version, "fixture", [], [], [],
                    new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize),
                _ => throw new InvalidOperationException("No inference is allowed in the scientific restart fixture: " + request.RequestUri),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
            });
        }
    }
}
