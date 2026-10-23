using Missum.Ai.Contracts;
using Missum.Core.Contracts;
using Missum.Core.Chat;
using Missum.Core.Coding;
using Missum.Core.Extensions;
using Missum.App.Services.Extensions;
using Missum.Core.Memory;
using Missum.Core.Models;
using System.Text.Json;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator(
    IChatRepository chats,
    IDocumentIngestor documents,
    IContextAssembler contextAssembler,
    IPromptTriggerRepository promptTriggers,
    IAssistantAttachmentRepository attachments,
    IChatArtifactRepository artifacts,
    IConversationSnapshotRepository conversationSnapshots,
    MissumAiAssistantService? missumAi,
    SettingsCoordinator settings,
    RecentActivityService recentActivity,
    MicrophoneTranscriptionService? microphone = null,
    AssistantRuntimeProfile? runtimeProfile = null,
    IExtensionActionCatalog? extensionActions = null,
    IExtensionRuntimeService? extensionRuntime = null,
    IProjectMemoryStore? projectMemory = null,
    IAssistantRunScheduler? runScheduler = null,
    NativeSessionCacheCleanupService? cacheCleanup = null,
    ScientificPublicationService? publicationService = null,
    ScientificPresentationCoordinator? presentation = null)
{
    private AppSettings CurrentSettings => AssistantClientExecutionScope.Resolve(settings.Current);
    private Task UpdateViewSettingsAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) =>
        AssistantClientExecutionScope.UpdateAsync(settings, update, cancellationToken);

    private const string DefaultSessionTitle = "Neue Sitzung";
    private sealed record QueuedChatInput(ChatSession Session, PromptTriggerMatch? Match);
    private const string DefaultSystemPrompt = "Du bist ein allgemeiner lokaler AI-Assistent. Unterstütze die konkrete Aufgabe des Nutzers, etwa beim Programmieren, Schreiben, Lernen, Analysieren oder Planen. Passe Sprache, Detailtiefe und Vorgehen an die Frage an. Unterscheide belegte Informationen von Annahmen, benenne relevante Unsicherheiten und erfinde keine Fakten, Quellen oder Ergebnisse.";
    internal static string? DerivePromptSessionTitle(string prompt)
    {
        var value = string.Join(' ', (prompt ?? string.Empty)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '#', '`');
        const int maximumLength = 160;
        if (value.Length > maximumLength)
        {
            var end = value.LastIndexOf(' ', maximumLength);
            if (end <= 0) end = maximumLength;
            if (char.IsHighSurrogate(value[end - 1])) end--;
            value = value[..end].TrimEnd();
        }
        return value.Length == 0 ? null : value;
    }
    private int _startupRunsHandled;

    private async Task<ChatSession> SaveSubmittedPromptTitleAsync(ChatSession session, string prompt,
        Func<string, object, string?, Task> emit, string requestId, CancellationToken cancellationToken)
    {
        var title = DerivePromptSessionTitle(prompt);
        if (title is null || string.Equals(session.Title, title, StringComparison.Ordinal)) return session;
        await chats.RenameSessionAsync(session.Id, title, cancellationToken).ConfigureAwait(false);
        // Refresh only shared sidebar/tab metadata. A title change must not
        // reload a long conversation or disturb a concurrent client's draft.
        await emit("session.grouped", await BuildSessionSidebarSnapshotAsync(false, cancellationToken).ConfigureAwait(false),
            requestId).ConfigureAwait(false);
        return session with { Title = title };
    }
    private Task? _resumeTask;
    private readonly object _resumeLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AssistantDisplayState> _displayStates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _scheduledRunObservers = new();
    private readonly IExtensionActionCatalog _extensionActions = extensionActions ?? ExtensionActionCatalog.CreateWithBuiltIns();
    private readonly IAssistantRunScheduler? _runScheduler = runScheduler;
    private readonly NativeSessionCacheCleanupService? _cacheCleanup = cacheCleanup
        ?? (runtimeProfile is null ? null : new(runtimeProfile));

    internal sealed record AssistantDisplayState(Guid MessageId, string? ModelSelection, bool IsCoding,
        bool IsRunning, string? Status = null, string? Detail = null, string? Model = null,
        int? ContextUsed = null, int? ContextLimit = null, int? LoadedFiles = null, bool ContextWasCompacted = false,
        string? GenerationState = null, int? GeneratedTokens = null, DateTimeOffset? GenerationUpdatedAt = null,
        string? ContextSource = null, int? ProcessedPromptTokens = null, int? TotalPromptTokens = null,
        double? PromptProgress = null);

    private async Task ObserveDisplayStateAsync(MissumAiAssistantUpdate update)
    {
        if (update.Kind is not (MissumAiAssistantUpdateKind.Started or MissumAiAssistantUpdateKind.Status
            or MissumAiAssistantUpdateKind.Completed or MissumAiAssistantUpdateKind.Cancelled or MissumAiAssistantUpdateKind.Failed)) return;
        var isCoding = _displayStates.TryGetValue(update.Message.SessionId, out var previous)
            && previous.MessageId == update.Message.Id ? previous.IsCoding
                : IsCodingAgentSession((await chats.GetSessionAsync(update.Message.SessionId, CancellationToken.None).ConfigureAwait(false))?.ChatMode);
        var selection = CurrentSettings.SelectedModel;
        _displayStates.AddOrUpdate(update.Message.SessionId,
            _ => Merge(new(update.Message.Id, selection, isCoding, true)),
            (_, current) => Merge(current.MessageId == update.Message.Id ? current : new(update.Message.Id, selection, isCoding, true)));

        AssistantDisplayState Merge(AssistantDisplayState current)
        {
            var reasoningOnly = update.ToolStep?.Tool == "assistant.reasoning";
            var resumed = update.Kind == MissumAiAssistantUpdateKind.Started && current.IsRunning && current.Status is not null
                && update.ToolStep?.Tool != MissumAiAssistantService.ContinuationStepTool;
            // Staged Science streams internal text fragments through model
            // progress too. They must not replace the last real token pulse.
            var preserveGeneration = reasoningOnly || resumed || update.GenerationState is "reasoningDelta" or "contentDelta";
            return current with
            {
                IsRunning = update.Kind is not (MissumAiAssistantUpdateKind.Completed or MissumAiAssistantUpdateKind.Cancelled or MissumAiAssistantUpdateKind.Failed),
                Status = reasoningOnly || resumed ? current.Status : update.Status ?? current.Status,
                Detail = reasoningOnly || resumed ? current.Detail : update.Detail ?? current.Detail,
                Model = reasoningOnly ? current.Model : update.Model ?? current.Model,
                ContextUsed = update.ContextUsed ?? current.ContextUsed,
                ContextSource = update.ContextUsed.HasValue ? update.ContextSource ?? "estimated" : current.ContextSource,
                ContextLimit = update.ContextLimit ?? current.ContextLimit,
                LoadedFiles = update.LoadedFiles ?? current.LoadedFiles,
                ContextWasCompacted = update.ContextUsed.HasValue ? update.ContextWasCompacted : current.ContextWasCompacted,
                GenerationState = preserveGeneration ? current.GenerationState : update.GenerationState,
                GeneratedTokens = preserveGeneration ? current.GeneratedTokens : update.GeneratedTokens,
                GenerationUpdatedAt = preserveGeneration ? current.GenerationUpdatedAt : update.GenerationUpdatedAt,
                ProcessedPromptTokens = preserveGeneration ? current.ProcessedPromptTokens
                    : update.ProcessedPromptTokens ?? (update.GenerationState == current.GenerationState ? current.ProcessedPromptTokens : null),
                TotalPromptTokens = preserveGeneration ? current.TotalPromptTokens
                    : update.TotalPromptTokens ?? (update.GenerationState == current.GenerationState ? current.TotalPromptTokens : null),
                PromptProgress = preserveGeneration ? current.PromptProgress
                    : update.PromptProgress ?? (update.GenerationState == current.GenerationState
                        && update.ProcessedPromptTokens is not >= 0 && update.TotalPromptTokens is not >= 0 ? current.PromptProgress : null),
            };
        }
    }

    public async Task SaveDraftAsync(Guid sessionId, string draft, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Length, 100_000);
        await AssistantClientExecutionScope.SaveDraftAsync(sessionId, draft, settings.Current, cancellationToken).ConfigureAwait(false);
        if (!AssistantClientExecutionScope.IsFrozen)
            await chats.SaveDraftAsync(sessionId, draft, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCodingWorkspacePathAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        EnsureContextCanChange(sessionId);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Der Workspace-Ordner existiert nicht.");
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die ausgewählte Sitzung wurde nicht gefunden.");
        await CodingWorkspaceGit.EnsureRepositoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await ProjectRepositoryScaffold.EnsureAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        // A workspace is session context in every chat mode, not a tool selection.
        await chats.SetCodingWorkspacePathAsync(session.Id, fullPath, activateCoding: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasAudiobookVoiceContextAsync(CancellationToken cancellationToken = default)
    {
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(
                session.PersistentExtensionActionId,
                BuiltInActionIds.CreateAudiobook,
                StringComparison.Ordinal)
            || await HasAudiobookContentAsync(session.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromptTriggerAction?> GetRequiredMediaCaptureAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var prompt = GetRequiredString(payload, "prompt", 100_000);
        var sessionId = GetOptionalGuid(payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        var explicitExtensionActionId = GetExplicitExtensionActionId(payload);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitExtensionActionId,
            cancellationToken).ConfigureAwait(false);
        var action = match?.Trigger.Action;
        if (action is not (PromptTriggerAction.AudioAnalysis
            or PromptTriggerAction.VideoAnalysis
            or PromptTriggerAction.ImageAnalysis))
        {
            return null;
        }

        var hasDocuments = (await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false)).Count > 0;
        var sessionAttachments = await attachments.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return HasMediaAnalysisContext(action.Value, hasDocuments, sessionAttachments)
            ? null
            : action;
    }

    public async Task<bool> IsSpeechRequestAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var prompt = GetOptionalString(payload, "prompt", 100_000) ?? string.Empty;
        var sessionId = GetOptionalGuid(payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        var explicitExtensionActionId = GetExplicitExtensionActionId(payload);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitExtensionActionId,
            cancellationToken).ConfigureAwait(false);
        return match?.Trigger.Action == PromptTriggerAction.TextToSpeech;
    }

    internal static bool HasMediaAnalysisContext(
        PromptTriggerAction action,
        bool hasDocuments,
        IReadOnlyList<AssistantAttachment> sessionAttachments) =>
        hasDocuments || sessionAttachments.Any(item => action switch
        {
            PromptTriggerAction.ImageAnalysis => item.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            PromptTriggerAction.VideoAnalysis => item.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
            PromptTriggerAction.AudioAnalysis => item.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
            _ => false,
        });

    internal static string MediaActionName(PromptTriggerAction action) => action switch
    {
        PromptTriggerAction.AudioAnalysis => "audioAnalysis",
        PromptTriggerAction.VideoAnalysis => "videoAnalysis",
        PromptTriggerAction.ImageAnalysis => "imageAnalysis",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public async Task CancelCurrentAsync(Guid? sessionId = null)
    {
        var snapshot = _runScheduler?.Snapshot;
        // Continuations and reattached SSE readers run outside the queue. Capture
        // their cancellation before cancelling a ticket, otherwise the linked
        // scheduler token can detach the reader and clear its server run ID first.
        var cancelActive = missumAi is not null
            && (!sessionId.HasValue || missumAi.ActiveSessionId == sessionId.Value)
                ? missumAi.CancelCurrentAndWaitAsync(sessionId, CancellationToken.None)
                : Task.CompletedTask;
        if (_runScheduler is not null && snapshot is not null)
        {
            if (snapshot.Active is { } active
                && (!sessionId.HasValue || active.SessionId == sessionId.Value))
            {
                _runScheduler.TryCancel(active.TicketId);
            }
            else
            {
                var queued = snapshot.Pending.FirstOrDefault(item => !sessionId.HasValue || item.SessionId == sessionId.Value);
                if (queued is not null) _runScheduler.TryCancel(queued.TicketId);
            }
        }

        await cancelActive.ConfigureAwait(false);
    }

    public async Task AddLiveCaptionResultAsync(
        string? transcript,
        string? error,
        CancellationToken cancellationToken = default)
    {
        var normalizedTranscript = FormatLiveCaptionText(transcript);
        var normalizedError = error?.Trim() ?? string.Empty;
        var title = normalizedError.Length == 0 ? "Live-Untertitel" : "Live-Untertitel fehlgeschlagen";
        var details = normalizedTranscript.Length > 0
            ? normalizedTranscript
            : normalizedError.Length > 0
                ? normalizedError
                : "Es wurde kein Sprachinhalt erkannt.";
        if (normalizedError.Length > 0 && normalizedTranscript.Length > 0)
        {
            details += $"\n\n**Fehler:** {normalizedError}";
        }
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            $"**{title}**\n\n{details}",
            MessageStatus.Completed,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"Live-Untertitel in AI-Sitzung „{session.Title}“ gespeichert",
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string FormatLiveCaptionText(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        // Markdown paragraphs preserve the speaker/segment boundaries in the
        // rendered chat. A single newline inside a paragraph would otherwise
        // be collapsed by HTML whitespace handling.
        var lines = normalized
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n\n", lines);
    }

    public Task<object> BuildSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => BuildSnapshotCoreAsync(cancellationToken), cancellationToken);

    private async Task<object> BuildSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextAssembler); // Retain the constructor contract; UI never assembles model history.
        var activeSession = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        var page = await conversationSnapshots.GetPageAsync(activeSession.Id, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die aktive Chat-Sitzung wurde nicht gefunden.");
        var conversation = page.Conversation;
        var auxiliary = await PrepareDisplayedPageAsync(page, cancellationToken).ConfigureAwait(false);
        var session = conversation.Session with { Draft = AssistantClientExecutionScope.Draft(conversation.Session.Id, conversation.Session.Draft, settings.Current) };
        var selectedChatMode = session.ChatMode;
        var sessions = await chats.ListSessionsAsync(selectedChatMode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var messages = conversation.Messages;
        var artifactItems = conversation.Artifacts;
        var documentItems = await documents.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var attachmentItems = await attachments.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var sessionGroups = await chats.ListSessionGroupsAsync(selectedChatMode, cancellationToken).ConfigureAwait(false);
        var extensionWorkspaceRoot = ResolveSessionWorkspaceRoot(session, sessionGroups);
        var documentGroupStatus = BuildDocumentGroupStatus(documentItems, attachmentItems.Count);
        // A snapshot is local UI state. Never make sidebar/session interaction wait for
        // the native model runtime, which may be offline or loading a model.
        var isCodingSession = IsCodingAgentSession(session.ChatMode);
        var selectedModel = isCodingSession ? CurrentSettings.SelectedModel : CurrentSettings.SelectedModel;
        var contextLimit = ModelContextSelection.Resolve(settings.Current, selectedModel ?? "", ModelContextProfiles.ResolveMaximum(selectedModel, isCodingSession ? "coding" : "general"));
        // Opening a view must never assemble, truncate or warm the model context.
        // Only real generation metrics describe the server's complete context.
        var context = new ContextBuildResult([], 0, false, "Der Kontextverbrauch wird beim AI-Lauf ermittelt.");
        _displayStates.TryGetValue(session.Id, out var display);
        if (display is not null && (display.IsCoding != isCodingSession || display.ModelSelection != selectedModel
            && !(missumAi?.IsRunning == true && missumAi.ActiveSessionId == session.Id))) display = null;
        var displayMessage = display is null ? null : messages.FirstOrDefault(message => message.Id == display.MessageId);
        var isSessionRunning =
            missumAi?.IsRunning == true && missumAi.ActiveSessionId == session.Id
            || display?.IsRunning == true && displayMessage?.Status is MessageStatus.Pending or MessageStatus.Streaming;
        var runQueue = _runScheduler?.Snapshot;
        return new
        {
            sessionGroups = sessionGroups.Select(group => new
            {
                group.Id,
                group.Name,
                group.IsCollapsed,
                group.CreatedAt,
                group.WorkspacePath,
                chatMode = ChatModeName(group.ChatMode),
                sessionIds = sessions.Where(item => item.SessionGroupId == group.Id).Select(item => item.Id),
            }),
            sessions = sessions.Select(ToSessionDto),
            messages = messages.Select(message => ToMessageDto(
                message,
                artifactItems.TryGetValue(message.Id, out var messageArtifacts) ? messageArtifacts : null,
                page.ToolStepWindows?.GetValueOrDefault(message.Id))),
            subagents = ToSubagentDtos(ReadActivitySummaries(auxiliary)),
            auxiliaryMessages = auxiliary.Select(message => new { id = message.Id, toolSteps = message.ToolSteps }),
            historyPage = new { page.HasMore, page.Before, page.TotalMessages },
            toolStepWindows = page.ToolStepWindows,
            workingPlan = await BuildWorkingPlanAsync(session.Id, cancellationToken).ConfigureAwait(false),
            conversationRevision = session.ConversationRevision,
            documents = documentItems.Select(ToDocumentDto),
            attachments = attachmentItems.Select(ToAttachmentDto),
            actionDescriptors = BuildActionDescriptors(selectedChatMode, page.TotalMessages, extensionWorkspaceRoot),
            documentGroupStatus,
            activeSessionId = session.Id,
            productName = runtimeProfile?.ProductName ?? "AI Assistent",
            assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent",
            productIdentity = new { assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent" },
            bridgeContract = AssistantProtocol.BuildProtocolContract(),
            publicationCapabilities = new { available = publicationService is not null },
            chatMode = ChatModeName(selectedChatMode),
            selectedChatMode = ChatModeName(selectedChatMode),
            draft = session.Draft,
            isRunning = isSessionRunning,
            isAiBusy = missumAi?.IsRunning == true
                || _displayStates.Values.Any(item => item.IsRunning)
                || runQueue?.IsIdle == false,
            activeRunSessionId = missumAi?.ActiveSessionId ?? runQueue?.Active?.SessionId,
            activeRunId = missumAi?.ActiveRunId,
            runQueue = runQueue is null ? null : ToRunQueueDto(runQueue),
            runMessageId = isSessionRunning ? display?.MessageId : null,
            runStatus = isSessionRunning ? display?.Status ?? "Denkt nach" : null,
            runDetail = isSessionRunning ? display?.Detail : null,
            generationState = isSessionRunning ? display?.GenerationState : null,
            generatedTokens = isSessionRunning ? display?.GeneratedTokens : null,
            generationUpdatedAt = isSessionRunning ? display?.GenerationUpdatedAt : null,
            processedPromptTokens = isSessionRunning ? display?.ProcessedPromptTokens : null,
            totalPromptTokens = isSessionRunning ? display?.TotalPromptTokens : null,
            promptProgress = isSessionRunning ? display?.PromptProgress : null,
            loadedFiles = display?.LoadedFiles,
            model = display?.Model ?? (isCodingSession ? selectedModel ?? "Lokaler AI-Server" : "Lokaler AI-Server"),
            provider = CurrentSettings.AiProvider.ToString(),
            contextUsed = display?.ContextUsed ?? context.EstimatedTokens,
            contextLimit = display?.ContextLimit ?? contextLimit,
            contextWasTruncated = display?.ContextUsed is not null ? display.ContextWasCompacted : context.WasTruncated,
            contextNotice = display?.ContextUsed is not null ? null : context.TruncationNotice,
            contextSource = display?.ContextUsed is not null ? display.ContextSource ?? "estimated" : "estimated",
            contextMessageId = display?.ContextUsed is not null ? display.MessageId : (Guid?)null,
            selectedExtensionActionId = session.PersistentExtensionActionId,
            selectedToolAction = LegacyToolAliasFor(session.PersistentExtensionActionId),
            reasoningModelId = selectedModel,
            reasoningRole = isCodingSession ? "coding" : "general",
            workspacePath = session.CodingWorkspacePath,
            // Read alias for WebViews from the transition build.
            codingWorkspacePath = session.CodingWorkspacePath,
            codingWorkspaceRequired = isCodingSession
                && (string.IsNullOrWhiteSpace(session.CodingWorkspacePath)
                    || !Directory.Exists(session.CodingWorkspacePath)),
            changesSummary = missumAi is null ? null : await missumAi.GetChangesSummaryAsync(session.Id, messages, cancellationToken).ConfigureAwait(false),
            isSessionPaneOpen = CurrentSettings.IsAssistantSessionPaneOpen,
        };
    }

    internal object BuildRunQueueSnapshot() => _runScheduler is null
        ? new { active = (object?)null, pending = Array.Empty<object>(), queueDepth = 0, isIdle = true }
        : ToRunQueueDto(_runScheduler.Snapshot);

    private object[] BuildActionDescriptors(ChatMode chatMode, int messageCount, string? workspaceRoot)
    {
        var extensionMode = chatMode switch
        {
            ChatMode.General => ExtensionChatMode.General,
            ChatMode.Coding => ExtensionChatMode.Coding,
            ChatMode.ClaudeScience => ExtensionChatMode.ClaudeScience,
            _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
        };
        return _extensionActions.GetActions(extensionMode)
            .Select(action =>
        {
            var disabledReason = action.DisabledReason;
            if (string.Equals(action.ActionId, BuiltInActionIds.ExportChatPdf, StringComparison.Ordinal)
                && messageCount == 0)
                disabledReason = "Der Chat enthält noch keine Nachrichten.";
            if (!action.ActionId.StartsWith("builtin.", StringComparison.Ordinal))
                disabledReason ??= extensionRuntime is null
                    ? "Der ExtensionHost ist für diese Aktion nicht verfügbar."
                    : extensionRuntime.GetDisabledReason(action.ActionId, workspaceRoot);
            var groupLabel = BuiltInExtensionCatalog.GroupLabels.TryGetValue(action.GroupId, out var label)
                ? label
                : "Erweiterungen";
            return (object)new
            {
                action.ActionId,
                action.DisplayName,
                action.Description,
                action.IconKey,
                action.GroupId,
                groupLabel,
                action.GroupOrder,
                action.ItemOrder,
                supportedChatModes = action.SupportedChatModes.Select(static mode => mode switch
                {
                    ExtensionChatMode.General => "general",
                    ExtensionChatMode.Coding => "coding",
                    ExtensionChatMode.ClaudeScience => "claudescience",
                    _ => throw new InvalidOperationException("Der Extension-Chatmodus ist ungültig."),
                }),
                actionKind = action.ActionKind switch
                {
                    ExtensionActionKind.Immediate => "immediate",
                    ExtensionActionKind.SelectableTool => "selectableTool",
                    _ => throw new InvalidOperationException("Die Extension-Aktionsart ist ungültig."),
                },
                selectionBehavior = action.SelectionBehavior switch
                {
                    ExtensionSelectionBehavior.None => "none",
                    ExtensionSelectionBehavior.Toggle => "toggle",
                    _ => throw new InvalidOperationException("Das Extension-Auswahlverhalten ist ungültig."),
                },
                disabledReason,
            };
        }).ToArray();
    }

    internal Task<object> BuildConversationForHostAsync(Guid sessionId, CancellationToken cancellationToken) => BuildConversationSnapshotAsync(sessionId, cancellationToken);

    private async Task<object> BuildConversationSnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var page = await Task.Run(() => conversationSnapshots.GetPageAsync(sessionId, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Chat-Sitzung wurde nicht gefunden.");
        var conversation = page.Conversation;
        var auxiliary = await PrepareDisplayedPageAsync(page, cancellationToken).ConfigureAwait(false);
        return new
        {
            activeSessionId = sessionId,
            subagents = ToSubagentDtos(ReadActivitySummaries(auxiliary)),
            auxiliaryMessages = auxiliary.Select(message => new { id = message.Id, toolSteps = message.ToolSteps }),
            historyPage = new { page.HasMore, page.Before, page.TotalMessages },
            toolStepWindows = page.ToolStepWindows,
            workingPlan = await BuildWorkingPlanAsync(sessionId, cancellationToken).ConfigureAwait(false),
            chatMode = ChatModeName(conversation.Session.ChatMode),
            conversationRevision = conversation.Session.ConversationRevision,
            changesSummary = missumAi is null ? null : await missumAi.GetChangesSummaryAsync(sessionId, conversation.Messages, cancellationToken).ConfigureAwait(false),
            messages = conversation.Messages.Select(message => ToMessageDto(
                message,
                conversation.Artifacts.TryGetValue(message.Id, out var messageArtifacts) ? messageArtifacts : null,
                page.ToolStepWindows?.GetValueOrDefault(message.Id))),
        };
    }

    private async Task EmitCommittedMessageAsync(
        Guid messageId,
        Func<string, object, string?, Task> emit,
        string requestId,
        IReadOnlyList<ChatArtifact>? knownArtifacts = null)
    {
        var messageReference = await chats.GetMessageAsync(
            messageId,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (messageReference is null)
        {
            return;
        }
        var message = messageReference;
        var session = await chats.GetSessionAsync(message.SessionId, CancellationToken.None).ConfigureAwait(false);
        if (session is null) return;
        var messageArtifacts = knownArtifacts ?? await artifacts.ListForMessageAsync(messageId, CancellationToken.None).ConfigureAwait(false);
        await emit("conversation.messageCommitted", new
        {
            sessionId = message.SessionId,
            conversationRevision = session.ConversationRevision,
            message = ToMessageDto(message, messageArtifacts),
        }, requestId).ConfigureAwait(false);
    }

    public async Task HandleAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken = default)
    {
        if (await HandleDisplayReadAsync(envelope, emit, cancellationToken).ConfigureAwait(false)) return;
        EnsureActivityCommandIsSupported(envelope);
        switch (envelope.Type)
        {
            case "app.ready":
            {
                var isFirstReady = Interlocked.CompareExchange(ref _startupRunsHandled, 1, 0) == 0;
                if (isFirstReady && missumAi is not null)
                {
                    // Local history must be visible even while the gateway starts
                    // or an old cancellation/cache-cleanup request is pending.
                    await missumAi.PreparePersistedRunsAtStartupAsync(cancellationToken).ConfigureAwait(false);
                }
                await emit("state.snapshot", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                if (CurrentSettings.AiProvider == AiProviderKind.MissumAiServer
                    && missumAi is not null)
                {
                    lock (_resumeLock)
                    {
                        // A new WebView can arrive while the previous cancelled reader is
                        // still releasing its run gate. Always attach this page afterwards.
                        _resumeTask = ResumePendingInBackgroundAsync(emit, envelope.RequestId, cancellationToken, _resumeTask);
                    }
                }
                break;
            }
            case "session.create":
            {
                var requestedMode = GetOptionalString(envelope.Payload, "chatMode", 16)
                    ?? GetOptionalString(envelope.Payload, "mode", 16);
                await CreateSessionAsync(
                    requestedMode is null ? null : ParseChatMode(requestedMode),
                    emit,
                    envelope.RequestId,
                    cancellationToken);
                break;
            }
            case "mode.switch":
                await SwitchModeAsync(
                    ParseChatMode(
                        GetOptionalString(envelope.Payload, "chatMode", 16)
                        ?? GetOptionalString(envelope.Payload, "mode", 16)),
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "session.projectCreate":
                await CreateWorkspaceSessionAsync(
                    GetRequiredString(envelope.Payload, "workspacePath", 32_768),
                    ParseChatMode(
                        GetOptionalString(envelope.Payload, "chatMode", 16)
                        ?? GetOptionalString(envelope.Payload, "mode", 16)
                        ?? ChatModeName(CurrentSettings.SelectedChatMode)),
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "session.open":
                await OpenSessionAsync(GetRequiredGuid(envelope.Payload, "sessionId"), emit, envelope.RequestId, cancellationToken);
                break;
            case "conversation.refresh":
            {
                var requestedSessionId = GetOptionalGuid(envelope.Payload, "sessionId")
                    ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
                await emit(
                    "conversation.snapshot",
                    await BuildConversationSnapshotAsync(requestedSessionId, cancellationToken).ConfigureAwait(false),
                    envelope.RequestId).ConfigureAwait(false);
                break;
            }
            case "session.rename":
                await RenameSessionAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetRequiredString(envelope.Payload, "title", 160),
                    emit,
                    envelope.RequestId,
                    cancellationToken);
                break;
            case "session.delete":
                await DeleteSessionAsync(GetRequiredGuid(envelope.Payload, "sessionId"), emit, envelope.RequestId, cancellationToken);
                break;
            case "session.clear":
            {
                // The confirmation dialog belongs to the mode that was visible
                // when the user opened it. Capture that mode from the request so
                // a concurrent mode switch cannot redirect the destructive action.
                var requestedMode = GetOptionalString(envelope.Payload, "chatMode", 16)
                    ?? GetOptionalString(envelope.Payload, "mode", 16);
                var mode = requestedMode is null
                    ? CurrentSettings.SelectedChatMode
                    : ParseChatMode(requestedMode);
                await ClearSessionsAsync(mode, emit, envelope.RequestId, cancellationToken);
                break;
            }
            case "session.draft":
                await SaveDraftAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetOptionalString(envelope.Payload, "draft", 100_000) ?? string.Empty,
                    cancellationToken);
                await emit("draft.saved", new { }, envelope.RequestId);
                break;
            case "session.groupCollapse":
            {
                var groupId = GetRequiredGuid(envelope.Payload, "groupId");
                var collapsed = envelope.Payload.TryGetProperty("collapsed", out var collapsedElement)
                    && collapsedElement.ValueKind == JsonValueKind.True;
                await chats.SetSessionGroupCollapsedAsync(groupId, collapsed, cancellationToken).ConfigureAwait(false);
                await emit("session.grouped", await BuildSessionSidebarSnapshotAsync(false, cancellationToken), envelope.RequestId).ConfigureAwait(false);
                break;
            }
            case "session.groupDeleteEmpty":
                await chats.DeleteEmptySessionGroupAsync(GetRequiredGuid(envelope.Payload, "groupId"), cancellationToken).ConfigureAwait(false);
                await emit("session.grouped", await BuildSessionSidebarSnapshotAsync(false, cancellationToken), envelope.RequestId).ConfigureAwait(false);
                break;
            case "action.invoke":
                await InvokeActionAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "models.list":
                if (missumAi is null) throw new InvalidOperationException("AI-Verbindung ist nicht verfügbar.");
                await emit("models.snapshot", await missumAi.GetBrowserModelsAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
                break;
            case "models.select":
            {
                if (missumAi is null) throw new InvalidOperationException("AI-Verbindung ist nicht verfügbar.");
                var modelId = GetRequiredString(envelope.Payload, "modelId", 512);
                var catalog = await missumAi.GetBrowserModelsAsync(cancellationToken).ConfigureAwait(false);
                if (!catalog.Models.Any(model => model.Id == modelId && model.Downloaded && model.Role is "general" or "coding"))
                    throw new ArgumentException("Das ausgewählte Modell ist nicht installiert.");
                await UpdateViewSettingsAsync(current => current with { SelectedModel = modelId, SelectedCodingModel = modelId }, cancellationToken).ConfigureAwait(false);
                await emit("state.snapshot", await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
                await emit("models.snapshot", catalog, envelope.RequestId).ConfigureAwait(false);
                break;
            }
            case "reasoning.get":
            case "reasoning.set":
            {
                if (missumAi is null) throw new InvalidOperationException("AI-Verbindung ist nicht verfügbar.");
                var role = GetOptionalString(envelope.Payload, "role", 16) ?? "general";
                if (role is not ("coding" or "general")) throw new ArgumentException("Unbekannte Modellrolle.");
                var modelId = GetOptionalString(envelope.Payload, "modelId", 512) ?? "";
                var selectedId = role == "coding" ? CurrentSettings.SelectedModel : CurrentSettings.SelectedModel;
                if (modelId != selectedId) throw new InvalidOperationException("Die Modellauswahl hat sich geändert. Öffne Reasoning erneut.");
                var options = await missumAi.GetReasoningOptionsAsync(modelId, role, cancellationToken).ConfigureAwait(false);
                if (envelope.Type == "reasoning.set")
                {
                    if (modelId != (role == "coding" ? CurrentSettings.SelectedModel : CurrentSettings.SelectedModel))
                        throw new InvalidOperationException("Das ausgewählte Modell hat sich während der Prüfung geändert.");
                    var effort = GetRequiredString(envelope.Payload, "effort", 32).Trim().ToLowerInvariant();
                    if (!options.Available || !options.Levels.Contains(effort))
                        throw new ArgumentException("Diese Reasoning-Stufe wird vom ausgewählten Modell nicht unterstützt.");
                    await missumAi.PersistSharedReasoningAsync(modelId, role, effort, cancellationToken).ConfigureAwait(false);
                    options = options with { Selected = effort };
                    // Reasoning is shared for future submissions. Accepted runs
                    // retain the preferences frozen when they entered the queue.
                }
                await emit("reasoning.snapshot", options, envelope.RequestId);
                break;
            }
            case "chat.send":
                await SendChatAsync(envelope, emit, cancellationToken);
                break;
            case "chat.resume":
                if (missumAi is null) throw new InvalidOperationException("Der AI-Clientdienst ist nicht verfügbar.");
                if (_runScheduler?.Snapshot.IsIdle == false)
                    throw new InvalidOperationException("Es läuft bereits ein geplanter Auftrag. Der gestoppte Lauf wurde nicht nochmals gestartet.");
                try
                {
                    await missumAi.ResumeMessageAsync(GetRequiredGuid(envelope.Payload, "sessionId"),
                        GetRequiredGuid(envelope.Payload, "messageId"),
                        update => EmitMissumAiUpdateAsync(update, emit, envelope.RequestId), cancellationToken).ConfigureAwait(false);
                }
                catch (MissumAiStreamDetachedException) { }
                break;
            case "chat.steer":
                if (missumAi is null) throw new InvalidOperationException("AI-Verbindung ist nicht verfügbar.");
                var steeringSessionId = GetRequiredGuid(envelope.Payload, "sessionId");
                var steeringPrompt = GetRequiredString(envelope.Payload, "prompt", 100_000);
                var steeringAccepted = await missumAi.SteerAsync(steeringSessionId,
                    steeringPrompt,
                    GetRequiredString(envelope.Payload, "inputId", 128),
                    GetOptionalString(envelope.Payload, "expectedRunId", 128), cancellationToken).ConfigureAwait(false);
                // A retry of an old acknowledgement must not replace the title
                // from a newer submission. Rejected steering keeps the old name.
                if (!steeringAccepted.Duplicate
                    && await chats.GetSessionAsync(steeringSessionId, CancellationToken.None).ConfigureAwait(false) is { } steeringSession)
                    await SaveSubmittedPromptTitleAsync(steeringSession, steeringPrompt, emit, envelope.RequestId,
                        CancellationToken.None).ConfigureAwait(false);
                await emit("chat.steer.accepted", new { inputId = steeringAccepted.InputId,
                    sessionId = steeringSessionId, runId = steeringAccepted.RunId, sequence = steeringAccepted.Sequence }, envelope.RequestId).ConfigureAwait(false);
                break;
            case "chat.cancel":
                await CancelCurrentAsync(GetOptionalGuid(envelope.Payload, "sessionId")).ConfigureAwait(false);
                if (_runScheduler is not null)
                {
                    await emit(
                        "queue.changed",
                        new { runQueue = ToRunQueueDto(_runScheduler.Snapshot) },
                        envelope.RequestId).ConfigureAwait(false);
                }
                break;
            case "document.remove":
                EnsureContextCanChange();
                await documents.RemoveAsync(GetRequiredGuid(envelope.Payload, "documentId"), cancellationToken);
                await emit("document.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "attachment.remove":
                EnsureContextCanChange();
                await attachments.RemoveAsync(GetRequiredGuid(envelope.Payload, "attachmentId"), cancellationToken);
                await emit("document.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "memory.list":
                await EmitProjectMemoryAsync(envelope, emit, "memory.snapshot", cancellationToken).ConfigureAwait(false);
                break;
            case "memory.create":
                await CreateProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.update":
                await UpdateProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.pin":
                await PinProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.confirm":
                await ConfirmProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.delete":
                await DeleteProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.autoCapture":
                await SetProjectMemoryAutoCaptureAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "publication.list":
                await EmitPublicationAsync(envelope, emit, selectedProjectId: null, cancellationToken).ConfigureAwait(false);
                break;
            case "publication.open":
                await EmitPublicationAsync(envelope, emit,
                    GetRequiredString(envelope.Payload, "projectId", 128), cancellationToken).ConfigureAwait(false);
                break;
            case "ui.sessionPane":
                await UpdateViewSettingsAsync(current => current with
                {
                    IsAssistantSessionPaneOpen = GetRequiredBoolean(envelope.Payload, "isOpen"),
                }, CancellationToken.None).ConfigureAwait(false);
                break;
        }
    }

    public async Task ImportDocumentAsync(
        Guid sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        EnsureContextCanChange(sessionId);
        var result = await documents.ImportAsync(sessionId, fileName, content, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Das Dokument konnte nicht importiert werden.");
        }

        if (!result.HasExtractableText)
        {
            if (result.Document is { } emptyDocument)
            {
                await documents.RemoveAsync(emptyDocument.Id, CancellationToken.None).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Das Dokument enthält keinen extrahierbaren Text. OCR ist in dieser Version nicht enthalten.");
        }

        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await recentActivity.RecordAsync(
            $"Datei „{fileName}“ zur AI-Sitzung „{session.Title}“ hinzugefügt",
            CancellationToken.None).ConfigureAwait(false);
    }

    public IReadOnlySet<string> SupportedDocumentExtensions => documents.SupportedExtensions;

    public async Task ImportAttachmentAsync(
        Guid sessionId,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        EnsureContextCanChange(sessionId);
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        _ = await attachments.ImportAsync(sessionId, fileName, contentType, content, cancellationToken).ConfigureAwait(false);
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"Datei „{fileName}“ zur AI-Sitzung „{session?.Title ?? DefaultSessionTitle}“ hinzugefügt",
            CancellationToken.None).ConfigureAwait(false);
    }

    private void EnsureContextCanChange(Guid? targetSessionId = null)
    {
        var selectedSessionId = targetSessionId
            ?? ActiveSessionIdFor(CurrentSettings, CurrentSettings.SelectedChatMode)
            ?? CurrentSettings.ActiveSessionId;
        var queue = _runScheduler?.Snapshot;
        var sessionIsBusy = selectedSessionId.HasValue && (
            missumAi?.ActiveSessionId == selectedSessionId.Value
            || queue?.Active?.SessionId == selectedSessionId.Value
            || queue?.Pending.Any(item => item.SessionId == selectedSessionId.Value) == true);
        if (sessionIsBusy)
        {
            throw new InvalidOperationException("Anhänge und Dokumente können während eines laufenden AI-Auftrags nicht geändert werden.");
        }
    }

    private async Task<ChatSession> EnsureActiveSessionAsync(CancellationToken cancellationToken)
    {
        // One-time settings migration: older builds persisted only one active ID.
        // Resolve its durable mode before choosing the surface to restore.
        if (CurrentSettings.ActiveGeneralSessionId is null
            && CurrentSettings.ActiveCodingSessionId is null
            && CurrentSettings.ActiveSessionId is { } legacyId
            && await chats.GetSessionAsync(legacyId, cancellationToken).ConfigureAwait(false) is { } legacy)
        {
            await ActivateSessionAsync(legacy, cancellationToken).ConfigureAwait(false);
        }

        var mode = CurrentSettings.SelectedChatMode;
        var activeId = ActiveSessionIdFor(CurrentSettings, mode);
        if (activeId is { } selectedId
            && await chats.GetSessionAsync(selectedId, cancellationToken).ConfigureAwait(false) is { } active
            && active.ChatMode == mode)
        {
            if (CurrentSettings.ActiveSessionId != active.Id)
            {
                await ActivateSessionAsync(active, cancellationToken).ConfigureAwait(false);
            }
            return active;
        }

        var existing = await chats.ListSessionsAsync(mode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = existing.Count > 0
            ? existing[0]
            : await chats.CreateSessionAsync(DefaultSessionTitle, mode, cancellationToken).ConfigureAwait(false);
        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private Task ActivateSessionAsync(ChatSession session, CancellationToken cancellationToken) =>
        UpdateViewSettingsAsync(current => session.ChatMode switch
        {
            ChatMode.General => current with
            {
                SelectedChatMode = ChatMode.General,
                ActiveGeneralSessionId = session.Id,
                ActiveSessionId = session.Id,
            },
            ChatMode.Coding => current with
            {
                SelectedChatMode = ChatMode.Coding,
                ActiveCodingSessionId = session.Id,
                ActiveSessionId = session.Id,
            },
            ChatMode.ClaudeScience => current with
            {
                SelectedChatMode = ChatMode.ClaudeScience,
                ActiveClaudeScienceSessionId = session.Id,
                ActiveSessionId = session.Id,
            },
            _ => throw new InvalidOperationException("Die Sitzung enthält einen unbekannten Chatmodus."),
        }, cancellationToken);

    private static Guid? ActiveSessionIdFor(AppSettings settingsValue, ChatMode mode) => mode switch
    {
        ChatMode.General => settingsValue.ActiveGeneralSessionId,
        ChatMode.Coding => settingsValue.ActiveCodingSessionId,
        ChatMode.ClaudeScience => settingsValue.ActiveClaudeScienceSessionId,
        _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
    };

    private async Task SwitchModeAsync(
        ChatMode mode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        await UpdateViewSettingsAsync(current => current with
        {
            SelectedChatMode = mode,
            ActiveSessionId = ActiveSessionIdFor(current, mode),
        }, cancellationToken).ConfigureAwait(false);
        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId).ConfigureAwait(false);
    }

    private async Task CreateSessionAsync(
        ChatMode? requestedMode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.CreateSessionAsync(
            DefaultSessionTitle,
            requestedMode ?? CurrentSettings.SelectedChatMode,
            cancellationToken).ConfigureAwait(false);
        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ erstellt",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    public async Task CreateWorkspaceSessionAsync(
        string workspacePath,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken) =>
        await CreateWorkspaceSessionAsync(
            workspacePath,
            CurrentSettings.SelectedChatMode,
            emit,
            requestId,
            cancellationToken).ConfigureAwait(false);

    public async Task CreateWorkspaceSessionAsync(
        string workspacePath,
        ChatMode chatMode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        EnsureContextCanChange();
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (!Enum.IsDefined(chatMode)) throw new ArgumentOutOfRangeException(nameof(chatMode));
        var fullPath = Path.GetFullPath(workspacePath.Trim());
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Der Projektordner existiert nicht.");
        await ProjectRepositoryScaffold.EnsureAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        await SciencePublicationScaffold.EnsureAsync(chatMode, fullPath, cancellationToken).ConfigureAwait(false);
        var session = await chats.CreateSessionAsync(DefaultSessionTitle, chatMode, cancellationToken).ConfigureAwait(false);
        try
        {
            // A workspace is shared session context in every mode. Assigning it
            // never changes the immutable mode selected by the user.
            await chats.SetCodingWorkspacePathAsync(session.Id, fullPath, activateCoding: false, cancellationToken).ConfigureAwait(false);
            session = await chats.GetSessionAsync(session.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die neue Workspace-Sitzung wurde nicht gespeichert.");
            if (session.SessionGroupId is not { } groupId)
                throw new InvalidOperationException("Die neue Workspace-Sitzung besitzt keine Projektgruppe.");
            await chats.SetSessionGroupCollapsedAsync(groupId, false, cancellationToken).ConfigureAwait(false);
            await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await chats.DeleteSessionAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await recentActivity.RecordAsync(
            $"Neue Sitzung im Projekt \"{WorkspaceLabel(fullPath)}\" gestartet",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task SetSessionToolAsync(
        Guid sessionId,
        string? requestedAction,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var extensionActionId = requestedAction?.Trim() switch
        {
            null or "" => null,
            "audiobook" => BuiltInActionIds.CreateAudiobook,
            "planMode" => BuiltInActionIds.PlanMode,
            _ => throw new InvalidOperationException("Die angeforderte persistente Tool-Aktion ist unbekannt."),
        };
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die ausgewählte Tool-Sitzung wurde nicht gefunden.");
        await chats.SetPersistentExtensionActionIdAsync(session.Id, extensionActionId, cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId).ConfigureAwait(false);
    }

    private async Task InvokeActionAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var actionId = GetRequiredString(envelope.Payload, "actionId", ExtensionIdentifiers.MaximumIdentifierLength);
        if (!_extensionActions.TryGetAction(actionId, out var descriptor) || descriptor is null)
            throw new InvalidOperationException($"Die Erweiterungsaktion '{actionId}' ist nicht registriert.");
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung der Erweiterungsaktion wurde nicht gefunden.");
        var extensionMode = ToExtensionChatMode(session.ChatMode);
        if (!descriptor.SupportedChatModes.Contains(extensionMode))
            throw new InvalidOperationException("Die Erweiterungsaktion unterstützt den aktuellen Chatmodus nicht.");
        if (!string.IsNullOrWhiteSpace(descriptor.DisabledReason))
            throw new InvalidOperationException(descriptor.DisabledReason);
        var enabled = true;
        if (envelope.Payload.TryGetProperty("enabled", out var enabledElement))
        {
            if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("'enabled' muss ein boolescher Wert sein.");
            enabled = enabledElement.GetBoolean();
        }
        if (descriptor.ActionKind == ExtensionActionKind.SelectableTool) EnsureContextCanChange(sessionId);
        if (!actionId.StartsWith("builtin.", StringComparison.Ordinal))
        {
            if (extensionRuntime is null)
                throw new InvalidOperationException("Der ExtensionHost ist für Drittanbieter-Aktionen nicht verfügbar.");
            var groups = await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false);
            var workspaceRoot = ResolveSessionWorkspaceRoot(session, groups);
            var disabledReason = extensionRuntime.GetDisabledReason(actionId, workspaceRoot);
            if (enabled && disabledReason is not null) throw new InvalidOperationException(disabledReason);
            if (descriptor.ActionKind == ExtensionActionKind.SelectableTool)
            {
                await emit("action.completed", new { actionId, enabled }, envelope.RequestId).ConfigureAwait(false);
                return;
            }
            var arguments = envelope.Payload.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement
                : JsonSerializer.SerializeToElement(new { });
            var result = await extensionRuntime.InvokeAsync(
                actionId, arguments, workspaceRoot, cancellationToken).ConfigureAwait(false);
            await emit("action.completed", new { actionId, result }, envelope.RequestId).ConfigureAwait(false);
            return;
        }

        if (string.Equals(actionId, BuiltInActionIds.DeepResearch, StringComparison.Ordinal))
        {
            await emit("action.completed", new
            {
                actionId,
                toolToggle = "deepResearch",
                enabled,
            }, envelope.RequestId).ConfigureAwait(false);
            return;
        }

        if (!PromptActionExtensionIds.TryGetPromptAction(actionId, out var promptAction)
            || promptAction is PromptTriggerAction.Coding or PromptTriggerAction.LiveCaptions)
            throw new InvalidOperationException($"Die integrierte Aktion '{actionId}' muss vom nativen UI-Host ausgeführt werden.");

        var toolAction = PromptToolActionName(promptAction);
        if (promptAction is PromptTriggerAction.Audiobook or PromptTriggerAction.PlanMode)
        {
            await SetSessionToolAsync(
                session.Id,
                enabled ? toolAction : null,
                emit,
                envelope.RequestId,
                cancellationToken).ConfigureAwait(false);
        }
        await emit("action.completed", new
        {
            actionId,
            toolAction,
            enabled,
            captureAction = enabled && promptAction is (PromptTriggerAction.AudioAnalysis
                or PromptTriggerAction.VideoAnalysis
                or PromptTriggerAction.ImageAnalysis)
                    ? toolAction
                    : null,
        }, envelope.RequestId).ConfigureAwait(false);
    }

    private static string PromptToolActionName(PromptTriggerAction action) => action switch
    {
        PromptTriggerAction.AudioAnalysis => "audioAnalysis",
        PromptTriggerAction.VideoAnalysis => "videoAnalysis",
        PromptTriggerAction.ImageAnalysis => "imageAnalysis",
        PromptTriggerAction.ImageGeneration => "imageGeneration",
        PromptTriggerAction.Audiobook => "audiobook",
        PromptTriggerAction.DocumentCreate => "documentCreate",
        PromptTriggerAction.TextToSpeech => "textToSpeech",
        PromptTriggerAction.Translation => "translation",
        PromptTriggerAction.WebSearch => "webSearch",
        PromptTriggerAction.PlanMode => "planMode",
        PromptTriggerAction.Lean => "lean",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Die Promptaktion ist nicht über das Werkzeugmenü auswählbar."),
    };

    private static string? GetExplicitExtensionActionId(JsonElement payload)
    {
        var extensionActionId = GetOptionalString(
            payload,
            "extensionActionId",
            ExtensionIdentifiers.MaximumIdentifierLength);
        if (!string.IsNullOrWhiteSpace(extensionActionId))
        {
            PromptActionExtensionIds.EnsureValid(extensionActionId);
            return extensionActionId;
        }

        // Read-only compatibility for pre-extension WebView assets and persisted retries.
        var legacyToolAction = GetOptionalString(payload, "toolAction", 40);
        return string.IsNullOrWhiteSpace(legacyToolAction)
            ? null
            : LegacyExtensionActionIdFor(legacyToolAction);
    }

    private static string LegacyExtensionActionIdFor(string toolAction) => toolAction switch
    {
        "audioAnalysis" => BuiltInActionIds.AudioAnalysis,
        "imageAnalysis" => BuiltInActionIds.ImageAnalysis,
        "imageGeneration" => BuiltInActionIds.GenerateImage,
        "audiobook" => BuiltInActionIds.CreateAudiobook,
        "coding" => PromptActionExtensionIds.CodingRun,
        "documentCreate" => BuiltInActionIds.CreateDocument,
        "textToSpeech" => BuiltInActionIds.ReadAloud,
        "translation" => BuiltInActionIds.Translate,
        "videoAnalysis" => BuiltInActionIds.VideoAnalysis,
        "webSearch" => BuiltInActionIds.WebSearch,
        "lean" => BuiltInActionIds.Lean,
        _ => throw new ArgumentException("Die ausgewählte Tool-Aktion ist nicht bekannt.", nameof(toolAction)),
    };

    private async Task OpenSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task RenameSessionAsync(
        Guid sessionId,
        string title,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await chats.RenameSessionAsync(sessionId, title, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung in „{title}“ umbenannt",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task DeleteSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");

        await CancelScheduledSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await chats.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _displayStates.TryRemove(sessionId, out _);
        await CleanupDeletedCodingChangesAsync([sessionId], [session]).ConfigureAwait(false);
        if (CurrentSettings.ActiveSessionId == sessionId
            || ActiveSessionIdFor(CurrentSettings, session.ChatMode) == sessionId)
        {
            await UpdateViewSettingsAsync(current => session.ChatMode switch
            {
                ChatMode.General => current with
                {
                    ActiveGeneralSessionId = null,
                    ActiveSessionId = current.SelectedChatMode == ChatMode.General ? null : current.ActiveSessionId,
                },
                ChatMode.Coding => current with
                {
                    ActiveCodingSessionId = null,
                    ActiveSessionId = current.SelectedChatMode == ChatMode.Coding ? null : current.ActiveSessionId,
                },
                ChatMode.ClaudeScience => current with
                {
                    ActiveClaudeScienceSessionId = null,
                    ActiveSessionId = current.SelectedChatMode == ChatMode.ClaudeScience ? null : current.ActiveSessionId,
                },
                _ => throw new InvalidOperationException("Die Sitzung enthält einen unbekannten Chatmodus."),
            }, cancellationToken).ConfigureAwait(false);
        }

        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ gelöscht",
            CancellationToken.None).ConfigureAwait(false);
        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task CancelScheduledSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var cancelActive = missumAi?.ActiveSessionId == sessionId
            ? missumAi.CancelCurrentAndWaitAsync(sessionId, CancellationToken.None)
            : Task.CompletedTask;
        var ticketIds = Array.Empty<Guid>();
        if (_runScheduler is not null)
        {
            var snapshot = _runScheduler.Snapshot;
            ticketIds = snapshot.Pending
                .Where(item => item.SessionId == sessionId)
                .Select(static item => item.TicketId)
                .Concat(snapshot.Active is { } active && active.SessionId == sessionId
                    ? [active.TicketId]
                    : [])
                .Distinct()
                .ToArray();
            foreach (var ticketId in ticketIds)
            {
                _runScheduler.TryCancel(ticketId);
            }
        }

        await cancelActive.ConfigureAwait(false);

        var observers = ticketIds
            .Select(ticketId => _scheduledRunObservers.TryGetValue(ticketId, out var observer) ? observer : null)
            .Where(static observer => observer is not null)
            .Cast<Task>()
            .ToArray();
        if (observers.Length > 0)
        {
            await Task.WhenAll(observers)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ClearSessionsAsync(
        ChatMode mode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (missumAi?.IsRunning == true || _runScheduler?.Snapshot.IsIdle == false)
        {
            throw new InvalidOperationException("Die Sitzungen können während einer laufenden Antwort nicht gelöscht werden.");
        }

        var previousSessions = await chats.ListSessionsAsync(mode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var deletedCount = await chats.DeleteSessionsAsync(mode, cancellationToken).ConfigureAwait(false);
        if (deletedCount > 0)
            await CleanupDeletedCodingChangesAsync(previousSessions.Select(static session => session.Id), previousSessions).ConfigureAwait(false);

        await UpdateViewSettingsAsync(current => mode switch
        {
            ChatMode.General => current with
            {
                ActiveGeneralSessionId = null,
                ActiveSessionId = current.SelectedChatMode == ChatMode.General ? null : current.ActiveSessionId,
            },
            ChatMode.Coding => current with
            {
                ActiveCodingSessionId = null,
                ActiveSessionId = current.SelectedChatMode == ChatMode.Coding ? null : current.ActiveSessionId,
            },
            ChatMode.ClaudeScience => current with
            {
                ActiveClaudeScienceSessionId = null,
                ActiveSessionId = current.SelectedChatMode == ChatMode.ClaudeScience ? null : current.ActiveSessionId,
            },
            _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
        }, cancellationToken).ConfigureAwait(false);
        if (deletedCount > 0)
        {
            await recentActivity.RecordAsync(
                $"Alle AI-Sitzungen der Ansicht „{mode}“ gelöscht",
                CancellationToken.None).ConfigureAwait(false);
        }

        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task CleanupDeletedCodingChangesAsync(IEnumerable<Guid> candidateSessionIds,
        IEnumerable<ChatSession>? deletedSessions = null)
    {
        // Re-read after the committed deletion so only removed sessions lose
        // their workspace baselines and evidence bundles.
        var remaining = (await chats.ListSessionsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false))
            .Select(static session => session.Id).ToHashSet();
        if (_cacheCleanup is not null && deletedSessions is not null)
        {
            try
            {
                await _cacheCleanup.DeleteSessionsAsync(deletedSessions.Where(session => !remaining.Contains(session.Id)),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Trace.TraceWarning("Die Sitzung wurde gelöscht; die gezielte KV-Cache-Bereinigung konnte noch nicht abgeschlossen werden: {0}", exception.Message);
            }
        }
        foreach (var sessionId in candidateSessionIds.Where(id => !remaining.Contains(id)))
        {
            _displayStates.TryRemove(sessionId, out _);
            try { await CodingChangesMonitor.DeleteSessionStorageAsync(settings.DataDirectory, sessionId).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Die gelöschte Sitzung {0} hinterließ einen nicht löschbaren Änderungscache: {1}", sessionId, exception.Message);
            }
            try { await CodingRunEvidenceStore.DeleteSessionAsync(settings.DataDirectory, sessionId).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Trace.TraceWarning("Die gelöschte Sitzung {0} hinterließ nicht löschbare Werkzeugbelege: {1}", sessionId, exception.Message);
            }
        }
    }

    private async Task SendChatAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        if (_runScheduler is null)
        {
            await SendMissumAiChatAsync(envelope, emit, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var runScope = AssistantClientExecutionScope.Capture(settings.Current);
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var prompt = GetRequiredString(envelope.Payload, "prompt", 100_000);
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        if (session.ChatMode is ChatMode.Coding or ChatMode.ClaudeScience
            && (string.IsNullOrWhiteSpace(session.CodingWorkspacePath)
                || !Directory.Exists(session.CodingWorkspacePath)))
        {
            throw new InvalidOperationException("Wähle für diese Coding-Sitzung zuerst ein vorhandenes Projekt oder einen Workspace aus.");
        }

        // Resolve tools when the user submits, not when a waiting run eventually
        // receives the model. A captured null is intentional and stays null.
        var match = IsCancelCommand(prompt) ? null
            : await ResolveRequestedMatchAsync(session, prompt, envelope.Payload, cancellationToken).ConfigureAwait(false);
        var persistentActionId = PersistentExtensionActionIdFor(match?.Trigger.Action);
        if (persistentActionId is not null && session.PersistentExtensionActionId != persistentActionId)
        {
            await chats.SetPersistentExtensionActionIdAsync(sessionId, persistentActionId, cancellationToken).ConfigureAwait(false);
            session = session with { PersistentExtensionActionId = persistentActionId };
            await emit("session.changed", await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
        }
        var queuedInput = new QueuedChatInput(session, match);
        await SaveDraftAsync(sessionId, string.Empty, cancellationToken).ConfigureAwait(false);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticket = _runScheduler.Enqueue<object?>(
            sessionId,
            envelope.RequestId,
            async scheduledCancellationToken =>
            {
                using var executionScope = runScope();
                await accepted.Task.WaitAsync(scheduledCancellationToken).ConfigureAwait(false);
                await WaitForDetachedRunAsync(scheduledCancellationToken).ConfigureAwait(false);
                await TryEmitAsync(
                    emit,
                    "queue.changed",
                    new { runQueue = ToRunQueueDto(_runScheduler.Snapshot) },
                    envelope.RequestId).ConfigureAwait(false);
                await SendMissumAiChatAsync(envelope, emit, queuedInput, scheduledCancellationToken).ConfigureAwait(false);
                return null;
            },
            CancellationToken.None);

        var observer = ObserveScheduledRunAsync(ticket, emit, envelope.RequestId);
        _scheduledRunObservers[ticket.TicketId] = observer;
        if (observer.IsCompleted)
        {
            _scheduledRunObservers.TryRemove(ticket.TicketId, out _);
        }

        try
        {
            // Commit at admission, before the model slot is available. Execution
            // of this older queued prompt must never rename the session again.
            if (!IsCancelCommand(prompt))
                await SaveSubmittedPromptTitleAsync(session, prompt, emit, envelope.RequestId,
                    CancellationToken.None).ConfigureAwait(false);
            await emit("chat.queued", new
            {
                ticketId = ticket.TicketId,
                ticket.SessionId,
                ticket.RequestId,
                position = ticket.InitialPosition,
                runQueue = ToRunQueueDto(_runScheduler.Snapshot),
            }, envelope.RequestId).ConfigureAwait(false);
        }
        finally
        {
            accepted.TrySetResult();
        }
    }

    private async Task WaitForDetachedRunAsync(CancellationToken cancellationToken)
    {
        while (missumAi?.IsRunning == true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ObserveScheduledRunAsync(
        AssistantRunTicket<object?> ticket,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        try
        {
            await ticket.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryEmitAsync(emit, "chat.cancelled", new
            {
                sessionId = ticket.SessionId,
                queued = true,
                runStatus = "Aus Warteschlange entfernt",
            }, requestId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await TryEmitAsync(emit, "host.error", new { message = exception.Message }, requestId).ConfigureAwait(false);
        }
        finally
        {
            _scheduledRunObservers.TryRemove(ticket.TicketId, out _);
            if (_runScheduler is not null)
            {
                await TryEmitAsync(
                    emit,
                    "queue.changed",
                    new { runQueue = ToRunQueueDto(_runScheduler.Snapshot) },
                    requestId).ConfigureAwait(false);
            }
        }
    }

    private static async Task TryEmitAsync(
        Func<string, object, string?, Task> emit,
        string type,
        object payload,
        string? requestId)
    {
        try
        {
            await emit(type, payload, requestId).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A closed/reloading WebView must not fail a queued model run or session cleanup.
        }
    }

    private async Task SendMissumAiChatAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        QueuedChatInput? queuedInput,
        CancellationToken cancellationToken)
    {
        var prompt = GetRequiredString(envelope.Payload, "prompt", 100_000);
        if (IsCancelCommand(prompt))
        {
            if (microphone is not null)
            {
                await microphone.StopSpeechAsync(cancellationToken).ConfigureAwait(false);
            }
            if (missumAi is not null)
            {
                await missumAi.CancelCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await emit("speech.status", new
            {
                active = false,
                status = "Abgebrochen",
                detail = (string?)null,
                model = (string?)null,
                error = (string?)null,
            }, envelope.RequestId).ConfigureAwait(false);
            return;
        }
        var sessionId = queuedInput?.Session.Id ?? GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        if (queuedInput is not null)
            session = session with
            {
                ChatMode = queuedInput.Session.ChatMode,
                CodingWorkspacePath = queuedInput.Session.CodingWorkspacePath,
                PersistentExtensionActionId = queuedInput.Session.PersistentExtensionActionId,
            };
        if (session.ChatMode is ChatMode.Coding or ChatMode.ClaudeScience
            && (string.IsNullOrWhiteSpace(session.CodingWorkspacePath)
                || !Directory.Exists(session.CodingWorkspacePath)))
        {
            throw new InvalidOperationException("Wähle für diese Coding-Sitzung zuerst ein vorhandenes Projekt oder einen Workspace aus.");
        }
        if (queuedInput is null)
            session = await SaveSubmittedPromptTitleAsync(session, prompt, emit, envelope.RequestId,
                cancellationToken).ConfigureAwait(false);
        await SaveDraftAsync(sessionId, string.Empty, cancellationToken).ConfigureAwait(false);
        var match = queuedInput is not null ? queuedInput.Match
            : await ResolveRequestedMatchAsync(session, prompt, envelope.Payload, cancellationToken).ConfigureAwait(false);

        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var speechMessageId = GetOptionalGuid(envelope.Payload, "speechMessageId");
        if (match?.Trigger.Action == PromptTriggerAction.TextToSpeech)
        {
            var serverAssistant = missumAi
                ?? throw new InvalidOperationException("Der Missum-AI-Clientdienst ist nicht verfügbar.");
            await serverAssistant.SpeakAsync(
                sessionId,
                match.RemainingPrompt,
                speechMessageId,
                speech => emit("speech.status", new
                {
                    sessionId,
                    isPromptRequest = true,
                    active = speech.IsActive,
                    status = speech.Status,
                    detail = speech.Detail,
                    model = speech.Model,
                    directionModel = speech.DirectionModel,
                    error = speech.Error,
                    cacheHit = speech.CacheHit,
                }, envelope.RequestId),
                playback => emit(
                    "speech.progress",
                    SpeechPlaybackProgressBridge.ToPayload(playback),
                    envelope.RequestId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        var requestedPersistentActionId = PersistentExtensionActionIdFor(match?.Trigger.Action);
        if (string.Equals(requestedPersistentActionId, BuiltInActionIds.CreateAudiobook, StringComparison.Ordinal)
            && IsAudiobookContinuationRequest(prompt, match)
            && !await HasAudiobookContentAsync(session.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "In dieser Sitzung ist noch keine Hörbuchgeschichte vorhanden. Starte zuerst mit „Hörbuch erstellen“.");
        }
        if (queuedInput is null && requestedPersistentActionId is { } persistentActionId
            && !string.Equals(session.PersistentExtensionActionId, persistentActionId, StringComparison.Ordinal))
        {
            await chats.SetPersistentExtensionActionIdAsync(
                session.Id,
                persistentActionId,
                cancellationToken).ConfigureAwait(false);
            await emit(
                "session.changed",
                await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false),
                envelope.RequestId).ConfigureAwait(false);
        }
        try
        {
            var serverAssistant = missumAi
                ?? throw new InvalidOperationException("Der Missum-AI-Clientdienst ist nicht verfügbar.");
            _ = await serverAssistant.SendAsync(
                sessionId,
                prompt,
                match,
                update => EmitMissumAiUpdateAsync(update, emit, envelope.RequestId),
                submittedSession: queuedInput?.Session,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MissumAiStreamDetachedException)
        {
            // Navigating away only detaches the local SSE reader. The run is resumed from SQLite later.
        }
    }

    private async Task<PromptTriggerMatch?> ResolveRequestedMatchAsync(ChatSession session, string prompt,
        JsonElement payload, CancellationToken cancellationToken)
    {
        var match = await ResolvePromptMatchAsync(session, prompt, GetExplicitExtensionActionId(payload), cancellationToken).ConfigureAwait(false);
        if (payload.TryGetProperty("deepResearch", out var research) && research.ValueKind == JsonValueKind.True)
        {
            if (match is not null && match.Trigger.Action is not (PromptTriggerAction.Coding or PromptTriggerAction.WebSearch))
                throw new ArgumentException("Deep Research ist mit General oder Coding nutzbar. Wähle andere einmalige Tools zuerst ab.");
            var profile = GetOptionalString(payload, "deepResearchProfile", 64)?.Trim() ?? "auto";
            if (!DeepResearchProfiles.IsValid(profile)) throw new ArgumentException("Das ausgewählte Deep-Research-Profil ist unbekannt.");
            match = (match ?? CreateToolMatch("webSearch", prompt)) with { DeepResearch = true, DeepResearchProfile = profile };
        }
        return match;
    }

    private async Task ResumePendingInBackgroundAsync(Func<string, object, string?, Task> emit,
        string requestId, CancellationToken cancellationToken, Task? previous = null)
    {
        try
        {
            if (previous is not null) await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
            await missumAi!.StopPersistedRunsAtStartupAsync(cancellationToken).ConfigureAwait(false);
            await missumAi!.ResumePendingAsync(update => EmitMissumAiUpdateAsync(update, emit, requestId), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (MissumAiStreamDetachedException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { await emit("host.error", new { message = exception.Message }, requestId).ConfigureAwait(false); }
            catch (Exception bridgeException) when (bridgeException is not OutOfMemoryException) { }
        }
    }

    internal async Task<object> BuildSessionSidebarSnapshotAsync(bool groupingCompleted, CancellationToken cancellationToken = default)
    {
        var mode = CurrentSettings.SelectedChatMode;
        var sessions = await chats.ListSessionsAsync(mode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var groups = await chats.ListSessionGroupsAsync(mode, cancellationToken).ConfigureAwait(false);
        // Grouping never owns the active conversation. The user may switch sessions,
        // type a draft or continue an AI run before this independent request finishes.
        return new
        {
            groupingCompleted,
            productName = runtimeProfile?.ProductName ?? "AI Assistent",
            assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent",
            productIdentity = new { assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent" },
            chatMode = ChatModeName(mode),
            selectedChatMode = ChatModeName(mode),
            sessions = sessions.Select(ToSessionDto),
            sessionGroups = groups.Select(group => new
            {
                group.Id,
                group.Name,
                group.IsCollapsed,
                group.CreatedAt,
                group.WorkspacePath,
                chatMode = ChatModeName(group.ChatMode),
                sessionIds = sessions.Where(session => session.SessionGroupId == group.Id).Select(session => session.Id),
            }),
        };
    }

    internal static PromptTriggerMatch CreateToolMatch(string toolAction, string prompt) =>
        CreateExtensionMatch(LegacyExtensionActionIdFor(toolAction), prompt);

    internal static PromptTriggerMatch CreateExtensionMatch(string extensionActionId, string prompt)
    {
        PromptActionExtensionIds.EnsureValid(extensionActionId);
        var action = PromptActionExtensionIds.TryGetPromptAction(extensionActionId, out var builtInAction)
            ? builtInAction
            : PromptTriggerAction.Extension;
        var now = DateTimeOffset.UtcNow;
        var trigger = new PromptTrigger(
            Guid.Empty,
            action,
            extensionActionId,
            "Einmalig über das Prompt-Tools-Menü ausgewählt.",
            PromptTriggerMatchMode.Exact,
            true,
            int.MaxValue,
            0,
            now,
            now,
            extensionActionId);
        return new PromptTriggerMatch(trigger, prompt, prompt.Trim());
    }

    private async Task<PromptTriggerMatch?> ResolvePromptMatchAsync(
        ChatSession session,
        string prompt,
        string? explicitExtensionActionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitExtensionActionId))
        {
            if (!_extensionActions.TryGetAction(explicitExtensionActionId, out var descriptor) || descriptor is null
                || descriptor.ActionKind != ExtensionActionKind.SelectableTool)
                throw new InvalidOperationException("Die ausgewählte Erweiterungsaktion ist nicht als Modellwerkzeug verfügbar.");
            var mode = ToExtensionChatMode(session.ChatMode);
            if (!descriptor.SupportedChatModes.Contains(mode))
                throw new InvalidOperationException("Die ausgewählte Erweiterungsaktion unterstützt diesen Chatmodus nicht.");
            if (!string.IsNullOrWhiteSpace(descriptor.DisabledReason))
                throw new InvalidOperationException(descriptor.DisabledReason);
            if (!explicitExtensionActionId.StartsWith("builtin.", StringComparison.Ordinal))
            {
                if (extensionRuntime is null)
                    throw new InvalidOperationException("Der ExtensionHost ist für das ausgewählte Modellwerkzeug nicht verfügbar.");
                var groups = await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false);
                var disabledReason = extensionRuntime.GetDisabledReason(
                    explicitExtensionActionId,
                    ResolveSessionWorkspaceRoot(session, groups));
                if (disabledReason is not null) throw new InvalidOperationException(disabledReason);
            }
            var match = CreateExtensionMatch(explicitExtensionActionId, prompt);
            if (session.ChatMode == ChatMode.Coding && match.Trigger.Action == PromptTriggerAction.Extension)
                match = match with { Trigger = match.Trigger with { Action = PromptTriggerAction.Coding } };
            return match;
        }

        var databaseMatch = await promptTriggers.MatchAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (databaseMatch?.Trigger.Action == PromptTriggerAction.TextToSpeech)
        {
            return databaseMatch;
        }
        if (databaseMatch is not null)
        {
            return databaseMatch;
        }
        if (string.Equals(
            session.PersistentExtensionActionId,
            BuiltInActionIds.CreateAudiobook,
            StringComparison.Ordinal))
        {
            return CreateToolMatch("audiobook", prompt);
        }
        if (string.Equals(
            session.PersistentExtensionActionId,
            BuiltInActionIds.PlanMode,
            StringComparison.Ordinal))
        {
            return CreateExtensionMatch(BuiltInActionIds.PlanMode, prompt);
        }
        return session.ChatMode is ChatMode.Coding or ChatMode.ClaudeScience
            ? CreateToolMatch("coding", prompt)
            : null;
    }

    private async Task<bool> HasAudiobookContentAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        ConversationCursor? before = null;
        while (true)
        {
            var page = await chats.ListContextMessagesAsync(sessionId, 256, before, cancellationToken).ConfigureAwait(false);
            if (page.Any(static message => message.Role == ChatRole.Assistant
                && message.ContentProfile == MessageContentProfile.Audiobook && !string.IsNullOrWhiteSpace(message.Content)
                && message.Status is MessageStatus.Completed or MessageStatus.Cancelled or MessageStatus.Interrupted)) return true;
            if (page.Count < 256) return false;
            before = new(page[0].CreatedAt, page[0].Id);
        }
    }

    private static bool IsAudiobookContinuationRequest(string prompt, PromptTriggerMatch? match)
    {
        if (match?.Trigger.Action != PromptTriggerAction.Audiobook)
        {
            return false;
        }
        var normalized = prompt.Trim();
        return normalized.StartsWith("Hörbuch fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Hoerbuch fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Fortsetzen:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PersistentExtensionActionIdFor(PromptTriggerAction? action) => action switch
    {
        PromptTriggerAction.Audiobook => BuiltInActionIds.CreateAudiobook,
        PromptTriggerAction.PlanMode => BuiltInActionIds.PlanMode,
        _ => null,
    };

    private static string? LegacyToolAliasFor(string? extensionActionId) => extensionActionId switch
    {
        BuiltInActionIds.CreateAudiobook => "audiobook",
        BuiltInActionIds.PlanMode => "planMode",
        _ => null,
    };

    internal static bool IsCodingAgentSession(ChatMode? mode) => mode is ChatMode.Coding or ChatMode.ClaudeScience;

    private static ExtensionChatMode ToExtensionChatMode(ChatMode mode) => mode switch
    {
        ChatMode.General => ExtensionChatMode.General,
        ChatMode.Coding => ExtensionChatMode.Coding,
        ChatMode.ClaudeScience => ExtensionChatMode.ClaudeScience,
        _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
    };

    private static string ChatModeName(ChatMode mode) => mode switch
    {
        ChatMode.General => "general",
        ChatMode.Coding => "coding",
        ChatMode.ClaudeScience => "claudescience",
        _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
    };

    internal static ChatMode ParseChatMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "general" => ChatMode.General,
        "coding" => ChatMode.Coding,
        "claudescience" or "claude-science" or "science" => ChatMode.ClaudeScience,
        _ => throw new ArgumentException("Die angeforderte Chatansicht ist unbekannt."),
    };

    internal async Task EmitMissumAiUpdateAsync(
        MissumAiAssistantUpdate update,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        // The UI can request an expanded step reentrantly from emit, before
        // write-behind acknowledges this event. Publish its full source first.
        ObserveLiveToolStep(update);
        if (update.ToolStep is { Tool: "coding.updatePlan", Status: "completed" } planStep)
            await emit("workingPlan.changed", new { sessionId = update.Message.SessionId, workingPlan = WorkingPlanDto(planStep) },
                requestId).ConfigureAwait(false);
        if (update.Kind == MissumAiAssistantUpdateKind.SubagentChanged && update.Subagent is { } subagent)
        {
            await emit("subagent.snapshot", ToSubagentDto(subagent with { ParentMessageId = update.Message.Id }), requestId).ConfigureAwait(false);
            await emit("chat.delta", ProjectStreamDelta(update), requestId).ConfigureAwait(false);
            return;
        }
        await ObserveDisplayStateAsync(update).ConfigureAwait(false);
        if (update.Kind == MissumAiAssistantUpdateKind.FileChangesChanged)
        {
            if (update.ChangesSummary is { } summary)
                await emit("coding.changes", summary, requestId).ConfigureAwait(false);
            return;
        }
        if (CurrentSettings.IsAutomaticSpeechEnabled)
        {
            missumAi?.ObserveAutomaticSpeech(update,
                speech => emit("speech.status", new
                {
                    sessionId = speech.SessionId,
                    sourceMessageId = speech.SourceMessageId,
                    playbackId = speech.PlaybackId,
                    automatic = speech.IsAutomatic,
                    ownerClientId = "desktop",
                    active = speech.IsActive,
                    status = speech.Status,
                    detail = speech.Detail,
                    model = speech.Model,
                    error = speech.Error,
                    cacheHit = speech.CacheHit,
                }, requestId),
                playback => emit("speech.progress", SpeechPlaybackProgressBridge.ToPayload(playback), requestId));
        }
        _displayStates.TryGetValue(update.Message.SessionId, out var generationDisplay);
        IReadOnlyList<ChatArtifact>? terminalArtifacts = null;
        if (update.Kind is MissumAiAssistantUpdateKind.Completed or MissumAiAssistantUpdateKind.Cancelled or MissumAiAssistantUpdateKind.Failed)
            terminalArtifacts = update.Artifacts ?? await artifacts.ListForMessageAsync(update.Message.Id, CancellationToken.None).ConfigureAwait(false);
        switch (update.Kind)
        {
            case MissumAiAssistantUpdateKind.Started:
                ResetStreamProjection(update.Message);
                var sessionMessages = (await conversationSnapshots.GetPageAsync(update.Message.SessionId,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false))?.Conversation.Messages ?? [];
                var currentSession = await chats.GetSessionAsync(
                    update.Message.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
                var pendingAttachments = await attachments.ListAsync(
                    update.Message.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
                var precedingUserMessage = sessionMessages
                    .TakeWhile(message => message.Id != update.Message.Id)
                    .LastOrDefault(message => message.Role == ChatRole.User);
                var precedingUserArtifacts = precedingUserMessage is null
                    ? null
                    : await artifacts.ListForMessageAsync(precedingUserMessage.Id, CancellationToken.None).ConfigureAwait(false);
                await emit(
                    "conversation.snapshot",
                    await BuildConversationSnapshotAsync(update.Message.SessionId, CancellationToken.None).ConfigureAwait(false),
                    requestId).ConfigureAwait(false);
                await emit("chat.started", new
                {
                    sessionId = update.Message.SessionId,
                    streamGeneration = StreamGeneration(update.Message.Id),
                    conversationRevision = currentSession?.ConversationRevision,
                    session = currentSession is null ? null : ToSessionDto(currentSession),
                    userMessage = precedingUserMessage is null
                        ? null
                        : ToMessageDto(precedingUserMessage, precedingUserArtifacts),
                    message = ToMessageDto(update.Message, update.Artifacts
                        ?? await artifacts.ListForMessageAsync(update.Message.Id, CancellationToken.None).ConfigureAwait(false)),
                    runId = missumAi?.ActiveRunId,
                    changesRunId = update.LocalRunId,
                    contextUsed = update.ContextUsed,
                    contextSource = update.ContextSource,
                    contextLimit = update.ContextLimit,
                    contextWasTruncated = update.ContextWasCompacted,
                    runStatus = update.Status,
                    runDetail = update.Detail,
                    generationState = generationDisplay?.GenerationState,
                    generatedTokens = generationDisplay?.GeneratedTokens,
                    generationUpdatedAt = generationDisplay?.GenerationUpdatedAt,
                    processedPromptTokens = generationDisplay?.ProcessedPromptTokens,
                    totalPromptTokens = generationDisplay?.TotalPromptTokens,
                    promptProgress = generationDisplay?.PromptProgress,
                    model = update.Model,
                    loadedFiles = update.LoadedFiles,
                    attachments = pendingAttachments.Select(ToAttachmentDto),
                }, requestId).ConfigureAwait(false);
                break;
            case MissumAiAssistantUpdateKind.Delta:
                // The service owns persistence of this update. The UI accepts the
                // live projection; rereading the entire conversation per fragment
                // duplicates delivery and makes streaming cost grow with history.
                await emit("chat.delta", ProjectStreamDelta(update), requestId).ConfigureAwait(false);
                break;
            case MissumAiAssistantUpdateKind.Status:
                await emit("status.changed", new
                {
                    messageId = update.Message.Id,
                    sessionId = update.Message.SessionId,
                    runStatus = update.Status,
                    runDetail = update.Detail,
                    generationState = generationDisplay?.GenerationState,
                    generatedTokens = generationDisplay?.GeneratedTokens,
                    generationUpdatedAt = generationDisplay?.GenerationUpdatedAt,
                    processedPromptTokens = generationDisplay?.ProcessedPromptTokens,
                    totalPromptTokens = generationDisplay?.TotalPromptTokens,
                    promptProgress = generationDisplay?.PromptProgress,
                    model = update.Model,
                    contextUsed = update.ContextUsed,
                    contextSource = update.ContextSource,
                    toolStep = LiveDisplayToolStep(update.ToolStep),
                    sourceUpdates = LiveSourcesFor(update.ToolStep),
                    toolStepOrdinal = StreamStepOrdinal(update), toolStepCount = update.Message.ToolSteps?.Count,
                    revision = NextStreamRevision(update.Message.Id),
                    streamGeneration = StreamGeneration(update.Message.Id),
                    runId = missumAi?.ActiveRunId,
                    contextLimit = update.ContextLimit,
                    contextWasTruncated = update.ContextUsed.HasValue ? update.ContextWasCompacted : (bool?)null,
                    loadedFiles = update.LoadedFiles,
                }, requestId).ConfigureAwait(false);
                break;
            case MissumAiAssistantUpdateKind.ArtifactsChanged:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                break;
            case MissumAiAssistantUpdateKind.DocumentsChanged:
                await emit("session.changed", await BuildSnapshotAsync(CancellationToken.None), requestId).ConfigureAwait(false);
                break;
            case MissumAiAssistantUpdateKind.Completed:

                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId, terminalArtifacts).ConfigureAwait(false);
                await emit("chat.completed", new
                {
                    sessionId = update.Message.SessionId,
                    message = ToMessageDto(update.Message, terminalArtifacts),
                    session = update.Session is null ? null : ToSessionDto(update.Session),
                    runStatus = update.Status,
                    runDetail = update.Detail,
                }, requestId).ConfigureAwait(false);
                break;
            case MissumAiAssistantUpdateKind.Cancelled:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId, terminalArtifacts).ConfigureAwait(false);
                await emit("chat.cancelled", new
                {
                    sessionId = update.Message.SessionId,
                    message = ToMessageDto(update.Message, terminalArtifacts),
                    runStatus = update.Status,
                }, requestId).ConfigureAwait(false);
                break;
            case MissumAiAssistantUpdateKind.Failed:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId, terminalArtifacts).ConfigureAwait(false);
                await emit("chat.failed", new
                {
                    sessionId = update.Message.SessionId,
                    message = ToMessageDto(update.Message, terminalArtifacts),
                    error = update.Error,
                    runStatus = update.Status,
                }, requestId).ConfigureAwait(false);
                break;
        }
    }

    private static bool IsCancelCommand(string prompt) =>
        prompt.Trim(' ', '.', ',', '!', '?').Equals("abbrechen", StringComparison.OrdinalIgnoreCase);

    private async Task EmitPublicationAsync(WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit, string? selectedProjectId, CancellationToken cancellationToken)
    {
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        var projectId = "research-" + sessionId.ToString("N");
        if (selectedProjectId is not null && selectedProjectId != projectId)
            throw new UnauthorizedAccessException("Die Publikation gehört nicht zu dieser Sitzung.");
        var workspace = session.CodingWorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) && session.SessionGroupId is { } groupId)
            workspace = (await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(group => group.Id == groupId)?.WorkspacePath;
        var available = publicationService is not null && !string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace);
        object[] projects = [];
        object? detail = null;
        if (available)
        {
            presentation?.RegisterWorkspace(projectId, workspace!, session.Title);
            var markdown = await publicationService!.EnsureWorkspaceDocumentAsync(projectId, workspace!, session.Title, cancellationToken).ConfigureAwait(false);
            var project = new { id = projectId, title = session.Title, workspacePath = workspace, markdownPath = markdown };
            projects = [project];
            if (selectedProjectId is not null) { detail = new { project }; presentation?.Queue(projectId); }
        }
        await emit("publication.snapshot", new { available, sessionId, projects, selectedProjectId, detail,
            disabledReason = available ? null : "Wähle einen vorhandenen Projektordner für die Publikation." }, envelope.RequestId).ConfigureAwait(false);
    }

    private async Task EmitProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        string messageType,
        CancellationToken cancellationToken)
    {
        var session = await ResolveProjectMemorySessionAsync(envelope, cancellationToken).ConfigureAwait(false);
        var search = string.Equals(envelope.Type, "memory.list", StringComparison.Ordinal)
            ? GetOptionalString(envelope.Payload, "search", 200)
            : null;
        await emit(
            messageType,
            await BuildProjectMemoryPayloadAsync(session, search, cancellationToken).ConfigureAwait(false),
            envelope.RequestId).ConfigureAwait(false);
    }

    private async Task CreateProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        _ = await store.CreateAsync(new(
            resolved.Scope,
            ParseProjectMemoryKind(GetRequiredString(envelope.Payload, "kind", 40)),
            GetRequiredString(envelope.Payload, "content", 100_000),
            GetOptionalString(envelope.Payload, "source", 2_000)?.Trim() ?? "manual"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        _ = await store.UpdateAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            new(
                ParseProjectMemoryKind(GetRequiredString(envelope.Payload, "kind", 40)),
                GetRequiredString(envelope.Payload, "content", 100_000),
                GetOptionalString(envelope.Payload, "source", 2_000)?.Trim() ?? entry.Source,
                entry.LastConfirmedAt),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task PinProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        _ = await store.SetPinnedAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            GetRequiredBoolean(envelope.Payload, "isPinned"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        _ = await store.ConfirmAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetProjectMemoryAutoCaptureAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        _ = await store.SetAutoCaptureAsync(
            resolved.Scope,
            GetRequiredBoolean(envelope.Payload, "enabled"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitProjectMemoryChangedAsync(
        ChatSession session,
        string requestId,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken) =>
        await emit(
            "memory.changed",
            await BuildProjectMemoryPayloadAsync(session, null, cancellationToken).ConfigureAwait(false),
            requestId).ConfigureAwait(false);

    private async Task<object> BuildProjectMemoryPayloadAsync(
        ChatSession session,
        string? search,
        CancellationToken cancellationToken)
    {
        if (projectMemory is null)
            return UnavailableProjectMemoryPayload(
                session.Id,
                "Das Projektgedächtnis ist in dieser Installation nicht verfügbar.");

        var resolved = await ResolveProjectMemoryScopeAsync(session, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
            return UnavailableProjectMemoryPayload(session.Id, "Öffne zuerst ein Projekt oder einen Workspace.");

        var settingsValue = await projectMemory.GetSettingsAsync(resolved.Scope, cancellationToken).ConfigureAwait(false);
        var entries = await projectMemory.ListAsync(resolved.Scope, search, cancellationToken).ConfigureAwait(false);
        return new
        {
            sessionId = session.Id,
            available = true,
            projectKey = resolved.Scope.ProjectKey,
            projectId = resolved.Scope.ProjectId,
            projectLabel = resolved.Label,
            autoCaptureEnabled = settingsValue.AutoCaptureEnabled,
            entries = entries.Select(ToProjectMemoryEntryDto),
            disabledReason = (string?)null,
        };
    }

    private static object UnavailableProjectMemoryPayload(Guid sessionId, string disabledReason) => new
    {
        sessionId,
        available = false,
        projectKey = (string?)null,
        projectId = (string?)null,
        projectLabel = "Projektgedächtnis",
        autoCaptureEnabled = false,
        entries = Array.Empty<object>(),
        disabledReason,
    };

    private async Task<ChatSession> ResolveProjectMemorySessionAsync(
        WebBridgeEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        return await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung des Projektgedächtnisses wurde nicht gefunden.");
    }

    private async Task<(IProjectMemoryStore Store, ChatSession Session, ResolvedProjectMemory Resolved)> RequireProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var store = projectMemory
            ?? throw new InvalidOperationException("Das Projektgedächtnis ist in dieser Installation nicht verfügbar.");
        var session = await ResolveProjectMemorySessionAsync(envelope, cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveProjectMemoryScopeAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Öffne zuerst ein Projekt oder einen Workspace.");
        return (store, session, resolved);
    }

    private async Task<ResolvedProjectMemory?> ResolveProjectMemoryScopeAsync(
        ChatSession session,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(session.CodingWorkspacePath))
        {
            var fullPath = Path.GetFullPath(session.CodingWorkspacePath);
            return new(
                ProjectMemoryScope.FromWorkspace(fullPath),
                WorkspaceLabel(fullPath));
        }

        if (session.SessionGroupId is not { } groupId) return null;
        var group = (await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return null;
        return !string.IsNullOrWhiteSpace(group.WorkspacePath)
            ? new(
                ProjectMemoryScope.FromWorkspace(group.WorkspacePath, group.Id.ToString("D")),
                group.Name)
            : new(ProjectMemoryScope.FromProjectId(group.Id.ToString("D")), group.Name);
    }

    private static async Task<ProjectMemoryEntry> GetScopedProjectMemoryEntryAsync(
        IProjectMemoryStore store,
        ProjectMemoryScope scope,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var entry = await store.GetAsync(entryId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Der Gedächtniseintrag wurde nicht gefunden.");
        if (!string.Equals(entry.ProjectKey, scope.ProjectKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Der Gedächtniseintrag gehört nicht zum aktuellen Projekt.");
        return entry;
    }

    private static object ToProjectMemoryEntryDto(ProjectMemoryEntry entry) => new
    {
        id = entry.Id,
        projectKey = entry.ProjectKey,
        projectId = entry.ProjectId,
        kind = ProjectMemoryKindName(entry.Kind),
        entry.Content,
        entry.Source,
        entry.Version,
        entry.CreatedAt,
        entry.UpdatedAt,
        entry.LastConfirmedAt,
        entry.IsPinned,
    };

    private static ProjectMemoryKind ParseProjectMemoryKind(string value) =>
        value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim().ToLowerInvariant() switch
        {
            "requirement" => ProjectMemoryKind.Requirement,
            "decision" => ProjectMemoryKind.Decision,
            "milestone" => ProjectMemoryKind.Milestone,
            "errorresolution" => ProjectMemoryKind.ErrorResolution,
            "verification" => ProjectMemoryKind.Verification,
            _ => throw new InvalidOperationException("Die Art des Gedächtniseintrags ist unbekannt."),
        };

    private static string ProjectMemoryKindName(ProjectMemoryKind kind) => kind switch
    {
        ProjectMemoryKind.Requirement => "requirement",
        ProjectMemoryKind.Decision => "decision",
        ProjectMemoryKind.Milestone => "milestone",
        ProjectMemoryKind.ErrorResolution => "errorResolution",
        ProjectMemoryKind.Verification => "verification",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Die Art des Gedächtniseintrags ist unbekannt."),
    };

    private static string WorkspaceLabel(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static string? ResolveSessionWorkspaceRoot(
        ChatSession session,
        IReadOnlyList<ChatSessionGroup> groups)
    {
        if (!string.IsNullOrWhiteSpace(session.CodingWorkspacePath))
            return Path.GetFullPath(session.CodingWorkspacePath);
        if (session.SessionGroupId is not { } groupId) return null;
        var workspace = groups.FirstOrDefault(group => group.Id == groupId)?.WorkspacePath;
        return string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace);
    }

    private sealed record ResolvedProjectMemory(ProjectMemoryScope Scope, string Label);

    private static object ToSessionDto(ChatSession session) => new
    {
        id = session.Id,
        session.Title,
        session.CreatedAt,
        session.UpdatedAt,
        chatMode = ChatModeName(session.ChatMode),
        workspacePath = session.CodingWorkspacePath,
        persistentExtensionActionId = session.PersistentExtensionActionId,
        sessionGroupId = session.SessionGroupId,
        session.ConversationRevision,
    };

    private static object ToRunQueueDto(AssistantRunScheduleSnapshot snapshot) => new
    {
        active = snapshot.Active is null ? null : ToScheduledRunDto(snapshot.Active),
        pending = snapshot.Pending.Select(ToScheduledRunDto),
        snapshot.QueueDepth,
        snapshot.IsIdle,
    };

    private static object ToScheduledRunDto(AssistantScheduledRun run) => new
    {
        run.TicketId,
        run.SessionId,
        run.RequestId,
        state = run.State.ToString().ToLowerInvariant(),
        run.Position,
        run.EnqueuedAt,
        run.StartedAt,
    };

    private static object ToMessageDto(ChatMessage message, IReadOnlyList<ChatArtifact>? messageArtifacts = null,
        ConversationToolStepWindow? toolWindow = null)
    {
        return new
        {
            id = message.Id,
            sessionId = message.SessionId,
            role = message.Role.ToString().ToLowerInvariant(),
            message.Content,
            status = message.Status.ToString().ToLowerInvariant(),
            message.CreatedAt,
            message.UpdatedAt,
            message.Error,
            message.ContextSummary,
            contentProfile = message.ContentProfile.ToString().ToLowerInvariant(),
            message.Revision,
            tool = message.ToolExecution,
            toolSteps = (message.ToolSteps ?? []).TakeLast(64).Select(DisplayToolStep),
            toolStepWindow = toolWindow ?? new ConversationToolStepWindow(message.ToolSteps?.Count ?? 0,
                message.ToolSteps?.Count > 64, message.ToolSteps?.Count > 64 ? message.ToolSteps.Count - 64 : null),
            artifacts = AssistantArtifactOriginalResolver.DisplayArtifacts(messageArtifacts ?? [], message.ToolSteps)
                .Select(artifact => ToArtifactDtoWithPosition(artifact,
                    message.ToolSteps?.FirstOrDefault(step => step.Id == artifact.StepId)?.ContentOffset)),
        };
    }

    private static AssistantToolStep DisplayToolStep(AssistantToolStep step) => step with
    {
        DisplayHeader = AssistantToolStepHeaders.Create(step),
        Detail = step.Tool is "assistant.narration" or "assistant.progress" or "assistant.steering" ? step.Detail : null,
        InputJson = null, PreviewHtml = null,
        OutputJson = step.Tool is "assistant.continuation" or "subagent" or "subagent.completed"
            && step.OutputJson?.Length <= 32768 ? step.OutputJson : null,
        Explanation = step.Explanation is { Length: > 400 } explanation ? explanation[..400] : step.Explanation,
        DetailsDeferred = step.Tool is not ("assistant.narration" or "assistant.progress" or "assistant.steering"),
    };

    private static object ToArtifactDto(ChatArtifact artifact) => ToArtifactDtoWithPosition(artifact, null);

    private static object ToArtifactDtoWithPosition(ChatArtifact artifact, int? offset) => new
    {
        id = artifact.Id,
        artifact.FileName,
        contentType = artifact.ContentType,
        artifact.Length,
        artifact.Provider,
        artifact.CreatedAt,
        artifact.Metadata,
        stepId = artifact.StepId,
        contentOffset = offset ?? (int.TryParse(artifact.Metadata?.GetValueOrDefault("sourceContentOffset"),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var position) ? (int?)position : null),
    };

    private static object ToDocumentDto(StoredDocument document) => new
    {
        id = document.Id,
        document.FileName,
        document.ContentType,
        document.Length,
        document.PageCount,
        document.CreatedAt,
        preparationStatus = document.PreparationStatus.ToString().ToLowerInvariant(),
        preparationProgress = document.PreparationProgress,
        cacheHit = document.WasReused,
        preparationError = document.PreparationError,
    };

    private static object BuildDocumentGroupStatus(IReadOnlyList<StoredDocument> documents, int readyAttachments)
    {
        var ready = documents.Count(static item => item.PreparationStatus == DocumentPreparationStatus.Ready) + readyAttachments;
        var failed = documents.Count(static item => item.PreparationStatus == DocumentPreparationStatus.Failed);
        var processing = documents.Count - (ready - readyAttachments) - failed;
        return new
        {
            total = documents.Count + readyAttachments,
            ready,
            processing,
            failed,
            status = failed > 0 ? "failed" : processing > 0 ? "processing" : "ready",
        };
    }

    private static object ToAttachmentDto(AssistantAttachment attachment) => new
    {
        id = attachment.Id,
        attachment.FileName,
        contentType = attachment.ContentType,
        attachment.Length,
        attachment.CreatedAt,
    };

    private static Guid GetRequiredGuid(JsonElement payload, string name)
    {
        return GetOptionalGuid(payload, name)
            ?? throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
    }

    private static Guid? GetOptionalGuid(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
               && Guid.TryParse(property.GetString(), out var result)
            ? result
            : null;
    }

    private static string GetRequiredString(JsonElement payload, string name, int maximumLength)
    {
        var value = GetOptionalString(payload, name, maximumLength);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"'{name}' darf nicht leer sein.")
            : value.Trim();
    }

    private static string? GetOptionalString(JsonElement payload, string name, int maximumLength)
    {
        if (!payload.TryGetProperty(name, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"'{name}' muss Text sein.");
        }

        var value = property.GetString() ?? string.Empty;
        return value.Length <= maximumLength
            ? value
            : throw new InvalidOperationException($"'{name}' ist zu lang.");
    }

    private static long GetRequiredInt64(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
    }

    private static bool GetRequiredBoolean(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
    }

    private static string GetRequiredJsonString(JsonElement payload, string name)
    {
        var json = GetRequiredString(payload, name, 1_000_000);
        using var _ = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        return json;
    }

    private static string[] GetStringArray(
        JsonElement payload,
        string name,
        int maximumItems,
        int maximumItemLength)
    {
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"'{name}' muss eine Liste sein.");
        }

        var values = property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length > maximumItems || values.Any(item => item.Length > maximumItemLength))
        {
            throw new InvalidOperationException($"'{name}' überschreitet das Größenlimit.");
        }

        return values;
    }
}
