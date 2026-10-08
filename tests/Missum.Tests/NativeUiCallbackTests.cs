using Missum.App.Pages;
using System.Runtime.InteropServices;

namespace Missum.Tests;

#pragma warning disable CA2201 // Test-only instances verify recoverable versus fatal runtime exception classification.

public sealed class NativeUiCallbackTests
{
    [Fact]
    public void OrdinaryUiProjectionFailuresCanBeDiagnosedAndRetried()
    {
        Assert.True(NativeAssistantPage.IsRecoverableUiProjectionException(new COMException("WinUI projection", unchecked((int)0x80004005))));
        Assert.True(NativeAssistantPage.IsRecoverableUiProjectionException(new InvalidOperationException("Detached visual")));
        Assert.True(NativeAssistantPage.IsRecoverableUiProjectionException(new ArgumentOutOfRangeException()));
    }

    [Fact]
    public void FatalAndNonProjectionFailuresAreNeverSilentlyConsumed()
    {
        Assert.False(NativeAssistantPage.IsRecoverableUiProjectionException(new OutOfMemoryException()));
        Assert.False(NativeAssistantPage.IsRecoverableUiProjectionException(new COMException("Native memory failure", unchecked((int)0x8007000E))));
        Assert.False(NativeAssistantPage.IsRecoverableUiProjectionException(new AccessViolationException()));
        Assert.False(NativeAssistantPage.IsRecoverableUiProjectionException(new StackOverflowException()));
        Assert.False(NativeAssistantPage.IsRecoverableUiProjectionException(new IOException("Storage failure")));
        Assert.False(NativeAssistantPage.IsRecoverableUiProjectionException(new NullReferenceException()));
    }
}
