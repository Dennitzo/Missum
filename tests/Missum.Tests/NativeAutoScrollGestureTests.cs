using Missum.App.Controls;

namespace Missum.Tests;

public sealed class NativeAutoScrollGestureTests
{
    [Fact]
    public void SmallWheelFragmentsRequireThreeLinesAndPreserveTheEntireGesture()
    {
        var gesture = new NativeAutoScrollGesture();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, gesture.Observe(22, now));
        Assert.Equal(0, gesture.Observe(21, now.AddMilliseconds(30)));
        Assert.Equal(67, gesture.Observe(24, now.AddMilliseconds(60)));
        Assert.Equal(0, gesture.Observe(2, now.AddMilliseconds(90)));
    }

    [Fact]
    public void DirectionChangesLongPausesAndExplicitResetDiscardIncidentalMotion()
    {
        var gesture = new NativeAutoScrollGesture();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, gesture.Observe(60, now));
        Assert.Equal(0, gesture.Observe(10, now.AddSeconds(2)));
        Assert.Equal(0, gesture.Observe(-1, now.AddSeconds(2.1)));
        Assert.Equal(0, gesture.Observe(60, now.AddSeconds(2.2)));
        gesture.Reset();
        Assert.Equal(0, gesture.Observe(10, now.AddSeconds(2.3)));
        Assert.Equal(210, gesture.Observe(200, now.AddSeconds(2.4)));
    }
}
