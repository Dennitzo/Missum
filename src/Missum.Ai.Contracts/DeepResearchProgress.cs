using System.Text.Json;

namespace Missum.Ai.Contracts;

/// <summary>Observed work of one existing web.deepResearch operation, not a separate run.</summary>
public sealed record DeepResearchProgressEvent(
    string OperationId,
    string ParentRunId,
    long Sequence,
    string Phase,
    string Status,
    string Title,
    string? Detail = null,
    string? StepId = null,
    string? Tool = null,
    string? Query = null,
    string? Url = null,
    IReadOnlyList<DeepResearchSource>? Sources = null,
    string? Text = null,
    string? ModelId = null,
    ModelGenerationEvent? ModelProgress = null,
    int? Completed = null,
    int? Total = null,
    string? ErrorCode = null,
    JsonElement? Result = null);

public sealed record DeepResearchSource(string? Id, string Title, string Url, string? Snippet = null);
