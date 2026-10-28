using Missum.Core.Models;

namespace Missum.Core.Contracts;

public interface IMissumDatabase
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default);
}

public interface IChatRepository
{
    Task<IReadOnlyList<ChatSession>> ListSessionsAsync(string? search = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatSession>> ListSessionsAsync(ChatMode mode, string? search = null, CancellationToken cancellationToken = default);
    Task<ChatSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ChatSession> CreateSessionAsync(string title, CancellationToken cancellationToken = default);
    Task<ChatSession> CreateSessionAsync(string title, ChatMode mode, CancellationToken cancellationToken = default);
    Task RenameSessionAsync(Guid id, string title, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<int> DeleteSessionsAsync(ChatMode mode, CancellationToken cancellationToken = default);
    Task SaveDraftAsync(Guid id, string draft, CancellationToken cancellationToken = default);
    Task ClearDraftIfMatchesAsync(Guid id, string expectedDraft, CancellationToken cancellationToken = default);
    Task SetPersistentExtensionActionIdAsync(
        Guid id,
        string? extensionActionId,
        CancellationToken cancellationToken = default);
    Task SetCodingWorkspacePathAsync(Guid id, string? path, CancellationToken cancellationToken = default);
    Task SetCodingWorkspacePathAsync(Guid id, string? path, bool activateCoding, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatSessionGroup>> ListSessionGroupsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatSessionGroup>> ListSessionGroupsAsync(ChatMode mode, CancellationToken cancellationToken = default);
    Task<ChatSessionGroup> GetOrCreateSessionGroupForWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default);
    Task<ChatSessionGroup> GetOrCreateSessionGroupForWorkspaceAsync(
        string workspacePath,
        ChatMode mode,
        CancellationToken cancellationToken = default);
    Task ApplySessionGroupingAsync(IReadOnlyList<SessionGroupAssignment> groups, CancellationToken cancellationToken = default);
    Task SetSessionGroupCollapsedAsync(Guid groupId, bool collapsed, CancellationToken cancellationToken = default);
    Task DeleteEmptySessionGroupAsync(Guid groupId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ChatMessage?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
    async Task<ChatMessage?> GetMessageTextAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        await GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false) is { } message ? message with { ToolSteps = [] } : null;
    async Task<ChatMessage?> GetFirstUserMessageAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        (await ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(message => message.Role == ChatRole.User);
    async Task<IReadOnlyList<ChatMessage>> ListContextMessagesAsync(Guid sessionId, int maximumMessages = 256,
        ConversationCursor? before = null, CancellationToken cancellationToken = default) =>
        (await ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .Where(message => before is null || message.CreatedAt < before.CreatedAt || message.CreatedAt == before.CreatedAt && message.Id.CompareTo(before.Id) < 0)
            .TakeLast(Math.Clamp(maximumMessages, 1, 512)).Select(message => message with { ToolSteps = [] }).ToArray();
    Task<ChatTurn> AddTurnAsync(
        Guid sessionId,
        string userContent,
        MessageContentProfile assistantContentProfile = MessageContentProfile.General,
        CancellationToken cancellationToken = default);
    Task<ChatMessage> AddMessageAsync(
        Guid sessionId,
        ChatRole role,
        string content,
        MessageStatus status,
        MessageContentProfile contentProfile = MessageContentProfile.General,
        CancellationToken cancellationToken = default);
    Task<int> DeleteEmptyTerminalMessagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task ResetMessageForRetryAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task UpdateMessageAsync(Guid messageId, string content, MessageStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task UpdateMessageWithToolStepsAsync(Guid messageId, string content, MessageStatus status, IReadOnlyList<AssistantToolStep> toolSteps, CancellationToken cancellationToken = default);
    Task SetMessageContextSummaryAsync(Guid messageId, string contextSummary, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssistantToolStep>> SaveToolStepAsync(Guid messageId, AssistantToolStep toolStep, CancellationToken cancellationToken = default);
    async Task<AssistantToolStep> SaveToolStepDeltaAsync(Guid messageId, AssistantToolStep toolStep,
        CancellationToken cancellationToken = default) =>
        (await SaveToolStepAsync(messageId, toolStep, cancellationToken).ConfigureAwait(false)).First(step => step.Id == toolStep.Id);
    Task NormalizeToolStepsAsync(Guid messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<SessionContextPreparation?> GetSessionContextPreparationAsync(string cacheKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionContextPreparation>> ListSessionContextPreparationsAsync(
        Guid sessionId,
        string modelId,
        int maximumMessageCount,
        SessionContextProfile profile = SessionContextProfile.General,
        CancellationToken cancellationToken = default);
    Task SaveSessionContextPreparationAsync(SessionContextPreparation preparation, CancellationToken cancellationToken = default);
    Task<int> MarkStreamingMessagesInterruptedAsync(CancellationToken cancellationToken = default);
}

public interface IConversationSnapshotRepository
{
    Task<ConversationSnapshot?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ConversationPage?> GetPageAsync(Guid sessionId, int pageSize = 24, ConversationCursor? before = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationPrompt>> GetPromptsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationPrompt>>([]);
    Task<ConversationPage?> GetPromptPageAsync(Guid sessionId, Guid promptId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ConversationPage?>(null);
    Task<AssistantToolStep?> GetToolStepAsync(Guid sessionId, Guid messageId, string stepId,
        CancellationToken cancellationToken = default);
    // Separate from the paged chat: a plan can predate every currently visible message.
    Task<AssistantToolStep?> GetWorkingPlanAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<AssistantToolStep?>(null);
    Task<AssistantToolStep?> GetWorkingPlanAtAsync(Guid sessionId, Guid messageId, string stepId,
        CancellationToken cancellationToken = default) => Task.FromResult<AssistantToolStep?>(null);
    async Task<AssistantToolStep?> GetSubagentReceiptAsync(Guid sessionId, Guid messageId, string agentId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        foreach (var step in snapshot?.Messages.FirstOrDefault(message => message.Id == messageId)?.ToolSteps?.Reverse() ?? [])
        {
            if (step.Tool != "subagent" || step.OutputJson is null) continue;
            using var document = System.Text.Json.JsonDocument.Parse(step.OutputJson);
            if (document.RootElement.TryGetProperty("agentId", out var value) && value.GetString() == agentId) return step;
        }
        return null;
    }
    Task<IReadOnlyList<ConversationSource>> GetSourcesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<ChatMessage>> GetAuxiliaryMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        (await GetAsync(sessionId, cancellationToken).ConfigureAwait(false))?.Messages
            .Select(message => message with { Content = "", ToolSteps = (message.ToolSteps ?? [])
                .Where(step => step.Tool is "subagent" or "assistant.continuation").ToArray() })
            .Where(message => message.ToolSteps is { Count: > 0 }).ToArray() ?? [];
    async Task<ConversationToolStepPage?> GetToolStepsPageAsync(Guid sessionId, Guid messageId, int pageSize = 64,
        long? beforeOrdinal = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var steps = snapshot?.Messages.FirstOrDefault(message => message.Id == messageId)?.ToolSteps;
        if (steps is null) return null;
        var end = (int)Math.Min(beforeOrdinal ?? steps.Count, steps.Count);
        var start = Math.Max(0, end - Math.Clamp(pageSize, 1, 128));
        return new(messageId, steps.Skip(start).Take(end - start).ToArray(), new(steps.Count, start > 0, start));
    }
}

public interface IStreamingConversationRepository
{
    Task CommitStreamAsync(ConversationStreamBatch batch, CancellationToken cancellationToken = default);
}

public interface IPromptTriggerRepository
{
    Task<IReadOnlyList<PromptTrigger>> ListAsync(CancellationToken cancellationToken = default);
    Task<PromptTrigger?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PromptTrigger> CreateAsync(PromptTrigger trigger, CancellationToken cancellationToken = default);
    Task<PromptTrigger> UpdateAsync(PromptTrigger trigger, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task<PromptTriggerMatch?> MatchAsync(string prompt, CancellationToken cancellationToken = default);
}

public interface IAssistantAttachmentRepository
{
    Task<IReadOnlyList<AssistantAttachment>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<AssistantAttachment> ImportAsync(Guid sessionId, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default);
    Task<AssistantAttachment?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IChatArtifactRepository
{
    Task<IReadOnlyList<ChatArtifact>> ListForMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>>> ListForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ChatArtifact?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ChatArtifact> ImportAsync(
        Guid messageId,
        string serverArtifactId,
        string fileName,
        string contentType,
        string sha256,
        long length,
        string provider,
        string? stepId,
        IReadOnlyDictionary<string, string>? metadata,
        Stream content,
        CancellationToken cancellationToken = default);
}

public interface IGeneratedDocumentRepository
{
    Task<IReadOnlyList<GeneratedDocument>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<GeneratedDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GeneratedDocument> CreateAsync(GeneratedDocument document, CancellationToken cancellationToken = default);
    Task<GeneratedDocument> UpdateAsync(
        Guid id,
        string sourceMarkdown,
        string sha256,
        long expectedRevision,
        CancellationToken cancellationToken = default);
    /// <summary>Commits a new/updated document and its already imported artifact blob atomically. Null expectedRevision creates a document.</summary>
    Task<(GeneratedDocument Document, ChatArtifact Artifact)> CommitWithArtifactAsync(
        GeneratedDocument document,
        long? expectedRevision,
        ChatArtifact artifact,
        CancellationToken cancellationToken = default);
}

public interface IDocumentFileCodec
{
    IReadOnlySet<string> ReadableExtensions { get; }
    Task<IReadOnlyList<string>> ReadAsync(string path, CancellationToken cancellationToken = default);
    Task WriteDocxAsync(string sourceMarkdown, string outputPath, CancellationToken cancellationToken = default);
    Task WriteXlsxAsync(string sourceMarkdown, string outputPath, CancellationToken cancellationToken = default);
    Task WritePptxAsync(string sourceMarkdown, string outputPath, CancellationToken cancellationToken = default);
}

public interface IMissumAiRunRepository
{
    Task<MissumAiRunRecord> CreateAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default);
    Task<MissumAiRunRecord> BeginAttemptAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default);
    Task<MissumAiRunRecord> BeginContinuationAttemptAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default);
    Task<MissumAiRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MissumAiRunRecord?> GetByServerRunIdAsync(string serverRunId, CancellationToken cancellationToken = default);
    Task<MissumAiRunRecord?> GetByAssistantMessageIdAsync(Guid assistantMessageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MissumAiRunRecord>> ListResumableAsync(CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<MissumAiRunRecord>> ListPendingForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        (await ListResumableAsync(cancellationToken).ConfigureAwait(false)).Where(run => run.SessionId == sessionId).ToArray();
    Task UpdateAsync(
        Guid id,
        string? serverRunId,
        long lastEventId,
        string state,
        string? selectedModel = null,
        string? errorCode = null,
        CancellationToken cancellationToken = default);
    Task RewindEventsAsync(
        Guid id,
        long lastEventId,
        string state,
        string? errorCode = null,
        CancellationToken cancellationToken = default);
}

public interface IClientToolExecutionRepository
{
    Task<ClientToolExecutionRecord?> GetAsync(string proposalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientToolExecutionRecord>> ListPendingSubmissionsAsync(
        Guid localRunId,
        string? serverRunId = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientToolExecutionRecord>> ListIncompleteExecutionsAsync(
        Guid localRunId,
        string serverRunId,
        CancellationToken cancellationToken = default);
    Task<ClientToolExecutionRecord> BeginAsync(ClientToolExecutionRecord execution, CancellationToken cancellationToken = default);
    Task<ClientToolExecutionRecord> CompleteAsync(string proposalId, string resultJson, CancellationToken cancellationToken = default);
    Task MarkSubmittedAsync(string proposalId, CancellationToken cancellationToken = default);
}

public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> ListAsync(ProjectStatus? status = null, CancellationToken cancellationToken = default);
    Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default);
    Task<Project> UpdateAsync(Project project, long expectedRevision, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task RestoreAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChecklistItem>> ListChecklistAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ChecklistItem> SaveChecklistItemAsync(ChecklistItem item, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task MoveChecklistItemAsync(Guid projectId, Guid itemId, int direction, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteChecklistItemAsync(Guid itemId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectAsset>> ListAssetsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectAsset> AddAssetAsync(ProjectAsset asset, CancellationToken cancellationToken = default);
    Task<ProjectAsset> UpdateAssetAsync(ProjectAsset asset, long expectedRevision, CancellationToken cancellationToken = default);
    Task MoveAssetAsync(Guid projectId, Guid itemId, int direction, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAssetAsync(Guid assetId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<AssetThumbnail?> GetAssetThumbnailAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task SaveAssetThumbnailAsync(AssetThumbnail thumbnail, CancellationToken cancellationToken = default);
    Task DeleteAssetThumbnailAsync(Guid assetId, CancellationToken cancellationToken = default);
}

public interface IBinaryObjectStore
{
    Task<BinaryObjectDescriptor> ImportAsync(Stream source, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(Guid id, CancellationToken cancellationToken = default);
    Task ExportAsync(Guid id, Stream destination, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteIfUnreferencedAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IDocumentIngestor
{
    IReadOnlySet<string> SupportedExtensions { get; }
    Task<DocumentIngestResult> ImportAsync(Guid sessionId, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredDocument>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentPage>> ReadPagesAsync(Guid documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentContextHit>> SearchAsync(Guid sessionId, string query, int maximumCharacters = 160_000, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentContextHit>> SearchHybridAsync(Guid sessionId, string query, string embeddingModelId, IReadOnlyList<double> queryEmbedding, int maximumCharacters = 160_000, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentIndexChunk>> ListIndexChunksAsync(Guid sessionId, string embeddingModelId, CancellationToken cancellationToken = default);
    Task SaveEmbeddingsAsync(IReadOnlyList<DocumentChunkEmbedding> embeddings, CancellationToken cancellationToken = default);
    Task<DocumentContextPreparation?> GetContextPreparationAsync(string cacheKey, CancellationToken cancellationToken = default);
    Task SaveContextPreparationAsync(DocumentContextPreparation preparation, CancellationToken cancellationToken = default);
    Task SetContextPreparationStateAsync(Guid sessionId, Guid messageId, DocumentPreparationStatus? status, int progress = 100, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task SaveEvidenceAsync(Guid messageId, IReadOnlyList<DocumentContextHit> evidence, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetEvidenceCitationsAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default);
}

public interface IContextAssembler
{
    ContextBuildResult Build(ContextBuildRequest request);
}

public interface ISettingsStore
{
    string SettingsPath { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISessionLog
{
    event EventHandler<SessionLogEntry>? EntryAdded;
    IReadOnlyList<SessionLogEntry> Snapshot(string? minimumLevel = null, string? category = null, string? search = null);
    void Clear();
    Task ExportAsync(Stream destination, bool asJson, CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    Task<BackupResult> CreateAsync(string destinationPath, CancellationToken cancellationToken = default);
    Task ValidateAsync(string backupPath, CancellationToken cancellationToken = default);
    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
}
