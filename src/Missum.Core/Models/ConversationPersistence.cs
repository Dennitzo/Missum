namespace Missum.Core.Models;

/// <summary>Display-only tool pagination, independent of the complete model transcript.</summary>
public sealed record ConversationToolStepWindow(int TotalSteps, bool HasMore, long? BeforeOrdinal,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool RequiresLegacyNormalization = false);

public sealed record ConversationToolStepPage(Guid MessageId, IReadOnlyList<AssistantToolStep> Steps,
    ConversationToolStepWindow Window);

/// <summary>A replay-safe client checkpoint. Its cursor is committed with the corresponding visible state.</summary>
public sealed record ConversationStreamBatch(Guid MessageId, string Content, MessageStatus Status,
    IReadOnlyList<AssistantToolStep> ToolSteps, Guid LocalRunId, string ServerRunId, long LastEventId,
    string RunState, string? SelectedModel = null, string? ErrorCode = null, DateTimeOffset? SourceUpdatedAt = null);
