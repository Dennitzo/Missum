using Missum.App.Services;

namespace Missum.Tests;

public sealed class ComputerDesktopBrokerTests
{
    private static readonly Guid Session = Guid.Parse("1cf00108-84e1-4dac-91f6-5a435184e36b");
    private const string Run = "run-1";

    [Fact]
    public async Task ActivationIsRunBoundAndObservationIsSingleUse()
    {
        var approval = new FakeApproval();
        var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        broker.Begin(Session, Run);
        var first = await broker.ObserveAsync(Session, Run);
        Assert.Equal(1, approval.Activations);
        await broker.ExecuteAsync(Session, Run, new(first.ObservationId, "move", X: 1, Y: 1, CoordinateSpace: "pixels"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run, new(first.ObservationId, "move", X: 1, Y: 1, CoordinateSpace: "pixels")));
        await broker.ObserveAsync(Session, Run);
        Assert.Equal(1, approval.Activations);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.ObserveAsync(Session, "other"));
        broker.End(Session, Run);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.ObserveAsync(Session, Run));
        Assert.Throws<UnauthorizedAccessException>(() => broker.Begin(Session, Run));
        broker.Begin(Session, "new-run");
    }

    [Fact]
    public async Task UnclassifiedActionNeedsApprovalAndManualDoneDoesNotInject()
    {
        var approval = new FakeApproval { Decision = ComputerActionDecision.ManualDone };
        var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        var result = await broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels"));
        Assert.Equal("manual", result.Status);
        Assert.Equal(1, approval.Requests);
        Assert.Empty(platform.Actions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels")));
    }

    [Fact]
    public async Task CancelledActivationAndRunStopDoNotCaptureOrInject()
    {
        var approval = new FakeApproval { Activate = false };
        var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.ObserveAsync(Session, Run));
        Assert.Equal(0, platform.Captures);
        approval.Activate = true;
        var frame = await broker.ObserveAsync(Session, Run);
        broker.End(Session, Run);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels")));
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task RefusedActionDoesNotInjectAndRequiresANewObservation()
    {
        var approval = new FakeApproval { Decision = ComputerActionDecision.Cancel };
        var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);

        var result = await broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels"));

        Assert.Equal("cancelled", result.Status);
        Assert.Empty(platform.Actions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run,
            new(frame.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels")));
    }

    [Fact]
    public async Task StoppingRunWhileApprovalIsPendingCancelsTheRequestWithoutInput()
    {
        var approval = new PendingApproval();
        var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        var action = broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels"));
        await approval.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        broker.End(Session, Run);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Empty(platform.Actions);
        Assert.Throws<UnauthorizedAccessException>(() => broker.Begin(Session, Run));
    }

    [Fact]
    public async Task ObservationWaitsForReturnAndRejectsWindowSwitchDuringCapture()
    {
        var activity = new FakeForeground(); activity.Pause();
        var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(new FakeApproval(), platform, () => activity);
        broker.Begin(Session, Run);
        var observation = broker.ObserveAsync(Session, Run);
        Assert.False(observation.IsCompleted);
        Assert.Equal(0, platform.Captures);
        activity.Resume();
        await observation;
        platform.OnCapture = () => activity.ChangeWindow();
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ObserveAsync(Session, Run));
    }

    [Fact]
    public async Task WindowSwitchPausesActionAndInvalidatesObservation()
    {
        var activity = new FakeForeground(); var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(new FakeApproval(), platform, () => activity);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        activity.ChangeWindow(); activity.Pause();
        var action = broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "move", X: 1, Y: 1, CoordinateSpace: "pixels"));
        Assert.False(action.IsCompleted);
        activity.Resume();
        await Assert.ThrowsAsync<InvalidOperationException>(() => action);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task StoppingDuringUserPauseCancelsWithoutInput()
    {
        var activity = new FakeForeground(); var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(new FakeApproval(), platform, () => activity);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        activity.Pause();
        var action = broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "move", X: 1, Y: 1, CoordinateSpace: "pixels"));
        Assert.False(action.IsCompleted);
        broker.End(Session, Run);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task WindowSwitchAndReturnBetweenCallsInvalidatesObservation()
    {
        var activity = new FakeForeground(); var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(new FakeApproval(), platform, () => activity);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        activity.ChangeWindow();
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run,
            new(frame.ObservationId, "move", X: 1, Y: 1, CoordinateSpace: "pixels")));
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task WindowSwitchInterruptsActionInProgress()
    {
        var monitor = new FakeForeground();
        var platform = new FakePlatform { OnExecute = () => monitor.ChangeWindow() };
        using var broker = new ComputerDesktopBroker(new FakeApproval(), platform, () => monitor);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run,
            new(frame.ObservationId, "move", X: 1, Y: 1, CoordinateSpace: "pixels")));
        Assert.Empty(platform.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalAllowsMissumCardButRejectsOtherWindowSwitches(bool outside)
    {
        var activity = new FakeForeground(); var platform = new FakePlatform(); var approval = new ControlledApproval();
        using var broker = new ComputerDesktopBroker(approval, platform, () => activity);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        var action = broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "click", X: 1, Y: 1, CoordinateSpace: "pixels"));
        await approval.Requested.Task;
        activity.ChangeWindow(isMissum: !outside);
        approval.Decision.SetResult(ComputerActionDecision.Execute);
        if (outside) { await Assert.ThrowsAsync<InvalidOperationException>(() => action); Assert.Empty(platform.Actions); }
        else { Assert.Equal("completed", (await action).Status); Assert.Single(platform.Actions); }
    }

    [Fact]
    public async Task FocusActionMarksTheIntendedWindowAsExpected()
    {
        var monitor = new FakeForeground();
        var platform = new FakePlatform { Windows = [new("window-2", "Ziel", 43, 1, 0, 0, 100, 50)] };
        platform.OnExecute = () => Assert.Equal("window-2", monitor.Expected);
        using var broker = new ComputerDesktopBroker(new FakeApproval(), platform, () => monitor);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        await broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "focus", WindowId: "window-2"));
        Assert.Equal("window-2", monitor.Expected);
    }

    [Fact]
    public async Task RejectsStaleFramesAndMismatchedImageCoordinates()
    {
        var platform = new FakePlatform { Age = TimeSpan.FromMinutes(2) };
        using var broker = new ComputerDesktopBroker(new FakeApproval(), platform);
        broker.Begin(Session, Run);
        var stale = await broker.ObserveAsync(Session, Run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run, new(stale.ObservationId, "click", X: 1, Y: 1, CoordinateSpace: "pixels")));
        platform.Age = TimeSpan.Zero;
        var current = await broker.ObserveAsync(Session, Run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run, new(current.ObservationId, "click", X: 1, Y: 1, CoordinateSpace: "pixels", ImageWidth: 101)));
        var another = await broker.ObserveAsync(Session, Run);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => broker.ExecuteAsync(Session, Run, new(another.ObservationId, "click", X: 100, Y: 1, CoordinateSpace: "pixels")));
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("move")]
    [InlineData("click")]
    [InlineData("double_click")]
    [InlineData("drag")]
    [InlineData("scroll")]
    public async Task CoordinatesWithoutExplicitSpaceAreRejectedBeforeInputOrApproval(string kind)
    {
        var approval = new FakeApproval(); var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => broker.ExecuteAsync(Session, Run,
            new(frame.ObservationId, kind, X: 10, Y: 10, EndX: 15, EndY: 15, ScrollY: 120)));
        Assert.Contains("coordinateSpace", exception.Message);
        Assert.Equal(0, approval.Requests);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public void RejectsUnknownCoordinateSpacesInsteadOfGuessing()
    {
        var frame = new ComputerObservation("frame", [], "image/png", 100, 50, 0, 0, 100, 50, "", 0, []);
        Assert.Throws<ArgumentException>(() => ComputerDesktopBroker.NormalizeCoordinates(
            new("frame", "click", X: 10, Y: 10, CoordinateSpace: "unknown"), frame));
        Assert.Null(ComputerDesktopBroker.NormalizeCoordinates(new("frame", "type", Text: "Hallo"), frame).CoordinateSpace);
    }

    [Fact]
    public async Task InspectReturnsStructuredElementWithoutExecutingInput()
    {
        var approval = new FakeApproval(); var platform = new FakePlatform();
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        var result = await broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "inspect", X: 1, Y: 2, CoordinateSpace: "pixels"));
        Assert.Equal("Example", result.Element?.Name);
        Assert.Equal(0, approval.Requests);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task VerifiedEditClickAndPlainTextDoNotRequireAdditionalApproval()
    {
        var approval = new FakeApproval();
        var element = new ComputerElement("Eingabe", 50004, 42, true, false, "1.2.3");
        var platform = new FakePlatform { Foreground = new("window", "Test", 42, 1, 0, 0, 100, 50),
            InspectedElement = element, FocusedElement = element, FocusHandle = 7 };
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var first = await broker.ObserveAsync(Session, Run);
        await broker.ExecuteAsync(Session, Run, new(first.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels"));
        var second = await broker.ObserveAsync(Session, Run);
        await broker.ExecuteAsync(Session, Run, new(second.ObservationId, "type", Text: "Hallo"));
        Assert.Equal(0, approval.Requests);
        Assert.Equal(2, platform.Actions.Count);
    }

    [Theory]
    [InlineData(50000, 42, true, false, "1.2.3")]
    [InlineData(50004, 43, true, false, "1.2.3")]
    [InlineData(50004, 42, false, false, "1.2.3")]
    [InlineData(50004, 42, true, true, "1.2.3")]
    [InlineData(50004, 42, true, false, "")]
    public async Task UnverifiedOrProtectedEditRequiresApproval(int controlType, int pid, bool enabled, bool password, string runtimeId)
    {
        var approval = new FakeApproval { Decision = ComputerActionDecision.Cancel };
        var element = new ComputerElement("Eingabe", controlType, pid, enabled, password, runtimeId);
        var platform = new FakePlatform { Foreground = new("window", "Test", 42, 1, 0, 0, 100, 50),
            InspectedElement = element, FocusedElement = element, FocusHandle = 7 };
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var first = await broker.ObserveAsync(Session, Run);
        await broker.ExecuteAsync(Session, Run, new(first.ObservationId, "click", X: 2, Y: 3, CoordinateSpace: "pixels"));
        var second = await broker.ObserveAsync(Session, Run);
        await broker.ExecuteAsync(Session, Run, new(second.ObservationId, "type", Text: "Hallo"));
        Assert.Equal(2, approval.Requests);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task TextWithControlCharactersRequiresApprovalEvenInVerifiedEdit()
    {
        var approval = new FakeApproval { Decision = ComputerActionDecision.Cancel };
        var element = new ComputerElement("Eingabe", 50004, 42, true, false, "1.2.3");
        var platform = new FakePlatform { Foreground = new("window", "Test", 42, 1, 0, 0, 100, 50),
            FocusedElement = element, FocusHandle = 7 };
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        await broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "type", Text: "Hallo\n"));
        Assert.Equal(1, approval.Requests);
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task FocusCanNavigateOnlyToAWindowInTheCurrentObservation()
    {
        var approval = new FakeApproval();
        var platform = new FakePlatform { Windows = [new("window-2", "Zielprogramm", 43, 1, 0, 0, 100, 50)] };
        using var broker = new ComputerDesktopBroker(approval, platform);
        broker.Begin(Session, Run);
        var frame = await broker.ObserveAsync(Session, Run);
        await broker.ExecuteAsync(Session, Run, new(frame.ObservationId, "focus", WindowId: "window-2"));
        Assert.Equal(0, approval.Requests);
        var next = await broker.ObserveAsync(Session, Run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(Session, Run,
            new(next.ObservationId, "focus", WindowId: "window-unknown")));
    }

    [Theory]
    [InlineData(0, 100, -1920, 3840, -1920)]
    [InlineData(50, 100, -1920, 3840, 0)]
    [InlineData(99, 100, -1920, 3840, 1881)]
    public void CoordinatesUsePhysicalDesktopBounds(double x, int image, int origin, int extent, int expected) =>
        Assert.Equal(expected, WindowsComputerDesktopPlatform.ToScreenCoordinate(x, image, origin, extent));

    [Theory]
    [InlineData(120, -120)]
    [InlineData(-120, 120)]
    public void ScrollDirectionsMatchScreenCoordinates(int scrollY, int nativeDelta) =>
        Assert.Equal(nativeDelta, WindowsComputerDesktopPlatform.ToMouseWheelDelta(scrollY));

    [Fact]
    public void ConvertsExplicitNormalizedModelCoordinatesUsingActualImageSize()
    {
        var frame = new ComputerObservation("frame", [], "image/png", 3840, 2160, -1920, 0, 3840, 2160, "", 0, []);
        var action = ComputerDesktopBroker.NormalizeCoordinates(new("frame", "click", X: 110, Y: 125,
            CoordinateSpace: "normalized1000"), frame);
        Assert.Equal(422.4, action.X);
        Assert.Equal(270, action.Y);
        Assert.Throws<ArgumentOutOfRangeException>(() => ComputerDesktopBroker.NormalizeCoordinates(
            new("frame", "click", X: 1000, Y: 1, CoordinateSpace: "normalized1000"), frame));
    }

    [Fact]
    public void ChangedTargetPixelsRejectStaleObservation()
    {
        var original = new byte[40 * 40 * 4]; var same = new byte[original.Length];
        Assert.True(WindowsComputerDesktopPlatform.MatchesPatch(original, same, 40, 40, 20, 20));
        same[(20 * 40 + 20) * 4] = 1;
        Assert.False(WindowsComputerDesktopPlatform.MatchesPatch(original, same, 40, 40, 20, 20));
    }

    private sealed class FakeApproval : IComputerActionApproval
    {
        public bool Activate { get; set; } = true;
        public int Activations { get; private set; }
        public int Requests { get; private set; }
        public ComputerApprovalRequest? LastRequest { get; private set; }
        public ComputerActionDecision Decision { get; set; } = ComputerActionDecision.Execute;
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken)
        { Activations++; return Task.FromResult(Activate); }
        public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        { Requests++; LastRequest = request; return Task.FromResult(Decision); }
    }

    private sealed class PendingApproval : IComputerActionApproval
    {
        public TaskCompletionSource<bool> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) => Task.FromResult(true);
        public async Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        {
            Requested.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ComputerActionDecision.Execute;
        }
    }

    private sealed class ControlledApproval : IComputerActionApproval
    {
        public TaskCompletionSource<bool> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ComputerActionDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) => Task.FromResult(true);
        public async Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        { Requested.TrySetResult(true); return await Decision.Task.WaitAsync(cancellationToken); }
    }

    private sealed class FakeForeground : IComputerForegroundMonitor
    {
        private TaskCompletionSource? _pause;
        private bool _allowMissum;
        private long _revision;
        public event Action? UnexpectedForeground;
        public long Revision => _revision;
        public string? Expected { get; private set; }
        public void Expect(string? id) => Expected = id;
        public void AllowMissum(bool allow) => _allowMissum = allow;
        public void ChangeWindow(bool isMissum = false) { if (!isMissum || !_allowMissum) { _revision++; UnexpectedForeground?.Invoke(); } }
        public void Pause() => _pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resume() => _pause?.TrySetResult();
        public async Task<bool> WaitForExpectedAsync(CancellationToken cancellationToken)
        { if (_pause is null || _pause.Task.IsCompleted) return false; await _pause.Task.WaitAsync(cancellationToken); return true; }
        public void Dispose() { }
    }

    private sealed class FakePlatform : IComputerDesktopPlatform
    {
        public int Captures { get; private set; }
        public TimeSpan Age { get; set; }
        public IReadOnlyList<ComputerWindow> Windows { get; set; } = [];
        public ComputerWindow? Foreground { get; set; }
        public ComputerElement? FocusedElement { get; set; }
        public ComputerElement InspectedElement { get; set; } = new("Example", 50000, 42, true, false);
        public nint FocusHandle { get; set; }
        public Action? OnCapture { get; set; }
        public Action? OnExecute { get; set; }
        public List<ComputerAction> Actions { get; } = [];
        public Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            Captures++;
            OnCapture?.Invoke();
            var view = new ComputerObservation(Guid.NewGuid().ToString("N"), [1, 2, 3], "image/png", 100, 50,
                -1920, 0, 3840, 1080, "Test", 42, Windows);
            return Task.FromResult(new ComputerFrame(view, [], Foreground, FocusHandle, FocusedElement, DateTimeOffset.UtcNow - Age));
        }
        public Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken)
        { OnExecute?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); Actions.Add(action); return Task.CompletedTask; }
        public Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken) =>
            Task.FromResult(InspectedElement);
    }
}
