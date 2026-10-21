using Missum.Ai.Contracts;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    private static readonly Action<ILogger, string, int, Exception?> LogVisionCountingFallback =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(4118, "VisionTokenCountingFallback"),
            "Native vision count for {ModelId}: placeholder tokenizer unavailable; real-image prefill measured {InputTokens} prompt tokens; single probe token discarded.");
    private static readonly Action<ILogger, string, int, int, int, string, Exception?> LogModelRequestBudget =
        LoggerMessage.Define<string, int, int, int, string>(LogLevel.Information,
            new EventId(4104, "ModelRequestBudget"),
            "Native model request {ModelId}: input_tokens={InputTokens}, context_tokens={ContextTokens}, max_tokens={MaximumOutputTokens}, reasoning_effort={ReasoningEffort}.");

    private async Task<Dictionary<string, object?>> ApplyTokenBudgetAsync(
        IReadOnlyDictionary<string, object?> body,
        int contextLength,
        int? maximumOutputTokens,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? progress,
        bool compactAtThreshold,
        bool allowMultimodalCountingFallback,
        CancellationToken cancellationToken)
    {
        if (progress is not null)
            await progress(new ModelRuntimeProgress("tokenCounting"), cancellationToken).ConfigureAwait(false);
        // Native llama applies the identical chat template, tools, reasoning
        // kwargs and multimodal preprocessing without running model generation.
        var inputTokens = await CountNativeInputAsync(body, allowMultimodalCountingFallback, cancellationToken).ConfigureAwait(false);

        var compactionBudget = ContextCompactionPolicy.InputTokenBudget(contextLength);
        if (compactAtThreshold && inputTokens >= compactionBudget)
            throw new NativeContextBudgetException(inputTokens, contextLength, compactionBudget);
        var outputTokens = ResolveMaximumOutputTokens(contextLength, inputTokens, maximumOutputTokens);
        if (progress is not null)
            // Keep the already measured full target before dispatching native
            // inference. A Stop can arrive during the first prompt batch,
            // before llama emits its first streaming progress frame.
            await progress(new ModelRuntimeProgress("tokenCounting", PromptTokens: inputTokens),
                cancellationToken).ConfigureAwait(false);
        var request = new Dictionary<string, object?>(body, StringComparer.Ordinal)
        {
            ["max_tokens"] = outputTokens,
        };
        var effort = body.TryGetValue("reasoning_effort", out var direct) ? direct?.ToString() : null;
        if (body.TryGetValue("chat_template_kwargs", out var kwargs)
            && kwargs is IReadOnlyDictionary<string, object?> template
            && template.TryGetValue("reasoning_effort", out var templateEffort))
            effort = templateEffort?.ToString();
        if (effort is not (null or "none" or "off"))
            request["reasoning_budget_tokens"] = outputTokens;
        LogModelRequestBudget(_logger, body["model"]?.ToString() ?? "", inputTokens,
            contextLength, outputTokens, effort ?? "none", null);
        return request;
    }

    private async Task<int> CountNativeInputAsync(IReadOnlyDictionary<string, object?> body,
        bool allowMultimodalCountingFallback, CancellationToken token)
    {
        try
        {
            using var response = await SendJsonAsync(HttpMethod.Post, "v1/chat/completions/input_tokens", body, token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("input_tokens", out var count) && count.TryGetInt32(out var tokens) && tokens > 0) return tokens;
            throw new JsonException("Der native Tokenzähler lieferte keine gültige Promptlänge.");
        }
        catch (HttpRequestException error) when (allowMultimodalCountingFallback
            && error.Message.Contains("Failed to tokenize prompt", StringComparison.OrdinalIgnoreCase))
        {
            // Some mtmd preprocessors (DeepSeek 4.1) cannot tokenize placeholder
            // images. Real image preprocessing works. Only this stateless vision
            // path may prefill the exact same request; discard its single token.
            // Never estimate image tokens or bypass the subsequent budget check.
            var probe = new Dictionary<string, object?>(body, StringComparer.Ordinal)
            { ["stream"] = false, ["return_progress"] = false, ["max_tokens"] = 1, ["cache_prompt"] = true };
            probe.Remove("stream_options");
            using var response = await SendJsonAsync(HttpMethod.Post, "v1/chat/completions", probe, token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("prompt_tokens", out var count) && count.TryGetInt32(out var tokens) && tokens > 0
                && usage.TryGetProperty("completion_tokens", out var output) && output.TryGetInt32(out var generated) && generated is >= 0 and <= 1)
            {
                LogVisionCountingFallback(_logger, body["model"]?.ToString() ?? "", tokens, null);
                return tokens;
            }
            throw new JsonException("Der Vision-Zählvorlauf lieferte keine gültige Promptlänge.");
        }
    }

    internal static int ResolveMaximumOutputTokens(int contextLength, int inputTokens, int? requestedMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        if (requestedMaximum is { } explicitMaximum) ArgumentOutOfRangeException.ThrowIfLessThan(explicitMaximum, 1);
        // llama stops when prompt + generated + 1 reaches the slot capacity.
        // Reasoning and the visible answer share this single output allowance.
        var available = (long)contextLength - inputTokens - 1;
        if (available < 1)
            // The planner's estimate can differ from the actual chat template,
            // tool schema and tokenizer. Let the run deliberately compact and
            // retry this prompt; this is neither a programming error nor a
            // transient transport failure. No prompt evaluation or generation
            // has taken place at this point; preserve the prepared cache slot.
            throw new NativeContextBudgetException(inputTokens, contextLength);
        return (int)Math.Min(available, requestedMaximum ?? int.MaxValue);
    }

    internal static int ReadLoadedContextLength(JsonElement properties)
    {
        if (properties.TryGetProperty("default_generation_settings", out var settings)
            && settings.TryGetProperty("n_ctx", out var context)
            && context.TryGetInt32(out var length) && length >= 2_048)
            return length;
        throw new JsonException("Der native Modellprozess meldet keinen gültigen geladenen Kontext (props.n_ctx).");
    }

    private static int ReadNominalContextLength(JsonElement item, string modelId, int fallback)
    {
        const string tagPrefix = "missum-context-train:";
        if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var value = tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
                if (value?.StartsWith(tagPrefix, StringComparison.Ordinal) == true
                    && int.TryParse(value.AsSpan(tagPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)
                    && maximum >= 2_048)
                    return maximum;
            }
        }
        if (item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("n_ctx_train", out var context) && context.TryGetInt32(out var trainingMaximum)
            && trainingMaximum >= 2_048)
            return trainingMaximum;
        var familyMaximum = ModelContextProfiles.ResolveMaximum(modelId, null);
        return familyMaximum == ModelContextProfiles.NativeRuntimeDefault ? fallback : familyMaximum;
    }
}
