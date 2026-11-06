namespace Missum.App.Services;

/// <summary>Latched model work; token pulses cannot flicker an already published answer.</summary>
internal sealed class NativeChatActivityState
{
    internal const string DefaultThinkingLabel = "Denke nach";
    private string? _phase;
    private DateTimeOffset? _at;
    private string? _source;
    private string? _visibleStep;
    private NativeModelPhasePresentation? _promptProcessing;
    internal bool Waiting { get; private set; }
    internal int? ContextProcessingPercent => _promptProcessing?.MeasuredPromptFraction is { } progress
        ? (int)Math.Round(progress * 100, MidpointRounding.AwayFromZero) : null;
    internal string ThinkingLabel => ContextProcessingPercent is { } percent
        ? $"{DefaultThinkingLabel} · {percent}% verarbeitet" : DefaultThinkingLabel;

    internal bool ObservePhase(string phase, DateTimeOffset? at, int? processed = null, int? total = null, double? progress = null)
    {
        if (at.HasValue && _at.HasValue && at < _at) return false;
        var previous = Waiting;
        if (at.HasValue) _at = at;
        if (phase is "codingWaiting" or "") return false;
        _promptProcessing = phase == "promptProcessing"
            ? (_promptProcessing ?? new()).Observe(phase, processed, total, progress, at) : null;
        if (phase is "promptProcessing" or "codingLoading" or "codingCompacting" or "generationStarted" or "generationRetry"
            or "reasoningStarted" or "reasoningDelta" or "toolCallGeneration" or "responseRecovery")
        {
            if (_phase != phase || Waiting) Waiting = true;
        }
        else if (phase is "contentDelta" or "completed" or "cancelled" or "failed" or "idle") Waiting = false;
        _phase = phase;
        return previous != Waiting;
    }
    internal void ObserveOutput(string source, string visibleStep, bool active)
    {
        if (!active) { Waiting = false; _phase = "idle"; _promptProcessing = null; }
        else if (_source is not null && (_source != source || _visibleStep != visibleStep))
        { Waiting = false; _phase = "contentDelta"; _promptProcessing = null; }
        _source = source;
        _visibleStep = visibleStep;
    }
}
