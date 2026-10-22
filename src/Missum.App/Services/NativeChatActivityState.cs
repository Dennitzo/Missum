namespace Missum.App.Services;

/// <summary>Latched model work; token pulses cannot flicker an already published answer.</summary>
internal sealed class NativeChatActivityState
{
    private string? _phase;
    private DateTimeOffset? _at;
    private string? _source;
    private string? _visibleStep;
    internal bool Waiting { get; private set; }
    internal bool ObservePhase(string phase, DateTimeOffset? at)
    {
        if (at.HasValue && _at.HasValue && at < _at) return false;
        var previous = Waiting;
        if (at.HasValue) _at = at;
        if (phase is "codingWaiting" or "") return false;
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
        if (!active) { Waiting = false; _phase = "idle"; }
        else if (_source is not null && (_source != source || _visibleStep != visibleStep))
        { Waiting = false; _phase = "contentDelta"; }
        _source = source;
        _visibleStep = visibleStep;
    }
}
