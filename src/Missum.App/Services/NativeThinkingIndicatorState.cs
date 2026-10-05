namespace Missum.App.Services;

/// <summary>Tracks silent token generation without coupling it to a visual tree.</summary>
internal sealed class NativeThinkingIndicatorState
{
    internal static bool IsOwnedStep(string? stepAgentId, string? conversationAgentId) =>
        string.IsNullOrEmpty(stepAgentId) || string.Equals(stepAgentId, conversationAgentId, StringComparison.Ordinal);

    private static readonly TimeSpan ProgressFreshness = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ContentQuietPeriod = TimeSpan.FromMilliseconds(750);
    private DateTimeOffset? _latestProgressAt;
    private DateTimeOffset? _lastTokenProgressAt;
    private DateTimeOffset? _lastContentAt;
    private DateTimeOffset? _contentProgressAt;
    private string? _content;
    private string? _reasoningId;
    private string? _reasoning;
    private int _generatedTokens;
    private bool _generating;
    private bool _shown;

    public string? MessageId { get; private set; }

    public void Reset()
    {
        MessageId = null;
        _latestProgressAt = null;
        _lastTokenProgressAt = null;
        _lastContentAt = null;
        _contentProgressAt = null;
        _content = null;
        _reasoningId = null;
        _reasoning = null;
        _generatedTokens = 0;
        _generating = false;
        _shown = false;
    }

    public void ObserveReasoning(string messageId, string reasoningId, string text, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!string.Equals(MessageId, messageId, StringComparison.Ordinal))
        {
            Reset();
            MessageId = messageId;
        }
        if (_reasoningId == reasoningId && _reasoning == text) return;
        _reasoningId = reasoningId;
        _reasoning = text;
        // A real reasoning delta is generation evidence even when the provider
        // does not send separate token-progress pulses. Replay keeps its age.
        if (_contentProgressAt.HasValue && updatedAt <= _contentProgressAt) return;
        _lastTokenProgressAt = updatedAt;
        _generating = true;
    }

    public void ObserveProgress(string messageId, string? generationState, int? generatedTokens, DateTimeOffset? progressAt)
    {
        if (!string.Equals(MessageId, messageId, StringComparison.Ordinal))
        {
            Reset();
            MessageId = messageId;
        }

        // Replayed SSE events must not resurrect generation after a newer phase.
        if (progressAt.HasValue && _latestProgressAt.HasValue && progressAt < _latestProgressAt) return;
        if (progressAt.HasValue) _latestProgressAt = progressAt;

        // Heartbeats can retain an established generation phase, but they carry
        // no proof of new tokens and must not extend its freshness window.
        if (generationState is "codingWaiting" or "reasoningDelta" or "contentDelta") return;

        if (generationState == "tokenProgress" && generatedTokens is > 0 && progressAt.HasValue)
        {
            // Text consumes the previous pulse. Replayed snapshots and repeated
            // counts must not start another thinking interval on their own.
            if (_contentProgressAt.HasValue && progressAt <= _contentProgressAt) return;
            if (generatedTokens > _generatedTokens)
            {
                _lastTokenProgressAt = progressAt;
                _generatedTokens = generatedTokens.Value;
                _generating = true;
            }
            return;
        }

        // Intermediate phases only withdraw eligibility to START thinking.
        // Once visible, the row stays until visible answer text or run end.
        _generating = false;
        _lastTokenProgressAt = null;
        // Only an explicitly new generation round may restart its counter.
        // Tool/retry metadata must not turn an old count into a fresh pulse.
        if (generationState is "generationStarted" or "generationRetry") _generatedTokens = 0;
    }

    public bool ObserveContent(string messageId, string content, DateTimeOffset now)
    {
        // Separators streamed ahead of the next sentence are not visible output.
        content = content.TrimEnd();
        if (!string.Equals(MessageId, messageId, StringComparison.Ordinal)
            || string.Equals(_content, content, StringComparison.Ordinal)) return false;
        var firstContent = _content is null;
        _content = content;
        if (content.Length == 0) return false;
        _lastContentAt = now;
        // Historical text in the first snapshot is a baseline, not new output.
        if (firstContent && !_shown) return false;
        _shown = false;
        _generating = false;
        _lastTokenProgressAt = null;
        _contentProgressAt = _latestProgressAt > now ? _latestProgressAt : now;
        return true;
    }

    public bool ShouldShow(string messageId, bool active, bool blocked, bool visible, DateTimeOffset now)
    {
        if (!string.Equals(MessageId, messageId, StringComparison.Ordinal)) return false;
        if (!active)
        {
            _shown = false;
            _generating = false;
            _lastTokenProgressAt = null;
            return false;
        }
        // Navigation hides the visual without losing the answer's latch.
        if (!visible) return false;
        if (_shown) return true;
        if (blocked || !_generating || _lastTokenProgressAt is not { } tokenAt) return false;
        var progressAge = now - tokenAt;
        if (progressAge < TimeSpan.Zero || progressAge >= ProgressFreshness) return false;
        if (_lastContentAt is { } contentAt && now - contentAt < ContentQuietPeriod) return false;
        _shown = true;
        return true;
    }
}
