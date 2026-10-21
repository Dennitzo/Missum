using System.Text.Json;
using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Models;

internal sealed record ModelPreparation(string InstanceId, bool WasAlreadyLoaded, int ContextLength = 0);

public sealed record ModelRuntimeProgress(
    string State,
    string? ToolName = null,
    int? ArgumentCharacters = null,
    double? PromptProgress = null,
    int? PromptTokens = null,
    int? ProcessedPromptTokens = null,
    int? GeneratedTokens = null,
    double? TokensPerSecond = null,
    int? CurrentTokens = null,
    int? Attempt = null,
    string? FailureKind = null,
    bool? ToolArgumentsJsonComplete = null,
    int? ContentCharacters = null,
    bool? FinishObserved = null,
    string? ContentDelta = null,
    string? ReasoningDelta = null,
    int? CachedPromptTokens = null);

public sealed record LmChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<LmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ReasoningContent = null);

public sealed record LmToolCall(string Id, string Name, JsonElement Arguments);

public sealed record LmToolDefinition(string Name, string Description, JsonElement Parameters,
    string? ContextProfileVersion = null);

public sealed record LmChatResult(
    string? Content,
    IReadOnlyList<LmToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens,
    bool HadReasoning = false,
    int ReasoningTokens = 0,
    ModelTurnMetrics? Metrics = null,
    string? ReasoningContent = null,
    string? FinishReason = null);

public sealed class ModelEmptyResponseException(bool hadReasoning, int attempts)
    : InvalidOperationException(hadReasoning
        ? $"Das lokale Modell hat nach {attempts} aufeinanderfolgenden Anfragen nur Denktext geliefert, aber keinen Antworttext oder vollständigen Werkzeugaufruf. Bereits ausgeführte Änderungen und der Arbeitsstand bleiben gespeichert. Der Lauf kann fortgesetzt werden."
        : $"Das lokale Modell hat nach {attempts} aufeinanderfolgenden Anfragen keinen Antworttext oder vollständigen Werkzeugaufruf geliefert. Bereits ausgeführte Änderungen und der Arbeitsstand bleiben gespeichert. Der Lauf kann fortgesetzt werden.")
{
    public bool HadReasoning { get; } = hadReasoning;
    public int Attempts { get; } = attempts;
}

public sealed class ModelContextLengthException(
    string modelId,
    int requestedContextLength,
    int availableContextLength)
    : InvalidOperationException(
        $"Das Modell '{modelId}' stellt nur {availableContextLength:N0} statt der erforderlichen {requestedContextLength:N0} Kontexttoken bereit.")
{
    public string ModelId { get; } = modelId;
    public int RequestedContextLength { get; } = requestedContextLength;
    public int AvailableContextLength { get; } = availableContextLength;
}

/// <summary>
/// The fully templated native prompt does not leave room for generation in the
/// loaded slot. It may be recovered only by an explicit context-planning step,
/// never by retrying the same request or silently changing the model window.
/// </summary>
public sealed class NativeContextBudgetException(int inputTokens, int contextLength, int? inputBudget = null)
    : InvalidOperationException(
        inputBudget is { } threshold
            ? $"Der exakt tokenisierte Prompt benötigt {inputTokens:N0} Token und erreicht die Komprimierungsschwelle von {threshold:N0} bei {contextLength:N0} Kontexttoken. Der Arbeitskontext muss vor der nächsten Modellanfrage verdichtet werden."
            : $"Der exakt tokenisierte Prompt benötigt {inputTokens:N0} Token; im geladenen Kontext von {contextLength:N0} Token bleibt kein Platz für eine Antwort. Der Arbeitskontext muss vor der nächsten Modellanfrage verdichtet werden.")
{
    public int InputTokens { get; } = inputTokens;
    public int ContextLength { get; } = contextLength;
    public int MinimumOutputTokens { get; } = 1;
    // llama.cpp also needs its final context boundary token.
    public int BudgetTokens => inputBudget ?? Math.Max(0, ContextLength - MinimumOutputTokens - 1);
}

public sealed class ModelGenerationTerminatedException(
    string providerCode,
    Exception? innerException = null)
    : HttpRequestException($"Die Modellgenerierung wurde beendet ({providerCode}).", innerException)
{
    public string ProviderCode { get; } = string.IsNullOrWhiteSpace(providerCode)
        ? "unknown"
        : providerCode.Trim();
}

public sealed class ModelProviderRequestException(string phase, int attempts, Exception innerException)
    : HttpRequestException(CreateMessage(phase, attempts, innerException), innerException,
        (innerException as HttpRequestException)?.StatusCode)
{
    public string Phase { get; } = phase;

    private static string CreateMessage(string phase, int attempts, Exception exception)
    {
        var operation = phase switch { "token_counting" => "Prompt-Tokenzählung", "model_selection" => "Modellerkennung", "model_loading" => "Modellvorbereitung", _ => "Modellanfrage" };
        var detail = exception.Message[..Math.Min(exception.Message.Length, 1000)];
        detail = string.Concat(detail.Select(static character => char.IsControl(character) ? ' ' : character)).Trim();
        return $"Die Verbindung zum nativen Windows-llama-Modell ist bei der {operation} nach {attempts} Versuchen fehlgeschlagen. {detail}";
    }
}
