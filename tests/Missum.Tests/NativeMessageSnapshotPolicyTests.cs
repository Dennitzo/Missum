using System.Text.Json;
using Missum.App.Pages;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeMessageSnapshotPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static JsonElement Message(string status, long revision, int seconds) => JsonSerializer.SerializeToElement(new
    { id = "message", content = status, status, revision, updatedAt = At.AddSeconds(seconds), toolSteps = Array.Empty<object>() });

    [Theory]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    [InlineData("failed")]
    [InlineData("completed")]
    public void NewerDurableTerminalRevisionWinsOverLaterStreamingClock(string terminal)
    {
        var current = Message("streaming", 10, 0);
        var incoming = Message(terminal, 11, 1);
        var decision = NativeMessageSnapshotPolicy.Compare(current, incoming, At.AddSeconds(2).ToString("O"));
        Assert.False(decision.PreservePayload);
        Assert.False(decision.PreserveLifecycle);
        var projection = new NativeAssistantPage.NativeMessageProjection(current, 0);
        projection.Apply(JsonSerializer.SerializeToElement(new { contentAppend = " Letztes Token", updatedAt = At.AddSeconds(2) }));
        var oldVersion = projection.Version;
        projection.Adopt(incoming);
        Assert.Equal(terminal, projection.Content);
        Assert.True(projection.Version > oldVersion);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    [InlineData("failed")]
    [InlineData("completed")]
    public void TerminalMetadataInvalidatesViewEvenWhenSameRevisionKeepsLaterPayload(string terminal)
    {
        var current = Message("streaming", 10, 0);
        var incoming = Message(terminal, 10, 1);
        var decision = NativeMessageSnapshotPolicy.Compare(current, incoming, At.AddSeconds(2).ToString("O"));
        Assert.True(decision.PreservePayload);
        Assert.False(decision.PreserveLifecycle);
        var projection = new NativeAssistantPage.NativeMessageProjection(current, 0);
        projection.Apply(JsonSerializer.SerializeToElement(new { contentAppend = " Letztes Token", updatedAt = At.AddSeconds(2) }));
        var oldVersion = projection.Version;
        projection.Adopt(incoming);
        Assert.Equal("streaming Letztes Token", projection.Content);
        Assert.Equal(terminal, projection.Source.GetProperty("status").GetString());
        Assert.True(projection.Version > oldVersion);
    }

    [Fact]
    public void StaleSnapshotsCannotResurrectStoppedRunButNewAttemptCan()
    {
        var stopped = Message("cancelled", 11, 2);
        var stale = NativeMessageSnapshotPolicy.Compare(stopped, Message("streaming", 10, 3), At.AddSeconds(2).ToString("O"));
        Assert.True(stale.PreservePayload);
        Assert.True(stale.PreserveLifecycle);
        var retry = NativeMessageSnapshotPolicy.Compare(stopped, Message("streaming", 12, 1), At.AddSeconds(2).ToString("O"));
        Assert.False(retry.PreservePayload);
        Assert.False(retry.PreserveLifecycle);
    }

    [Fact]
    public void WriteBehindRevisionCannotReplaceAYoungerLivePrefix()
    {
        var current = Message("streaming", 10, 0);
        var projection = new NativeAssistantPage.NativeMessageProjection(current, 0);
        projection.Apply(JsonSerializer.SerializeToElement(new { contentAppend = " vollständiger Satz.", updatedAt = At.AddSeconds(2) }));
        var incoming = Message("streaming", 11, 1);
        var decision = NativeMessageSnapshotPolicy.Compare(current, incoming, At.AddSeconds(2).ToString("O"));
        Assert.True(decision.PreservePayload);
        Assert.False(decision.PreserveLifecycle);
        projection.Adopt(incoming);
        Assert.Equal("streaming vollständiger Satz.", projection.Content);
        projection.Apply(JsonSerializer.SerializeToElement(new { contentAppend = " Nächster Schritt.", updatedAt = At.AddSeconds(3) }));
        Assert.Equal("streaming vollständiger Satz. Nächster Schritt.", projection.Content);
    }

    [Fact]
    public void LaterAuthoritativeCorrectionCanReplaceAndShortenStreamingText()
    {
        var current = Message("streaming", 10, 0);
        var projection = new NativeAssistantPage.NativeMessageProjection(current, 0);
        projection.Apply(JsonSerializer.SerializeToElement(new { contentAppend = " alte lange Formulierung.", updatedAt = At.AddSeconds(1) }));
        var incoming = JsonSerializer.SerializeToElement(new { id = "message", content = "Korrigiert.", status = "streaming",
            revision = 11, updatedAt = At.AddSeconds(2), toolSteps = Array.Empty<object>() });
        projection.Adopt(incoming);
        Assert.Equal("Korrigiert.", projection.Content);
    }
}
