using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class SubagentChatStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions ProtocolJson = MissumAiProtocol.CreateJsonOptions();

    [Fact]
    public void AssignedTaskCreatesStableDistinctMessagesWithTheirParentSession()
    {
        var session = Guid.NewGuid();
        var info = Info("Analyse\nLies die Datei systemweit und schreibe das Ergebnis im Workspace.");
        var child = SubagentChatState.Create(info, session, Start);
        var replay = SubagentChatState.Create(info, session, Start.AddMinutes(1));

        Assert.Equal("Analyse", child.Title);
        Assert.Equal(info.Task, child.UserMessage.Content);
        Assert.Equal(session, child.UserMessage.SessionId);
        Assert.Equal(session, child.AssistantMessage.SessionId);
        Assert.Equal(ChatRole.User, child.UserMessage.Role);
        Assert.Equal(ChatRole.Assistant, child.AssistantMessage.Role);
        Assert.Equal(MessageStatus.Completed, child.UserMessage.Status);
        Assert.Equal(MessageStatus.Streaming, child.AssistantMessage.Status);
        Assert.Equal(child.UserMessage.Id, replay.UserMessage.Id);
        Assert.Equal(child.AssistantMessage.Id, replay.AssistantMessage.Id);
        Assert.NotEqual(child.UserMessage.Id, child.AssistantMessage.Id);
        Assert.True(child.IsRunning);
    }

    [Theory]
    [InlineData(null, "Subagent")]
    [InlineData(" \r\n\t", "Subagent")]
    [InlineData("...", "Subagent")]
    [InlineData("\n Numerische   Validierung\tdes Oszillators.\nWeitere Details.", "Numerische Validierung des Oszillators")]
    [InlineData("Analysiere die numerische Lösung. Speichere anschließend die Grafik.", "Analysiere die numerische Lösung")]
    [InlineData("Prüfe qwen3.8-27b und den Cache", "Prüfe qwen3.8-27b und den Cache")]
    public void LifecycleTaskTitleUsesTheAssignedTaskWithoutIncludingItsFullInstructions(string? task, string expected)
    {
        Assert.Equal(expected, SubagentChatState.TaskTitle(task));
    }

    [Fact]
    public void LongLifecycleTaskTitleTrimsAtAWordBoundaryAndPreservesTheFullAssignedTask()
    {
        var task = string.Join(' ', Enumerable.Repeat("Oszillator", 20)) + "\nFühre die Simulation vollständig aus.";
        var child = SubagentChatState.Create(Info(task), Guid.NewGuid(), Start);
        Assert.Equal(task, child.UserMessage.Content);
        Assert.True(child.Title.Length <= 101);
        Assert.EndsWith("Oszillator…", child.Title, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', child.Title);
        var persisted = Assert.Single(SubagentChatState.Read([child.AssistantMessage with
        {
            ToolSteps = [Receipt(child, child.ReceiptId, JsonSerializer.Serialize(child, ProtocolJson))],
        }]));
        Assert.Equal(child.Title, persisted.Title);
        Assert.Equal(task, persisted.UserMessage.Content);
    }

    [Theory]
    [InlineData("running", "hat die Arbeit begonnen")]
    [InlineData("completed", "hat die Arbeit beendet")]
    [InlineData("Completed", "hat die Arbeit beendet")]
    [InlineData("failed", "konnte die Arbeit nicht beenden")]
    [InlineData("cancelled", "hat die Arbeit abgebrochen")]
    [InlineData("interrupted", "hat die Arbeit unterbrochen")]
    [InlineData("queued", "hat die Arbeit begonnen")]
    [InlineData("waitingForClient", "hat die Arbeit begonnen")]
    [InlineData("awaitingDelivery", "hat die Arbeit begonnen")]
    [InlineData("unavailable", "ist derzeit nicht verfügbar")]
    public void LifecycleStateShowsTheActualTerminalOrRunningOutcome(string status, string expected)
    {
        Assert.Equal(expected, SubagentChatState.LifecycleDescription(status));
    }

    [Fact]
    public void LifecycleEndsOnlyAfterTheCompletedResultWasAuthenticatedAndDeliveredToItsParent()
    {
        var child = Child();
        Assert.False(child.ResultDelivered);
        Assert.Equal(child.Title + " hat die Arbeit begonnen", child.LifecycleText);
        var completed = child.Apply(Event(child, 1, RunEventTypes.RunCompleted, new { }));
        Assert.Equal(MessageStatus.Completed, completed.AssistantMessage.Status);
        Assert.False(completed.ResultDelivered);
        Assert.Equal(child.LifecycleText, completed.LifecycleText);
        var receipt = new RunEvent(100, child.ParentRunId, "subagent.resultConsumed", Start.AddSeconds(2),
            JsonSerializer.SerializeToElement(new { agentId = child.AgentId, runId = child.RunId, state = "completed" }, ProtocolJson));
        Assert.Same(child, child.MarkResultDelivered(receipt));
        var delivered = completed.MarkResultDelivered(receipt);
        Assert.True(delivered.ResultDelivered);
        Assert.Equal(child.LifecycleText, delivered.LifecycleText);
        Assert.Null(child.CompletionReceipt(30, Start));
        Assert.Null(completed.CompletionReceipt(30, Start));
        var completion = delivered.CompletionReceipt(30, receipt.CreatedAt);
        Assert.NotNull(completion);
        Assert.Equal(SubagentChatState.CompletionTool, completion.Tool);
        Assert.Equal(child.Title + " hat die Arbeit beendet", completion.Detail);
        Assert.Equal(30, completion.ContentOffset);
        Assert.Equal(child.AgentId, completion.AgentId);
        var parent = SubagentChatState.AddStep(child.AssistantMessage, Receipt(delivered, delivered.ReceiptId, JsonSerializer.Serialize(delivered, ProtocolJson)));
        parent = SubagentChatState.AddStep(parent, completion);
        parent = SubagentChatState.AddStep(parent, delivered.CompletionReceipt(30, receipt.CreatedAt)!);
        Assert.Equal(2, parent.ToolSteps!.Count);
        Assert.Single(parent.ToolSteps, step => step.Tool == SubagentChatState.CompletionTool);
        Assert.Same(completed.AssistantMessage, delivered.AssistantMessage);
        Assert.Equal(completed.LastEventId, delivered.LastEventId);
        Assert.Same(delivered, delivered.MarkResultDelivered(receipt));
        foreach (var state in new[] { completed, delivered })
        {
            var restored = Assert.Single(SubagentChatState.Read([state.AssistantMessage with
            { ToolSteps = [Receipt(state, state.ReceiptId, JsonSerializer.Serialize(state, ProtocolJson))] }]));
            Assert.Equal(state.ResultDelivered, restored.ResultDelivered);
            Assert.Equal(state.LifecycleText, restored.LifecycleText);
        }
        var legacy = completed with { ResultDelivered = null };
        var legacyData = JsonSerializer.SerializeToElement(legacy, ProtocolJson).Deserialize<Dictionary<string, JsonElement>>()!;
        legacyData.Remove("resultDelivered");
        var restoredLegacy = Assert.Single(SubagentChatState.Read([legacy.AssistantMessage with
        { ToolSteps = [Receipt(legacy, legacy.ReceiptId, JsonSerializer.Serialize(legacyData, ProtocolJson))] }]));
        Assert.Null(restoredLegacy.ResultDelivered);
        Assert.Equal(child.Title + " hat die Arbeit begonnen", restoredLegacy.LifecycleText);
    }

    [Theory]
    [InlineData("agentId", "another-agent")]
    [InlineData("runId", "another-child")]
    [InlineData("parentRunId", "another-parent")]
    [InlineData("state", "failed")]
    public void ForeignOrUnsuccessfulResultDeliveryCannotFinishTheLifecycle(string field, string value)
    {
        var child = Child();
        child = child.Apply(Event(child, 1, RunEventTypes.RunCompleted, new { }));
        var metadata = new Dictionary<string, string> { ["agentId"] = child.AgentId, ["runId"] = child.RunId, ["state"] = "completed", [field] = value };
        var receipt = new RunEvent(100, child.ParentRunId, "subagent.resultConsumed", Start.AddSeconds(2), JsonSerializer.SerializeToElement(metadata, ProtocolJson));
        Assert.Same(child, child.MarkResultDelivered(receipt));
        Assert.False(child.ResultDelivered);
        Assert.Same(child, child.MarkResultDelivered(receipt with { RunId = "foreign-parent" }));
    }

    [Fact]
    public void TextCorrectionsRetainLiteralSyntaxAndTypesetOnlyActualMath()
    {
        var child = Child();
        const string prefix = "Analyse $x^2=4$.\n\n";
        child = child.Apply(Event(child, 1, RunEventTypes.TextDelta, new TextDeltaEvent(prefix + "vorläufig")));
        const string code = "```python\nsource = '$literal$'\nresult = 2 ** 2\n```\n\nErgebnis: 4";
        child = child.Apply(Event(child, 2, RunEventTypes.TextDelta, new TextDeltaEvent(prefix + code, ReplaceFrom: 0)));

        Assert.Equal(prefix + code, child.AssistantMessage.Content);
        var segments = NativeMathSyntax.Split(child.AssistantMessage.Content);
        Assert.Equal(child.AssistantMessage.Content, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal("$x^2=4$", Assert.Single(segments, segment => segment.IsMath).Text);
        Assert.Empty(child.AssistantMessage.ToolSteps ?? []);
    }

    [Fact]
    public void ReplayedOlderAndForeignEventsDoNotAdvanceTheDurableCursorOrDuplicateText()
    {
        var child = Child();
        var applied = child.Apply(Event(child, 10, RunEventTypes.TextDelta, new TextDeltaEvent("Ein Ergebnis")));

        Assert.Same(applied, applied.Apply(Event(child, 10, RunEventTypes.TextDelta, new TextDeltaEvent("Ein Ergebnis"))));
        Assert.Same(applied, applied.Apply(Event(child, 9, RunEventTypes.TextDelta, new TextDeltaEvent("Zu spät"))));
        Assert.Same(applied, applied.Apply(Event(child, 11, RunEventTypes.TextDelta, new TextDeltaEvent("Fremd")) with { RunId = "foreign-run" }));
        Assert.Equal(10, applied.LastEventId);
        Assert.Equal("Ein Ergebnis", applied.AssistantMessage.Content);
        Assert.Equal(child.AssistantMessage.Revision + 1, applied.AssistantMessage.Revision);
    }

    [Fact]
    public void ReasoningRemainsItsOwnDetailedReceiptAndUsesIdempotentRoundCorrections()
    {
        var child = Child();
        child = child.Apply(Event(child, 1, RunEventTypes.TextDelta, new TextDeltaEvent("Antwort")));
        child = child.Apply(Event(child, 2, RunEventTypes.ReasoningDelta,
            new ReasoningDeltaEvent("Erste Überlegung", 2, Phase: "main", AgentId: child.AgentId)));
        child = child.Apply(Event(child, 3, RunEventTypes.ReasoningDelta,
            new ReasoningDeltaEvent("Korrigierte Überlegung", 2, Phase: "main", ReplaceFrom: 0, State: "completed", AgentId: child.AgentId)));

        var reasoning = Assert.Single(child.AssistantMessage.ToolSteps!);
        Assert.Equal("assistant.reasoning", reasoning.Tool);
        Assert.Equal("Korrigierte Überlegung", reasoning.Detail);
        Assert.Equal("completed", reasoning.Status);
        Assert.Equal(child.AgentId, reasoning.AgentId);
        Assert.Equal("Antwort".Length, reasoning.ContentOffset);
        Assert.Equal("Antwort", child.AssistantMessage.Content);
        using var metadata = JsonDocument.Parse(reasoning.OutputJson!);
        Assert.Equal(3, metadata.RootElement.GetProperty("lastEventId").GetInt64());
        Assert.Same(child, child.Apply(Event(child, 3, RunEventTypes.ReasoningDelta, new ReasoningDeltaEvent("Duplikat", 2))));
    }

    [Fact]
    public void ClientToolProjectionPreservesFullArgumentsResultDetailsAndChildOwnership()
    {
        var child = Child();
        var proposal = new ToolProposal("child-proposal", child.RunId, ClientToolNames.CodingRead,
            JsonSerializer.SerializeToElement(new { path = "C:\\Users\\AMD\\outside-workspace.txt", startLine = 7 }),
            ToolRiskClass.ReadOnly, "Systemweite Datei lesen", Start.AddHours(1));
        child = child.Apply(Event(child, 1, RunEventTypes.ClientToolProposed, proposal));
        var pending = Assert.Single(child.AssistantMessage.ToolSteps!);
        var resultJson = JsonSerializer.Serialize(new { path = "C:\\Users\\AMD\\outside-workspace.txt", content = "7: x = 2\n8: result = x ** 2", truncated = false });
        var complete = new AssistantToolStep(proposal.ProposalId, proposal.Name, "completed", "2 Zeilen gelesen\n7: x = 2\n8: result = x ** 2",
            OutputJson: resultJson, ContentOffset: pending.ContentOffset, CompletedAt: Start.AddSeconds(2), UpdatedAt: Start.AddSeconds(2), AgentId: child.AgentId);
        var message = SubagentChatState.AddStep(child.AssistantMessage, complete);
        message = SubagentChatState.AddStep(message, complete);

        var step = Assert.Single(message.ToolSteps!);
        Assert.Equal("completed", step.Status);
        Assert.Equal(proposal.Arguments.GetRawText(), step.InputJson);
        Assert.Equal(resultJson, step.OutputJson);
        Assert.Equal(complete.Detail, step.Detail);
        Assert.Equal(child.AgentId, step.AgentId);
        Assert.Equal(pending.StartedAt, step.StartedAt);
        Assert.Equal(pending.Explanation, step.Explanation);
    }

    [Fact]
    public void ServerToolCompletionKeepsTheOriginalTranscriptOffsetAndInput()
    {
        var child = Child();
        child = child.Apply(Event(child, 1, RunEventTypes.TextDelta, new TextDeltaEvent("Vorher.")));
        child = child.Apply(Event(child, 2, RunEventTypes.ServerToolStarted,
            new { tool = "web.search", callId = "child-search", target = "OpenAI Agenten", arguments = new { query = "OpenAI Agenten" } }));
        var started = Assert.Single(child.AssistantMessage.ToolSteps!);
        child = child.Apply(Event(child, 3, RunEventTypes.TextDelta, new TextDeltaEvent("Danach.")));
        child = child.Apply(Event(child, 4, RunEventTypes.ServerToolCompleted,
            new { tool = "web.search", callId = "child-search", success = true, result = new { results = new[] { new { title = "Agents", url = "https://openai.github.io/openai-agents-python/" } } } }));

        var completed = Assert.Single(child.AssistantMessage.ToolSteps!);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(started.ContentOffset, completed.ContentOffset);
        Assert.Equal("Vorher.".Length, completed.ContentOffset);
        Assert.Equal(started.InputJson, completed.InputJson);
        Assert.Equal(started.StartedAt, completed.StartedAt);
        Assert.Contains("openai-agents-python", completed.OutputJson);
        Assert.Equal(child.AgentId, completed.AgentId);
    }

    [Theory]
    [InlineData(RunEventTypes.RunCompleted, "completed", MessageStatus.Completed)]
    [InlineData(RunEventTypes.RunFailed, "failed", MessageStatus.Failed)]
    [InlineData(RunEventTypes.RunCancelled, "cancelled", MessageStatus.Cancelled)]
    public void TerminalEventsFinalizeTheChildAndItsOpenToolReceipts(string type, string status, MessageStatus messageStatus)
    {
        var child = Child();
        child = child.Apply(Event(child, 1, RunEventTypes.ServerToolStarted, new { tool = "web.search", callId = "pending-tool" }));
        child = child.Apply(Event(child, 2, RunEventTypes.ModelGeneration,
            new ModelGenerationEvent("tokenProgress", GeneratedTokens: 15, CurrentTokens: 2048)));
        var terminal = child.Apply(Event(child, 3, type, type == RunEventTypes.RunFailed
            ? (object)new RunFailedEvent("fixture.failure", "Konkreter Werkzeugfehler", false) : new { }));

        Assert.Equal(status, terminal.Status);
        Assert.False(terminal.IsRunning);
        Assert.Equal(messageStatus, terminal.AssistantMessage.Status);
        Assert.Equal(status, Assert.Single(terminal.AssistantMessage.ToolSteps!).Status);
        Assert.NotNull(Assert.Single(terminal.AssistantMessage.ToolSteps!).CompletedAt);
        if (messageStatus == MessageStatus.Failed) Assert.Equal("Konkreter Werkzeugfehler", terminal.AssistantMessage.Error);
        var thinking = new NativeThinkingIndicatorState();
        thinking.ObserveProgress(child.AssistantMessage.Id.ToString(), child.GenerationState, child.GeneratedTokens, child.GenerationUpdatedAt);
        Assert.False(thinking.ShouldShow(child.AssistantMessage.Id.ToString(), terminal.IsRunning, false, true, Start.AddSeconds(3)));
    }

    [Fact]
    public void ChildTokenProgressKeepsItsThinkingCursorAcrossHiddenTabsAndEndsAtCompletion()
    {
        var child = Child();
        child = child.Apply(Event(child, 1, RunEventTypes.ModelGeneration,
            new ModelGenerationEvent("tokenProgress", GeneratedTokens: 10, CurrentTokens: 512)));
        var indicator = new NativeThinkingIndicatorState();
        var messageId = child.AssistantMessage.Id.ToString();
        indicator.ObserveProgress(messageId, child.GenerationState, child.GeneratedTokens, child.GenerationUpdatedAt);

        Assert.True(indicator.ShouldShow(messageId, child.IsRunning, false, true, Start.AddSeconds(1)));
        Assert.False(indicator.ShouldShow(messageId, child.IsRunning, false, false, Start.AddSeconds(30)));
        Assert.True(indicator.ShouldShow(messageId, child.IsRunning, false, true, Start.AddSeconds(31)));
        var terminal = child.Apply(Event(child, 2, RunEventTypes.RunCompleted, new { }));
        Assert.False(indicator.ShouldShow(messageId, terminal.IsRunning, false, true, Start.AddSeconds(32)));
    }

    [Fact]
    public void CorruptAndIncompleteNestedReceiptsCannotBreakOpeningAConversation()
    {
        var child = Child();
        var parent = child.AssistantMessage with { ToolSteps = new[]
        {
            Receipt(child, "invalid-json", "{"), Receipt(child, "missing-fields", "{}"),
            Receipt(child, "array-value", "[]"), Receipt(child, "null-value", "null"), Receipt(child, "scalar-value", "123"),
            Receipt(child, "null-message", "{\"agentId\":\"bad\",\"runId\":\"bad\",\"userMessage\":null,\"assistantMessage\":null}"),
            Receipt(child, child.ReceiptId, JsonSerializer.Serialize(child, ProtocolJson)),
        } };
        Assert.Equal(child.AgentId, Assert.Single(SubagentChatState.Read([parent])).AgentId);
    }

    [Fact]
    public async Task OversizedToolOutputUsesASmallReceiptAndReopensLosslesslyFromDisk()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Großer Subagent-Beleg", ChatMode.Coding);
        var turn = await chats.AddTurnAsync(session.Id, "Analysiere die vollständige Ausgabe.");
        var child = SubagentChatState.Create(Info(), session.Id, Start);
        var fullContent = new string('x', AssistantToolStep.MaximumStructuredJsonCharacters + 4096) + "\nEnde: Größe ✓";
        var outputJson = JsonSerializer.Serialize(new { content = fullContent, truncated = false });
        child = child with
        {
            Status = "completed", LastEventId = 42,
            AssistantMessage = child.AssistantMessage with
            {
                Content = "Die komplette Datei wurde gelesen.", Status = MessageStatus.Completed,
                ToolSteps = new[] { new AssistantToolStep("large-read", "coding.read", "completed", "Vollständiger Dateibeleg",
                    InputJson: "{\"path\":\"large.txt\"}", OutputJson: outputJson, AgentId: child.AgentId) },
            },
        };

        var receiptJson = await child.PersistAsync(environment.Directory);
        Assert.True(receiptJson.Length < 512);
        Assert.True(outputJson.Length > AssistantToolStep.MaximumStructuredJsonCharacters);
        using var receiptDocument = JsonDocument.Parse(receiptJson);
        var key = receiptDocument.RootElement.GetProperty("subagentStorageKey").GetString()!;
        Assert.Equal(64, key.Length);
        Assert.All(key, character => Assert.True(char.IsAsciiHexDigit(character)));
        var storedFile = Path.Combine(environment.Directory, "Subagents", key + ".json");
        Assert.True(File.Exists(storedFile));
        Assert.Empty(System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(storedFile)!, "*.tmp"));
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, Receipt(child, child.ReceiptId, receiptJson));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { SelectedChatMode = ChatMode.Coding, ActiveSessionId = session.Id, ActiveCodingSessionId = session.Id });

        var services = new ServiceCollection(); services.AddLogging();
        services.AddMissumInfrastructure(options => options.DataDirectory = environment.Directory);
        await using var reopened = services.BuildServiceProvider(validateScopes: true);
        await reopened.GetRequiredService<IMissumDatabase>().InitializeAsync();
        var persisted = await reopened.GetRequiredService<IChatRepository>().ListMessagesAsync(session.Id);
        Assert.Empty(SubagentChatState.Read(persisted));
        var restored = Assert.Single(SubagentChatState.Read(persisted, environment.Directory));
        Assert.Equal(child.LastEventId, restored.LastEventId);
        Assert.Equal(child.AssistantMessage.Id, restored.AssistantMessage.Id);
        Assert.Equal(outputJson, Assert.Single(restored.AssistantMessage.ToolSteps!).OutputJson);
        using var restoredOutput = JsonDocument.Parse(restored.AssistantMessage.ToolSteps![0].OutputJson!);
        Assert.Equal(fullContent, restoredOutput.RootElement.GetProperty("content").GetString());
        using var settings = new SettingsCoordinator(reopened.GetRequiredService<ISettingsStore>());
        await settings.InitializeAsync();
        var activity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance); activity.Restore();
        var coordinator = new AssistantCoordinator(reopened.GetRequiredService<IChatRepository>(), reopened.GetRequiredService<IDocumentIngestor>(),
            reopened.GetRequiredService<IContextAssembler>(), reopened.GetRequiredService<IPromptTriggerRepository>(),
            reopened.GetRequiredService<IAssistantAttachmentRepository>(), reopened.GetRequiredService<IChatArtifactRepository>(),
            reopened.GetRequiredService<IConversationSnapshotRepository>(), null, settings, activity);
        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        AssertChildSnapshot(snapshot, child);
        var projectedOutput = Assert.Single(Assert.Single(snapshot.GetProperty("subagents").EnumerateArray())
            .GetProperty("messages").EnumerateArray().Last().GetProperty("toolSteps").EnumerateArray()).GetProperty("outputJson").GetString();
        Assert.Equal(outputJson, projectedOutput);
    }

    [Fact]
    public async Task MissingExternalFilesAndTraversalOrMismatchedKeysAreIgnored()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var child = Child();
        var directory = Path.Combine(environment.Directory, "Subagents");
        System.IO.Directory.CreateDirectory(directory);
        var wrongKey = new string('b', 64);
        await File.WriteAllTextAsync(Path.Combine(directory, wrongKey + ".json"), JsonSerializer.Serialize(child, ProtocolJson));
        var missing = JsonSerializer.Serialize(new { subagentStorageKey = new string('a', 64) });
        var traversal = JsonSerializer.Serialize(new { subagentStorageKey = "../" + new string('c', 61) });
        var wrongIdentity = JsonSerializer.Serialize(new { subagentStorageKey = wrongKey });
        var invalidType = "{\"subagentStorageKey\":123}";
        var parent = child.AssistantMessage with { ToolSteps = new[]
        {
            Receipt(child, "missing-file", missing), Receipt(child, "traversal", traversal),
            Receipt(child, "wrong-identity", wrongIdentity), Receipt(child, "wrong-key-type", invalidType),
        } };

        Assert.Empty(SubagentChatState.Read([parent], environment.Directory));
        Assert.Empty(SubagentChatState.Read([parent]));
    }

    [Fact]
    public async Task OrdinaryTranscriptsRemainInlineWithoutCreatingExternalStorage()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var child = Child();
        child = child.Apply(Event(child, 1, RunEventTypes.TextDelta, new TextDeltaEvent("Ein vollständiges Ergebnis: Grüße ✓")));
        var json = await child.PersistAsync(environment.Directory);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("subagentStorageKey", out _));
        Assert.False(System.IO.Directory.Exists(Path.Combine(environment.Directory, "Subagents")));
        var parent = child.AssistantMessage with { ToolSteps = [Receipt(child, child.ReceiptId, json)] };
        var restored = Assert.Single(SubagentChatState.Read([parent]));
        Assert.Equal(child.AssistantMessage.Content, restored.AssistantMessage.Content);
        Assert.Equal(child.UserMessage.Content, restored.UserMessage.Content);
        Assert.Equal(child.LastEventId, restored.LastEventId);
    }

    [Fact]
    public async Task NestedTranscriptAndNativeSnapshotSurviveDatabaseAndCoordinatorReopen()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Delegierter Auftrag", ChatMode.Coding);
        var turn = await chats.AddTurnAsync(session.Id, "Erstelle die Auswertung.");
        var child = SubagentChatState.Create(Info("Datei analysieren und Ergebnis liefern"), session.Id, Start);
        child = child.Apply(Event(child, 1, RunEventTypes.TextDelta, new TextDeltaEvent("Ergebnis $x=2$.\n\n```python\nx = 2\n```")));
        child = child.Apply(Event(child, 2, RunEventTypes.ServerToolStarted, new { tool = "web.search", callId = "persisted-search", arguments = new { query = "OpenAI Agents SDK" } }));
        child = child.Apply(Event(child, 3, RunEventTypes.ServerToolCompleted, new { tool = "web.search", callId = "persisted-search", success = true, result = new { results = new[] { new { title = "Agents SDK", url = "https://openai.github.io/openai-agents-python/" } } } }));
        child = child.Apply(Event(child, 4, RunEventTypes.RunCompleted, new { }));
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, Receipt(child, child.ReceiptId, JsonSerializer.Serialize(child, ProtocolJson)));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { SelectedChatMode = ChatMode.Coding, ActiveSessionId = session.Id, ActiveCodingSessionId = session.Id });

        var services = new ServiceCollection(); services.AddLogging();
        services.AddMissumInfrastructure(options => options.DataDirectory = environment.Directory);
        await using var reopened = services.BuildServiceProvider(validateScopes: true);
        await reopened.GetRequiredService<IMissumDatabase>().InitializeAsync();
        var persisted = await reopened.GetRequiredService<IChatRepository>().ListMessagesAsync(session.Id);
        var restored = Assert.Single(SubagentChatState.Read(persisted));
        Assert.Equal(child.AgentId, restored.AgentId);
        Assert.Equal(child.AssistantMessage.Content, restored.AssistantMessage.Content);
        Assert.Equal(child.LastEventId, restored.LastEventId);
        Assert.Equal(child.AssistantMessage.ToolSteps!.Single().OutputJson, restored.AssistantMessage.ToolSteps!.Single().OutputJson);
        Assert.Equal(child.AssistantMessage.Id, restored.AssistantMessage.Id);
        Assert.Equal(MessageStatus.Completed, restored.AssistantMessage.Status);
        using var settings = new SettingsCoordinator(reopened.GetRequiredService<ISettingsStore>());
        await settings.InitializeAsync();
        var activity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance); activity.Restore();
        var coordinator = new AssistantCoordinator(reopened.GetRequiredService<IChatRepository>(), reopened.GetRequiredService<IDocumentIngestor>(),
            reopened.GetRequiredService<IContextAssembler>(), reopened.GetRequiredService<IPromptTriggerRepository>(),
            reopened.GetRequiredService<IAssistantAttachmentRepository>(), reopened.GetRequiredService<IChatArtifactRepository>(),
            reopened.GetRequiredService<IConversationSnapshotRepository>(), null, settings, activity);
        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        AssertChildSnapshot(snapshot, child);
        JsonElement conversation = default;
        await coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, "conversation.refresh", "subagent-reopen",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id })), (type, payload, _) =>
            { Assert.Equal("conversation.snapshot", type); conversation = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web); return Task.CompletedTask; });
        AssertChildSnapshot(conversation, child);
    }

    private static void AssertChildSnapshot(JsonElement snapshot, SubagentChatState expected)
    {
        var child = Assert.Single(snapshot.GetProperty("subagents").EnumerateArray());
        Assert.Equal(expected.AgentId, child.GetProperty("agentId").GetString());
        Assert.False(child.GetProperty("isRunning").GetBoolean());
        var messages = child.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal(expected.UserMessage.Content, messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal(expected.AssistantMessage.Content, messages[1].GetProperty("content").GetString());
        Assert.Equal("completed", messages[1].GetProperty("status").GetString());
        Assert.Equal(expected.AssistantMessage.Id, child.GetProperty("runMessageId").GetGuid());
        Assert.Equal(expected.AgentId, Assert.Single(messages[1].GetProperty("toolSteps").EnumerateArray()).GetProperty("agentId").GetString());
    }

    private static SubagentRunEvent Info(string task = "Analysiere die Datei") => new("parent-run", "child-run", "child-agent", task, "local-model", RunState.Running);
    private static SubagentChatState Child() => SubagentChatState.Create(Info(), Guid.NewGuid(), Start);
    private static RunEvent Event(SubagentChatState child, long id, string type, object data) => new(id, child.RunId, type, Start.AddSeconds(id), JsonSerializer.SerializeToElement(data, ProtocolJson));
    private static AssistantToolStep Receipt(SubagentChatState child, string id, string json) => new(id, "subagent", child.IsRunning ? "running" : child.Status, child.UserMessage.Content,
        OutputJson: json, StartedAt: Start, UpdatedAt: Start.AddMinutes(1));
}
