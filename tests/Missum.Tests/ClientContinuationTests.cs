using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Coding;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Tests;

public sealed class ClientContinuationTests
{
    private const string Partial = "Bereits erarbeitete Herleitung.";
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    [Fact]
    public void MetadataOnlyDeltaCannotTrimRetainedAnswerOrBreakSubsequentStreaming()
    {
        const string prefix = "Bisherige Antwort.\n\n";
        var metadata = MissumAiAssistantService.ApplyContinuationTextDelta(prefix,
            new("MISSUM_SESSION_TITLE: Versteckt", ReplaceFrom: 0), prefix);
        var visible = MissumAiAssistantService.NormalizeContinuationNarration(metadata, prefix);
        Assert.Equal(prefix, visible);
        Assert.Equal(prefix + "Nächster Absatz.", MissumAiAssistantService.ApplyContinuationTextDelta(visible,
            new("Nächster Absatz.", ReplaceFrom: 0), prefix));
    }

    [Fact]
    public async Task CancelledTurnContinuesSameAnchorWithoutAnotherUserMessageAndPreservesReceiptsAndTitle()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled);
        using var host = await Host.CreateAsync(environment, handler);
        var started = new List<MissumAiAssistantUpdate>();
        var final = await host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id,
            update => { if (update.Kind == MissumAiAssistantUpdateKind.Started) started.Add(update); return Task.CompletedTask; });
        Assert.Equal(MessageStatus.Completed, final.Status);
        Assert.Equal(seeded.Message.Id, final.Id);
        Assert.StartsWith(Partial, final.Content);
        Assert.Contains("Neue Fortsetzung.", final.Content);
        var messages = await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("Leite die Energiegleichung vollständig her.", messages[0].Content);
        Assert.Equal(seeded.Session.Title, (await environment.Get<IChatRepository>().GetSessionAsync(seeded.Session.Id))!.Title);
        Assert.Contains(final.ToolSteps!, step => step.Id == "original-read" && step.Detail == "Herkunftsdatei gelesen");
        Assert.Contains(final.ToolSteps!, step => step.Tool == MissumAiAssistantService.ContinuationStepTool && step.Status == "completed");
        var request = Assert.Single(handler.Requests);
        Assert.Contains(request.Messages, message => message.Role == "user" && message.Content.Any(part => part.Text == messages[0].Content));
        Assert.Contains(request.Messages, message => message.Role == "assistant" && message.Content.Any(part => part.Text == Partial));
        Assert.NotNull(Assert.Single(started).LocalRunId);
        Assert.Equal("run-new", host.Service.ActiveRunId ?? handler.LastCreatedRun);
    }

    [Fact]
    public async Task InterruptedActiveServerJobReattachesFromDurableCursorWithoutCreatingRun()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Interrupted);
        var handler = new ContinuationHandler(RunState.Running);
        using var host = await Host.CreateAsync(environment, handler);
        var final = await host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);
        Assert.Equal(MessageStatus.Completed, final.Status);
        Assert.StartsWith(Partial, final.Content);
        Assert.Empty(handler.Requests);
        Assert.Equal("8", handler.LastEventCursor);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("mac-tab")]
    public async Task ManualContinuationUsesInvokingClientChoiceFrozenBeforePreparation(string clientId)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockLookup = true };
        using var host = await Host.CreateAsync(environment, handler);
        using var views = new AssistantClientStateStore(environment.Directory);
        using var client = AssistantClientExecutionScope.Enter(views, clientId);
        await host.Settings.UpdateAsync(current => current with
        {
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "xhigh" },
        });
        await AssistantClientExecutionScope.UpdateAsync(host.Settings, current => current with
        {
            SelectedModel = "fixture/selected",
        }, CancellationToken.None);
        await environment.Get<IChatRepository>().SaveDraftAsync(seeded.Session.Id, "Unsent next prompt");
        var updates = new List<MissumAiAssistantUpdate>();
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id,
            update => { updates.Add(update); return Task.CompletedTask; });
        await handler.LookupReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await host.Settings.UpdateAsync(current => current with
        {
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "low" },
        });
        await AssistantClientExecutionScope.UpdateAsync(host.Settings, current => current with
        {
            SelectedModel = "fixture/later",
        }, CancellationToken.None);
        handler.ReleaseLookup.TrySetResult();

        var final = await continuation;
        var request = Assert.Single(handler.Requests);
        Assert.Equal("fixture/selected", request.PreferredGeneralModelId);
        Assert.Equal("xhigh", request.ReasoningEffort);
        Assert.Equal("fixture/selected", Assert.Single(updates, update => update.Kind == MissumAiAssistantUpdateKind.Started).Model);
        Assert.Equal("fixture/later", AssistantClientExecutionScope.Resolve(host.Settings.Current).SelectedModel);
        Assert.Equal(seeded.Message.Id, final.Id);
        Assert.StartsWith(Partial, final.Content);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
        Assert.Equal("Unsent next prompt", (await environment.Get<IChatRepository>().GetSessionAsync(seeded.Session.Id))!.Draft);
        Assert.Equal("fixture/selected", (await environment.Get<IMissumAiRunRepository>().GetByAssistantMessageIdAsync(seeded.Message.Id))!.SelectedModel);
    }

    [Fact]
    public async Task DirectNativeContinuationWithoutAClientScopeAlsoFreezesModelAndReasoning()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockLookup = true };
        using var host = await Host.CreateAsync(environment, handler);
        await host.Settings.UpdateAsync(current => current with
        {
            SelectedModel = "fixture/selected",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "xhigh" },
        });
        var originalChoices = host.Settings.Current.ReasoningEffortsByModel;
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);
        await handler.LookupReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        originalChoices["text:fixture/selected"] = "low";
        await host.Settings.UpdateAsync(current => current with { SelectedModel = "fixture/later" });
        handler.ReleaseLookup.TrySetResult();

        Assert.Equal(MessageStatus.Completed, (await continuation).Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("fixture/selected", request.PreferredGeneralModelId);
        Assert.Equal("xhigh", request.ReasoningEffort);
        Assert.False(AssistantClientExecutionScope.IsFrozen);
        Assert.Equal("fixture/later", host.Settings.Current.SelectedModel);
    }

    [Fact]
    public async Task CodingContinuationKeepsOriginalWorkspaceAndUsesSubmittedCodingModelAndReasoning()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled, ChatMode.Coding);
        var originalWorkspace = seeded.Session.CodingWorkspacePath!;
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockLookup = true, OriginalRunMode = RunMode.Coding };
        using var host = await Host.CreateAsync(environment, handler);
        await host.Settings.UpdateAsync(current => current with
        {
            SelectedModel = "fixture/selected",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "xhigh" },
        });
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);
        await handler.LookupReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var otherWorkspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "later-project")).FullName;
        await environment.Get<IChatRepository>().SetCodingWorkspacePathAsync(seeded.Session.Id, otherWorkspace, activateCoding: true);
        await host.Settings.UpdateAsync(current => current with
        {
            SelectedModel = "fixture/later",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "medium" },
        });
        handler.ReleaseLookup.TrySetResult();

        var final = await continuation;
        Assert.Equal(MessageStatus.Completed, final.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(RunMode.Coding, request.Mode);
        Assert.Equal("fixture/selected", request.PreferredCodingModelId);
        Assert.Null(request.PreferredGeneralModelId);
        Assert.Equal("xhigh", request.ReasoningEffort);
        Assert.Equal(originalWorkspace, request.CodingOptions!.WorkspacePath);
        Assert.True(request.CodingOptions.ContinueSessionContext);
        var persisted = (await environment.Get<IMissumAiRunRepository>().GetByAssistantMessageIdAsync(seeded.Message.Id))!;
        Assert.Equal(originalWorkspace, persisted.WorkspacePath);
        Assert.Equal(PromptTriggerAction.Coding, persisted.Action);
        Assert.Equal(seeded.Message.Id, final.Id);
        Assert.StartsWith(Partial, final.Content);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
        Assert.False((await CodingChangesMonitor.ReadLatestAsync(CodingChangesMonitor.StorageDirectory(environment.Directory,
            seeded.Session.Id, seeded.Message.Id)))!.IsPartial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScienceContinuationUsesSubmittedModelAndKeepsCanonicalResearchProtocolAndSession(bool webSearch)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled, ChatMode.ClaudeScience,
            webSearch ? PromptTriggerAction.WebSearch : null);
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockLookup = true };
        using var host = await Host.CreateAsync(environment, handler, includeScientificRepository: true);
        await host.Settings.UpdateAsync(current => current with
        {
            SelectedModel = "fixture/selected",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "xhigh" },
        });
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);
        await handler.LookupReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await host.Settings.UpdateAsync(current => current with { SelectedModel = "fixture/later" });
        handler.ReleaseLookup.TrySetResult();

        var final = await continuation;
        Assert.Equal(MessageStatus.Completed, final.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(webSearch ? RunMode.General : RunMode.Auto, request.Mode);
        Assert.Equal("fixture/selected", request.PreferredGeneralModelId);
        Assert.Equal("xhigh", request.ReasoningEffort);
        Assert.True(request.DeepResearch);
        Assert.Contains("visual-tools", request.ClientCapabilities!);
        Assert.Contains("media.inspect", request.AllowedServerTools!);
        Assert.Contains("media.analyze", request.AllowedServerTools!);
        Assert.Equal(seeded.Session.Id.ToString("D"), request.SessionId);
        Assert.Equal("research-" + seeded.Session.Id.ToString("N"), request.ResearchOptions!.ProjectId);
        Assert.Equal(2L, request.ResearchOptions.ProtocolVersion);
        Assert.Equal(ResearchVerificationLevel.MultiPath, request.ResearchOptions.VerificationLevel);
        Assert.Equal(ChatMode.ClaudeScience, (await environment.Get<IChatRepository>().GetSessionAsync(seeded.Session.Id))!.ChatMode);
        Assert.Equal(seeded.Message.Id, final.Id);
        Assert.StartsWith(Partial, final.Content);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Theory]
    [InlineData("fixture/selected", "high")]
    [InlineData("fixture/model", "low")]
    public async Task ManualReattachRequestsCurrentModelAndReasoningWithoutStartingAParallelRun(string modelId, string effort)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Interrupted);
        var handler = new ContinuationHandler(RunState.Running) { BlockLookup = true };
        using var host = await Host.CreateAsync(environment, handler);
        await host.Settings.UpdateAsync(current => current with
        {
            SelectedModel = modelId,
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:" + modelId] = effort },
        });
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);
        await handler.LookupReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await host.Settings.UpdateAsync(current => current with { SelectedModel = "fixture/later" });
        handler.ReleaseLookup.TrySetResult();

        Assert.Equal(MessageStatus.Completed, (await continuation).Status);
        var selection = Assert.Single(handler.ModelSelections);
        Assert.Equal(seeded.Session.Id.ToString("D"), selection.SessionId);
        Assert.Equal(modelId, selection.ModelId);
        Assert.Equal(effort, selection.ReasoningEffort);
        Assert.Empty(handler.Requests);
        Assert.Equal("8", handler.LastEventCursor);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Fact]
    public async Task AutomaticReattachmentKeepsOriginalModelDespiteChangedComposerChoice()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Interrupted);
        var handler = new ContinuationHandler(RunState.Running);
        using var host = await Host.CreateAsync(environment, handler);
        await host.Settings.UpdateAsync(current => current with
        {
            SelectedModel = "fixture/selected",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "xhigh" },
        });

        await host.Service.ResumePendingAsync(_ => Task.CompletedTask);
        Assert.Empty(handler.Requests);
        Assert.Empty(handler.ModelSelections);
        Assert.Equal("8", handler.LastEventCursor);
        Assert.Equal("fixture/model", (await environment.Get<IMissumAiRunRepository>().GetByAssistantMessageIdAsync(seeded.Message.Id))!.SelectedModel);
        Assert.Equal("fixture/selected", host.Settings.Current.SelectedModel);
    }

    [Theory]
    [InlineData(true, "high")]
    [InlineData(false, null)]
    public async Task ManualReattachRestoresModelDefaultAfterStoredReasoningIsRemoved(bool supportsReasoning, string? expected)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Interrupted);
        var handler = new ContinuationHandler(RunState.Running) { OriginalModelSupportsReasoning = supportsReasoning };
        using var host = await Host.CreateAsync(environment, handler);
        await host.Settings.UpdateAsync(current => current with
        {
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/model"] = "xhigh" },
        });
        await host.Settings.UpdateAsync(current => current with { ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) });

        Assert.Equal(MessageStatus.Completed, (await host.Service.ResumeMessageAsync(seeded.Session.Id,
            seeded.Message.Id, _ => Task.CompletedTask)).Status);
        var selection = Assert.Single(handler.ModelSelections);
        Assert.Equal("fixture/model", selection.ModelId);
        Assert.Equal(expected, selection.ReasoningEffort);
        Assert.Equal(expected, handler.SelectedReasoningEffort);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CompletedServerSnapshotDoesNotStartAnotherInferenceForANewSelection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Interrupted);
        var handler = new ContinuationHandler(RunState.Completed);
        using var host = await Host.CreateAsync(environment, handler);
        await host.Settings.UpdateAsync(current => current with { SelectedModel = "fixture/selected" });

        Assert.Equal(MessageStatus.Completed, (await host.Service.ResumeMessageAsync(seeded.Session.Id,
            seeded.Message.Id, _ => Task.CompletedTask)).Status);
        Assert.Empty(handler.Requests);
        Assert.Empty(handler.ModelSelections);
    }

    [Fact]
    public async Task ConcurrentDoubleClickDoesNotSubmitTwoAttempts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockCreate = true };
        using var host = await Host.CreateAsync(environment, handler);
        var first = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);
        await handler.CreateReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.ResumeMessageAsync(seeded.Session.Id,
            seeded.Message.Id, _ => Task.CompletedTask));
        handler.ReleaseCreate.TrySetResult();
        Assert.Equal(MessageStatus.Completed, (await first).Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PreparationIsReportedBeforeGatewayLookupWithoutAnOptimisticStartReceipt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockLookup = true };
        using var host = await Host.CreateAsync(environment, handler);
        var updates = new List<MissumAiAssistantUpdate>();
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id,
            update => { updates.Add(update); return Task.CompletedTask; });
        await handler.LookupReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var preparing = Assert.Single(updates);
        Assert.Equal(MissumAiAssistantUpdateKind.Status, preparing.Kind);
        Assert.Equal("AI-Modell und Dienste werden vorbereitet", preparing.Status);
        Assert.Equal(seeded.Message.Id, preparing.Message.Id);
        Assert.Null(preparing.ToolStep);
        Assert.Empty(handler.Requests);
        Assert.Null(host.Service.ActiveRunId);
        Assert.DoesNotContain((await environment.Get<IChatRepository>().GetMessageAsync(seeded.Message.Id))!.ToolSteps!,
            step => step.Tool == MissumAiAssistantService.ContinuationStepTool);
        handler.ReleaseLookup.TrySetResult();
        Assert.Equal(MessageStatus.Completed, (await continuation).Status);
        Assert.Single(updates, update => update.Kind == MissumAiAssistantUpdateKind.Started);
    }

    [Fact]
    public async Task CancellingGatewayPreparationPreservesStoppedAnswerAndCreatesNoServerAttempt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockLookup = true };
        using var host = await Host.CreateAsync(environment, handler);
        var updates = new List<MissumAiAssistantUpdate>();
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id,
            update => { updates.Add(update); return Task.CompletedTask; });
        await handler.LookupReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await host.Service.CancelCurrentAndWaitAsync(seeded.Session.Id);
        var stopped = await continuation;
        Assert.Equal(MessageStatus.Cancelled, stopped.Status);
        Assert.Equal(Partial, stopped.Content);
        Assert.Null(stopped.Error);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(updates, update => update.Kind is MissumAiAssistantUpdateKind.Started or MissumAiAssistantUpdateKind.Failed);
        Assert.Contains(updates, update => update.Kind == MissumAiAssistantUpdateKind.Cancelled);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
        Assert.Equal("run-old", (await environment.Get<IMissumAiRunRepository>().GetByAssistantMessageIdAsync(seeded.Message.Id))!.ServerRunId);
    }

    [Fact]
    public async Task GenuinePreparationFailureClearsWaitingStatusButPreservesResumableAnswer()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { LookupFailureStatus = HttpStatusCode.InternalServerError };
        using var host = await Host.CreateAsync(environment, handler);
        var updates = new List<MissumAiAssistantUpdate>();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id,
            update => { updates.Add(update); return Task.CompletedTask; }));
        Assert.Equal(MissumAiAssistantUpdateKind.Status, updates[0].Kind);
        var failure = Assert.Single(updates, update => update.Kind == MissumAiAssistantUpdateKind.Failed);
        Assert.Equal("Fortsetzen fehlgeschlagen", failure.Status);
        Assert.Equal(MessageStatus.Cancelled, failure.Message.Status);
        Assert.Equal(Partial, failure.Message.Content);
        Assert.NotNull(failure.Error);
        Assert.DoesNotContain(updates, update => update.Kind == MissumAiAssistantUpdateKind.Started);
        Assert.Empty(handler.Requests);
        Assert.False(host.Service.IsRunning);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Fact]
    public async Task GatewayStartup502WaitsThenContinuesWithExactlyOneCreateAndPreservedPrefix()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { InitialUnavailableHealthProbes = 1 };
        using var host = await Host.CreateAsync(environment, handler);
        var updates = new List<MissumAiAssistantUpdate>();
        var final = await host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id,
            update => { updates.Add(update); return Task.CompletedTask; });
        Assert.Equal(2, handler.HealthProbeCount);
        Assert.Single(handler.Requests);
        Assert.Single(updates, update => update.Kind == MissumAiAssistantUpdateKind.Started);
        Assert.DoesNotContain(updates, update => update.Kind == MissumAiAssistantUpdateKind.Failed);
        Assert.Equal("AI-Modell und Dienste werden vorbereitet", updates[0].Status);
        Assert.Equal(MessageStatus.Completed, final.Status);
        Assert.StartsWith(Partial + "\n\n", final.Content);
        Assert.Contains("Neue Fortsetzung.", final.Content);
        Assert.Equal(seeded.Message.Id, final.Id);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Fact]
    public async Task CancellingUncertainCreateClosesPreparationButRetainsFrozenRecoveryKey()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { BlockCreate = true };
        using var host = await Host.CreateAsync(environment, handler);
        var updates = new List<MissumAiAssistantUpdate>();
        var continuation = host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id,
            update => { updates.Add(update); return Task.CompletedTask; });
        await handler.CreateReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await host.Service.CancelCurrentAndWaitAsync(seeded.Session.Id);
        var stopped = await continuation;
        var preparation = Assert.Single(stopped.ToolSteps!, step => step.Tool == MissumAiAssistantService.ContinuationStepTool);
        Assert.Equal("cancelled", preparation.Status);
        Assert.NotNull(preparation.InputJson);
        Assert.StartsWith(Partial, stopped.Content);
        Assert.Equal(MessageStatus.Cancelled, stopped.Status);
        Assert.DoesNotContain(updates, update => update.Kind == MissumAiAssistantUpdateKind.Started);
        var pending = (await environment.Get<IMissumAiRunRepository>().GetByAssistantMessageIdAsync(seeded.Message.Id))!;
        Assert.Null(pending.ServerRunId);
        Assert.Equal("queued", pending.State);
        handler.ReleaseCreate.TrySetResult();
        var recovered = await host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);
        Assert.Equal(MessageStatus.Completed, recovered.Status);
        Assert.Equal(2, handler.RawRequests.Count);
        Assert.Equal(handler.RawRequests[0], handler.RawRequests[1]);
        Assert.Equal(handler.IdempotencyKeys[0], handler.IdempotencyKeys[1]);
        Assert.Equal(pending.IdempotencyKey, handler.IdempotencyKeys[1]);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Fact]
    public async Task LostCreateAcceptanceKeepsPartialAndRetriesExactlySameFrozenRequestAndKey()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { FailFirstCreate = true };
        using var host = await Host.CreateAsync(environment, handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => host.Service.ResumeMessageAsync(seeded.Session.Id,
            seeded.Message.Id, _ => Task.CompletedTask));
        var stopped = (await environment.Get<IChatRepository>().GetMessageAsync(seeded.Message.Id))!;
        Assert.Equal(MessageStatus.Cancelled, stopped.Status);
        Assert.StartsWith(Partial, stopped.Content);
        var pending = (await environment.Get<IMissumAiRunRepository>().GetByAssistantMessageIdAsync(seeded.Message.Id))!;
        Assert.Null(pending.ServerRunId);
        Assert.Equal("queued", pending.State);
        Assert.Equal(0, pending.LastEventId);
        Assert.Equal(MessageStatus.Completed, (await host.Service.ResumeMessageAsync(seeded.Session.Id,
            seeded.Message.Id, _ => Task.CompletedTask)).Status);
        Assert.Equal(2, handler.RawRequests.Count);
        Assert.Equal(handler.RawRequests[0], handler.RawRequests[1]);
        Assert.Equal(handler.IdempotencyKeys[0], handler.IdempotencyKeys[1]);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Fact]
    public async Task RecoveringOldScientificCreateKeepsItsFrozenToolCatalogAndIdempotencyKey()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled, ChatMode.ClaudeScience,
            PromptTriggerAction.WebSearch);
        var handler = new ContinuationHandler(RunState.Cancelled);
        using var host = await Host.CreateAsync(environment, handler, includeScientificRepository: true);
        var pending = await environment.Get<IMissumAiRunRepository>().BeginContinuationAttemptAsync(new(
            Guid.NewGuid(), seeded.Session.Id, seeded.Message.Id, PromptTriggerAction.WebSearch,
            "legacy-frozen-create", null, 0, "queued", "fixture/model", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await environment.Get<IChatRepository>().UpdateMessageAsync(seeded.Message.Id, Partial, MessageStatus.Interrupted);
        var frozen = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", Text: "Prüfe den gespeicherten fachlichen Stand.")])],
            SessionId: seeded.Session.Id.ToString("D"), ClientCapabilities: ["visual-tools"],
            AllowedServerTools: ["web.search", "web.fetch"], PreferredGeneralModelId: "fixture/model",
            DeepResearch: true, ResearchOptions: new(ProjectId: "research-" + seeded.Session.Id.ToString("N"),
                ProtocolVersion: 2));
        var frozenJson = JsonSerializer.Serialize(frozen, Json);
        await environment.Get<IChatRepository>().SaveToolStepAsync(seeded.Message.Id, new(
            "continuation:" + pending.IdempotencyKey, MissumAiAssistantService.ContinuationStepTool, "running",
            InputJson: frozenJson, OutputJson: JsonSerializer.Serialize(new
            {
                sessionId = seeded.Session.Id, messageId = seeded.Message.Id, localRunId = pending.Id,
                idempotencyKey = pending.IdempotencyKey, retainedPrefixLength = Partial.Length,
            }, Json)));

        var final = await host.Service.ResumeMessageAsync(seeded.Session.Id, seeded.Message.Id, _ => Task.CompletedTask);

        Assert.Equal(MessageStatus.Completed, final.Status);
        Assert.Equal(frozenJson, Assert.Single(handler.RawRequests));
        Assert.Equal(pending.IdempotencyKey, Assert.Single(handler.IdempotencyKeys));
        Assert.Equal(["web.search", "web.fetch"], Assert.Single(handler.Requests).AllowedServerTools);
        Assert.StartsWith(Partial, final.Content);
        Assert.Equal(2, (await environment.Get<IChatRepository>().ListMessagesAsync(seeded.Session.Id)).Count);
    }

    [Fact]
    public async Task LostCreateRecoveryKeepsItsOriginalChoiceEvenWhenTheNewComposerModelIsUnavailable()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var handler = new ContinuationHandler(RunState.Cancelled) { FailFirstCreate = true };
        using var host = await Host.CreateAsync(environment, handler);
        await host.Settings.UpdateAsync(current => current with
        {
            SelectedModel = "fixture/selected",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase) { ["text:fixture/selected"] = "xhigh" },
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => host.Service.ResumeMessageAsync(seeded.Session.Id,
            seeded.Message.Id, _ => Task.CompletedTask));
        await host.Settings.UpdateAsync(current => current with { SelectedModel = "not-installed" });
        var updates = new List<MissumAiAssistantUpdate>();

        Assert.Equal(MessageStatus.Completed, (await host.Service.ResumeMessageAsync(seeded.Session.Id,
            seeded.Message.Id, update => { updates.Add(update); return Task.CompletedTask; })).Status);
        Assert.Equal(2, handler.RawRequests.Count);
        Assert.Equal(handler.RawRequests[0], handler.RawRequests[1]);
        Assert.Equal(handler.IdempotencyKeys[0], handler.IdempotencyKeys[1]);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("fixture/selected", request.PreferredGeneralModelId);
            Assert.Equal("xhigh", request.ReasoningEffort);
        });
        Assert.Equal("fixture/selected", Assert.Single(updates, update => update.Kind == MissumAiAssistantUpdateKind.Started).Model);
        Assert.Equal("not-installed", host.Settings.Current.SelectedModel);
    }

    [Fact]
    public async Task ContinuationRepositoryPreservesContentAndToolReceiptsAtomically()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var seeded = await SeedAsync(environment, MessageStatus.Cancelled);
        var runs = environment.Get<IMissumAiRunRepository>();
        var old = (await runs.GetByAssistantMessageIdAsync(seeded.Message.Id))!;
        var continued = await runs.BeginContinuationAttemptAsync(old with { Id = Guid.NewGuid(), IdempotencyKey = "new-safe-key", ServerRunId = null, State = "queued" });
        Assert.Equal(old.Id, continued.Id);
        var message = (await environment.Get<IChatRepository>().GetMessageAsync(seeded.Message.Id))!;
        Assert.Equal(Partial, message.Content);
        Assert.Equal(MessageStatus.Streaming, message.Status);
        Assert.Contains(message.ToolSteps!, step => step.Id == "original-read");
    }

    [Fact]
    public void LatestStoppedAnchorRequiredAndOnlyRealFailedContentIsEligible()
    {
        var session = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var user = new ChatMessage(Guid.NewGuid(), session, ChatRole.User, "Originalauftrag", MessageStatus.Completed, now, now);
        var empty = new ChatMessage(Guid.NewGuid(), session, ChatRole.Assistant, "", MessageStatus.Cancelled, now, now);
        Assert.Equal(empty, MissumAiAssistantService.ValidateContinuationAnchor(session, empty.Id, [user, empty]).Assistant);
        Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.ValidateContinuationAnchor(Guid.NewGuid(), empty.Id, [user, empty]));
        Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.ValidateContinuationAnchor(session, empty.Id,
            [user, empty, user with { Id = Guid.NewGuid() }]));
        var failed = empty with { Status = MessageStatus.Failed, Content = "Provider ausgefallen", Error = "Provider ausgefallen" };
        Assert.False(MissumAiAssistantService.HasGenuineContinuationContent(failed));
        Assert.False(MissumAiAssistantService.HasGenuineContinuationContent(failed with { Content = "Der Missum-AI-Auftrag konnte nicht abgeschlossen werden.", ToolSteps = [new("progress", "assistant.progress", "failed")] }));
        Assert.True(MissumAiAssistantService.HasGenuineContinuationContent(failed with { Content = Partial }));
        Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.ValidateContinuationAnchor(session, empty.Id, [user, failed]));
    }

    [Fact]
    public void RevisionsAndSteeringUseRunRelativeOffsetsAndCannotErasePreviousAnswer()
    {
        const string prefix = "Bisherige Antwort.\n\n";
        Assert.Equal(prefix + "Neu", MissumAiAssistantService.ApplyContinuationTextDelta(prefix + "Alter Versuch", new("Neu", ReplaceFrom: 0), prefix));
        Assert.Equal(prefix + "abXYZ", MissumAiAssistantService.ApplyContinuationTextDelta(prefix + "abcdef", new("XYZ", ReplaceFrom: 2), prefix));
        Assert.Equal(prefix + "Neu", MissumAiAssistantService.ApplyContinuationTextDelta("Bisherige Antwort.", new("Neu", ReplaceFrom: 0), prefix));
        Assert.Equal(prefix.Length + 3, MissumAiAssistantService.ShiftContinuationOffset(3, prefix.Length));
        Assert.Throws<InvalidDataException>(() => MissumAiAssistantService.ApplyContinuationTextDelta(prefix + "x", new("y", ReplaceFrom: 5), prefix));
        Assert.NotEqual(MissumAiAssistantService.ContinuationFallbackToolStepId("run-first", 1),
            MissumAiAssistantService.ContinuationFallbackToolStepId("run-second", 1));
    }

    [Fact]
    public void ContinuationPrefixReceiptChecksOwnerAndReconstructsMetadataSanitizedGap()
    {
        var session = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), session, ChatRole.Assistant, Partial, MessageStatus.Interrupted, now, now);
        var run = new MissumAiRunRecord(Guid.NewGuid(), session, message.Id, null, "key", "run-new", 3, "running", null, null, now, now);
        var receipt = new AssistantToolStep("continuation:key", MissumAiAssistantService.ContinuationStepTool, "completed",
            OutputJson: JsonSerializer.Serialize(new { sessionId = session, messageId = message.Id, localRunId = run.Id,
                serverRunId = run.ServerRunId, idempotencyKey = run.IdempotencyKey, retainedPrefixLength = Partial.Length }, Json));
        Assert.Equal(Partial + "\n\n", MissumAiAssistantService.RetainedContinuationPrefix(run, message with { ToolSteps = [receipt] }));
        Assert.Throws<InvalidDataException>(() => MissumAiAssistantService.RetainedContinuationPrefix(run with { SessionId = Guid.NewGuid() },
            message with { ToolSteps = [receipt] }));
    }

    private static async Task<(ChatSession Session, ChatMessage Message)> SeedAsync(TestEnvironment environment, MessageStatus status,
        ChatMode mode = ChatMode.General, PromptTriggerAction? action = null)
    {
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Unveränderter Projekttitel", mode);
        string? workspace = null;
        if (mode == ChatMode.Coding)
        {
            workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "original-project")).FullName;
            await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "original source\n");
            await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: true);
        }
        var turn = await chats.AddTurnAsync(session.Id, "Leite die Energiegleichung vollständig her.");
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, Partial, status);
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, new("original-read", "coding.read", "completed", "Herkunftsdatei gelesen"));
        var now = DateTimeOffset.UtcNow;
        var run = await environment.Get<IMissumAiRunRepository>().CreateAsync(new(Guid.NewGuid(), session.Id, turn.AssistantMessage.Id,
            action ?? (mode == ChatMode.Coding ? PromptTriggerAction.Coding : null), "old-key", "run-old", 8,
            status == MessageStatus.Cancelled ? "cancelled" : "running", "fixture/model", null, now, now, WorkspacePath: workspace));
        if (workspace is not null)
        {
            await using var monitor = new CodingChangesMonitor(workspace,
                CodingChangesMonitor.StorageDirectory(environment.Directory, session.Id, turn.AssistantMessage.Id),
                session.Id, turn.AssistantMessage.Id, run.Id, _ => Task.CompletedTask, failure => throw failure);
            await monitor.StartAsync(resume: false, CancellationToken.None);
        }
        return ((await chats.GetSessionAsync(session.Id))!, (await chats.GetMessageAsync(turn.AssistantMessage.Id))!);
    }

    private sealed class Host(SettingsCoordinator settings, MissumAiConnectionService connection, MicrophoneTranscriptionService microphone,
        MissumAiAssistantService service) : IDisposable
    {
        internal MissumAiAssistantService Service => service;
        internal SettingsCoordinator Settings => settings;
        internal static async Task<Host> CreateAsync(TestEnvironment environment, ContinuationHandler handler,
            bool includeScientificRepository = false)
        {
            await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { MissumAiServerUrl = "http://127.0.0.1:65000", SelectedModel = "fixture/model", IsAutomaticSpeechEnabled = false });
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance, () => handler);
            var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
            var chats = environment.Get<IChatRepository>();
            var documents = environment.Get<IDocumentIngestor>();
            var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
                environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(), environment.Get<IBinaryObjectStore>(), documents,
                new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats), new LocalToolBroker(connection, documents, null!, chats),
                null!, microphone, settings, new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance),
                NullLogger<MissumAiAssistantService>.Instance,
                scientificResearch: includeScientificRepository ? environment.Get<IScientificResearchRepository>() : null);
            return new(settings, connection, microphone, service);
        }
        public void Dispose() { service.Dispose(); microphone.Dispose(); connection.Dispose(); settings.Dispose(); }
    }

    private sealed class ContinuationHandler(RunState oldState) : HttpMessageHandler
    {
        internal List<RunRequest> Requests { get; } = [];
        internal List<string> RawRequests { get; } = [];
        internal List<string> IdempotencyKeys { get; } = [];
        internal List<RunModelSelectionRequest> ModelSelections { get; } = [];
        internal RunMode OriginalRunMode { get; init; } = RunMode.General;
        internal bool OriginalModelSupportsReasoning { get; init; } = true;
        internal string? SelectedReasoningEffort { get; private set; } = "xhigh";
        internal bool FailFirstCreate { get; init; }
        internal bool BlockCreate { get; init; }
        internal bool BlockLookup { get; init; }
        internal HttpStatusCode? LookupFailureStatus { get; init; }
        internal int InitialUnavailableHealthProbes { get; init; }
        internal int HealthProbeCount { get; private set; }
        internal TaskCompletionSource LookupReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CreateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? LastEventCursor { get; private set; }
        internal string? LastCreatedRun { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            if (path == "/v1/health/live")
            {
                HealthProbeCount++;
                if (HealthProbeCount <= InitialUnavailableHealthProbes)
                    return new(HttpStatusCode.BadGateway) { Content = new StringContent("Gateway is still starting.") };
                return Response(new HealthSnapshot("live", MissumAiProtocol.Version, now));
            }
            if (path == "/v1/models/status") return Response(new ModelStatusSnapshot(true, "fixture",
                [new("fixture/model", "general", true, true, "loaded", 131_072,
                        ReasoningEfforts: OriginalModelSupportsReasoning ? ["low", "high", "xhigh"] : [],
                        DefaultReasoningEffort: OriginalModelSupportsReasoning ? "high" : null),
                    new("fixture/selected", "general", true, false, "unloaded", 131_072,
                        ReasoningEfforts: ["low", "high", "xhigh"], DefaultReasoningEffort: "high"),
                    new("fixture/selected", "coding", true, false, "unloaded", 131_072,
                        ReasoningEfforts: ["medium", "xhigh"], DefaultReasoningEffort: "medium"),
                    new("fixture/later", "general", true, false, "unloaded", 131_072)], now));
            if (path == "/v1/models/coding") return Response(new CodingModelCatalogResponse(
                [new("fixture/selected", "coding", true, false, "unloaded", 131_072,
                    ReasoningEfforts: ["medium", "xhigh"], DefaultReasoningEffort: "medium")], "fixture", true, null, now));
            if (path == "/v1/capabilities") return Response(new CapabilitySnapshot(MissumAiProtocol.Version, "fixture", [],
                ["web.search", "web.fetch"], [], new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize,
                SupportsCodingSessionContext: true));
            if (path == "/v1/runs/run-old")
            {
                LookupReached.TrySetResult();
                if (BlockLookup) await ReleaseLookup.Task.WaitAsync(token);
                if (LookupFailureStatus is { } failure)
                    return new(failure) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                return Response(new RunSnapshot("run-old", oldState, OriginalRunMode, "fixture/model", "Ignorierter AI-Titel", 10, now, now));
            }
            if (path == "/v1/runs" && request.Method == HttpMethod.Post)
            {
                var raw = await request.Content!.ReadAsStringAsync(token);
                RawRequests.Add(raw);
                Requests.Add(JsonSerializer.Deserialize<RunRequest>(raw, Json)!);
                IdempotencyKeys.Add(request.Headers.GetValues(MissumAiHeaders.IdempotencyKey).Single());
                CreateReached.TrySetResult();
                if (BlockCreate) await ReleaseCreate.Task.WaitAsync(token);
                if (FailFirstCreate && Requests.Count == 1) throw new HttpRequestException("Acceptance response lost.");
                LastCreatedRun = "run-new";
                return Response(new RunAccepted("run-new", RunState.Running, now, "/v1/runs/run-new/events"));
            }
            if (path == "/v1/runs/run-old/selection" && request.Method == HttpMethod.Post)
            {
                var selection = JsonSerializer.Deserialize<RunModelSelectionRequest>(await request.Content!.ReadAsStringAsync(token), Json)!;
                ModelSelections.Add(selection);
                SelectedReasoningEffort = selection.ReasoningEffort;
                return Response(new RunEvent(9, "run-old", RunModelSelectionEvents.Requested, now,
                    JsonSerializer.SerializeToElement(selection, Json)));
            }
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                var runId = path.Split('/')[3];
                LastEventCursor = request.Headers.TryGetValues(MissumAiHeaders.LastEventId, out var values) ? values.Single() : null;
                var delta = runId == "run-old" ? Partial + "\n\nNeue Fortsetzung." : "Neue Fortsetzung.";
                var completedModel = runId == "run-old" ? ModelSelections.LastOrDefault()?.ModelId ?? "fixture/model"
                    : Requests.Last().PreferredGeneralModelId ?? Requests.Last().PreferredCodingModelId ?? "fixture/model";
                var items = new[]
                {
                    new RunEvent(9, runId, RunEventTypes.TextDelta, now, JsonSerializer.SerializeToElement(new TextDeltaEvent(delta, ReplaceFrom: 0), Json)),
                    new RunEvent(10, runId, RunEventTypes.RunCompleted, now, JsonSerializer.SerializeToElement(new RunCompletedEvent("Ignorierter AI-Titel", completedModel, 10, 10, []), Json)),
                };
                return new(HttpStatusCode.OK) { Content = new StringContent(string.Join("", items.Select(item => "data: " + JsonSerializer.Serialize(item, Json) + "\n\n")), Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException("Unexpected continuation test request: " + path);
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    }
}
