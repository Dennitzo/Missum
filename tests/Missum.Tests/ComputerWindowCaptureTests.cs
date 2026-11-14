using Missum.App.Services;

namespace Missum.Tests;

public sealed class ComputerWindowCaptureTests
{
    [Fact]
    public void WindowRegionIsClippedToVirtualDesktopAcrossNegativeMonitorCoordinates()
    {
        var window = new ComputerWindow("window-1", "Test", 1, 1, -2100, -100, 1000, 500);
        var region = WindowsComputerDesktopPlatform.VisibleWindowRegion(-1920, 0, 5760, 2160, window);
        Assert.Equal(new ComputerImageRegion(-1920, 0, 820, 400), region);
        Assert.Equal(-1510, WindowsComputerDesktopPlatform.ToScreenCoordinate(410, 820, region.X, region.Width));
        Assert.Equal(200, WindowsComputerDesktopPlatform.ToScreenCoordinate(100, 200, region.Y, region.Height));
    }

    [Fact]
    public void OffscreenAndMissingWindowsCannotProduceAFrame()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsComputerDesktopPlatform.VisibleWindowRegion(0, 0, 1920, 1080, null));
        var window = new ComputerWindow("window-1", "Test", 1, 1, 2000, 0, 100, 100);
        Assert.Throws<InvalidOperationException>(() => WindowsComputerDesktopPlatform.VisibleWindowRegion(0, 0, 1920, 1080, window));
    }

    [Fact]
    public void CropPreservesPixelsFromTheActualDesktopCapture()
    {
        var source = Enumerable.Range(0, 4 * 3 * 4).Select(i => (byte)i).ToArray();
        var cropped = WindowsComputerDesktopPlatform.Crop(source, 4, 3, 1, 1, 2, 2);
        Assert.Equal(source[20..28].Concat(source[36..44]), cropped);
        Assert.Throws<ArgumentException>(() => WindowsComputerDesktopPlatform.Crop(source, 4, 3, 3, 1, 2, 2));
    }
}
