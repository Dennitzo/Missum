namespace Missum.App.Controls;

/// <summary>Filters incidental wheel movement while the conversation follows its live tail.</summary>
internal sealed class NativeAutoScrollGesture
{
    internal const int MinimumLines = 3;
    internal const double LineHeight = 22;
    internal const double MinimumDistance = MinimumLines * LineHeight;
    private double _upwardDistance;
    private DateTimeOffset _lastInput;

    // Positive values move toward older content. Return the complete gesture
    // distance once it establishes reading intent, including its small prefixes.
    internal double Observe(double upwardDistance, DateTimeOffset now)
    {
        if (!double.IsFinite(upwardDistance) || upwardDistance <= 0)
        { Reset(); return 0; }
        if (now - _lastInput > TimeSpan.FromMilliseconds(900)) _upwardDistance = 0;
        _lastInput = now;
        _upwardDistance += upwardDistance;
        if (_upwardDistance < MinimumDistance) return 0;
        var distance = _upwardDistance;
        Reset();
        return distance;
    }

    internal void Reset() { _upwardDistance = 0; _lastInput = default; }
}
