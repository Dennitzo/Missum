using System.Runtime.InteropServices;
using Missum.App.Services;

namespace Missum.Tests;

#pragma warning disable CA2201 // Test-only injections verify the real clipboard HRESULTs and fatal exception boundary.

public sealed class ClipboardTextWriterTests
{
    [Fact]
    public async Task BusyClipboardRetriesTheExactUnicodeAndLatexSnapshot()
    {
        const string text = "Teil einer Antwort: α\r\n\\frac{1}{2} 😀";
        var writes = new List<string>();
        var delays = new List<TimeSpan>();
        var writer = new ClipboardTextWriter(value =>
        {
            writes.Add(value);
            if (writes.Count <= 2) throw new COMException("Clipboard is open elsewhere", unchecked((int)0x800401D0));
        }, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        Assert.True(await writer.WriteTextAsync(text));
        Assert.Equal(new[] { text, text, text }, writes);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(120) }, delays);
    }

    [Fact]
    public async Task PermanentlyLockedClipboardHasBoundedNonBlockingRetries()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var writer = new ClipboardTextWriter(_ => { calls++; throw new COMException("locked", unchecked((int)0x800401D0)); },
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        Assert.False(await writer.WriteTextAsync("Selected text"));
        Assert.Equal(5, calls);
        Assert.Equal(4, delays.Count);
        Assert.True(delays.Aggregate(TimeSpan.Zero, (sum, item) => sum + item) < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(unchecked((int)0x800401D0))]
    [InlineData(unchecked((int)0x800401D1))]
    [InlineData(unchecked((int)0x800401D2))]
    [InlineData(unchecked((int)0x800401D4))]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x800700AA))]
    public async Task TemporaryClipboardErrorsRecover(int hresult)
    {
        var calls = 0;
        var writer = new ClipboardTextWriter(_ => { if (++calls == 1) throw new COMException("busy", hresult); }, (_, _) => Task.CompletedTask);
        Assert.True(await writer.WriteTextAsync("range"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NewCopyRequestPreventsOlderDelayedCopyFromOverwritingIt()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        var writer = new ClipboardTextWriter(value =>
        {
            if (value == "old range") throw new COMException("busy", unchecked((int)0x800401D0));
            writes.Add(value);
        }, (_, _) => delay.Task);
        var old = writer.WriteTextAsync("old range");
        Assert.True(await writer.WriteTextAsync("new range"));
        delay.SetResult();
        Assert.False(await old);
        Assert.Equal("new range", Assert.Single(writes));
    }

    [Fact]
    public async Task CancellationDuringRetryDoesNotWriteOrThrow()
    {
        using var token = new CancellationTokenSource();
        var calls = 0;
        var writer = new ClipboardTextWriter(_ => { calls++; throw new COMException("busy", unchecked((int)0x800401D0)); },
            (_, _) => { token.Cancel(); return Task.FromCanceled(token.Token); });
        Assert.False(await writer.WriteTextAsync("range", token.Token));
        Assert.Equal(1, calls);
        Assert.False(await writer.WriteTextAsync("other", token.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnexpectedClipboardFailureIsHandledWithoutRetry()
    {
        var calls = 0;
        var writer = new ClipboardTextWriter(_ => { calls++; throw new InvalidOperationException("clipboard service unavailable"); },
            (_, _) => throw new InvalidOperationException("unexpected retry"));
        Assert.False(await writer.WriteTextAsync("range"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FatalMemoryFailureIsNotHidden()
    {
        var writer = new ClipboardTextWriter(_ => throw new COMException("out of memory", unchecked((int)0x8007000E)));
        await Assert.ThrowsAsync<COMException>(() => writer.WriteTextAsync("range"));
    }
}
