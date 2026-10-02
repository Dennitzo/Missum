using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Missum.Tests;

public sealed class LongRunningScienceClientTests
{
    private const string Model = "fixture/long-running-science";
    private const int ContextTokens = 1_048_576;
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData(ChatMode.General, false)]
    [InlineData(ChatMode.General, true)]
    [InlineData(ChatMode.Coding, false)]
    [InlineData(ChatMode.Coding, true)]
    [InlineData(ChatMode.ClaudeScience, false)]
    [InlineData(ChatMode.ClaudeScience, true)]
    public async Task NewAndContinuedRequestsHaveNoOverallDeadlineInEveryMode(ChatMode mode, bool continuing)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            MissumAiServerUrl = "http://127.0.0.1:65000",
            SelectedModel = Model,
        });
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Lang laufende Aufgabe", mode);
        await chats.SetCodingWorkspacePathAsync(session.Id, environment.Directory);
        const string original = "Leite eine dimensionskonsistente Niedrigenergiegrenze der Gravitation her und prüfe alle Annahmen.";
        var prompt = continuing ? "Weitermachen" : original;
        if (continuing)
        {
            await chats.AddMessageAsync(session.Id, ChatRole.User, original, MessageStatus.Completed);
            await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
                "Die klassische Grenze ist erarbeitet; offen bleibt die Ein-Schleifen-Korrektur.", MessageStatus.Cancelled);
        }
        var history = await chats.ListMessagesAsync(session.Id);
        var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var documents = environment.Get<IDocumentIngestor>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            static () => new CatalogHandler());
        var broker = new LocalToolBroker(connection, documents, null!, chats);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats,
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
            new SessionContextPreparationService(chats), broker, null!, null!, settings, recent,
            NullLogger<MissumAiAssistantService>.Instance, scientificResearch: environment.Get<IScientificResearchRepository>());
        using var client = await connection.CreateClientAsync();
        PromptTriggerMatch? trigger = mode switch
        {
            ChatMode.Coding => AssistantCoordinator.CreateToolMatch("coding", prompt),
            ChatMode.ClaudeScience => AssistantCoordinator.CreateToolMatch("webSearch", prompt) with
            {
                DeepResearch = true,
                DeepResearchProfile = "mathematicalInvestigation",
            },
            _ => null,
        };
        // Exercise the actual request path without inference or starting a sandbox.
        var builder = typeof(MissumAiAssistantService).GetMethod("BuildRunRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var uploadedType = builder.GetParameters()[6].ParameterType.GenericTypeArguments.Single();
        var request = await (Task<RunRequest>)builder.Invoke(service,
        [
            client, session.Id, prompt, trigger, Array.Empty<AssistantAttachment>(), history,
            Array.CreateInstance(uploadedType, 0), assistant,
            (Func<MissumAiAssistantUpdate, Task>)(static _ => Task.CompletedTask), CancellationToken.None,
        ])!;

        Assert.NotNull(request.Limits);
        Assert.Equal(0, request.Limits.TimeoutSeconds);
        Assert.Null(request.Limits.MaximumOutputTokens);
        Assert.Equal(ContextTokens, request.Limits.MaximumContextTokens);
        Assert.Equal(session.Id.ToString("D"), request.SessionId);
        Assert.Equal(environment.Directory, mode == ChatMode.Coding ? request.CodingOptions?.WorkspacePath : request.WorkspacePath);
        if (mode == ChatMode.ClaudeScience)
        {
            Assert.True(request.DeepResearch);
            Assert.Equal($"research-{session.Id:N}", request.ResearchOptions?.ProjectId);
        }
        if (continuing)
            Assert.Contains(request.Messages, message => message.Content.Any(part => part.Text?.Contains(original, StringComparison.Ordinal) == true));

        // Zero must survive protocol serialization; omitting it would reinstate a server default.
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(request, Json));
        Assert.Equal(0, wire.RootElement.GetProperty("limits").GetProperty("timeoutSeconds").GetInt32());
        var roundTrip = JsonSerializer.Deserialize<RunRequest>(wire.RootElement.GetRawText(), Json)!;
        Assert.Equal(0, roundTrip.Limits!.TimeoutSeconds);
    }

    [Theory]
    [InlineData(32_768)]
    [InlineData(1_048_576)]
    public void ToolSelectionsCannotReintroduceAnOverallDeadline(int contextTokens)
    {
        foreach (var action in Enum.GetValues<PromptTriggerAction>())
        {
            var limits = MissumAiAssistantService.CreateGeneralChatRunLimits(contextTokens, action,
                ["coding", "research.sandbox", "workspace", "visual-tools"]);
            Assert.Equal(0, limits.TimeoutSeconds);
            Assert.Equal(contextTokens, limits.MaximumContextTokens);
            Assert.Null(limits.MaximumOutputTokens);
        }
        Assert.Equal(0, MissumAiAssistantService.CreateChatRunLimits(contextTokens).TimeoutSeconds);
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
                    ["coding.updatePlan"], [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence],
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
