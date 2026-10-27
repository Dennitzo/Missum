using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeInspectorLayoutTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(850, false)]
    [InlineData(887, false)]
    [InlineData(888, true)]
    [InlineData(1040, true)]
    [InlineData(1920, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void SidebarOnlyReservesSpaceWhenChatRemainsUsable(double width, bool expected) =>
        Assert.Equal(expected, NativeInspectorLayout.HasRoom(width));
}
