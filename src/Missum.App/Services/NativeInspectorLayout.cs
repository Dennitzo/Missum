namespace Missum.App.Services;

/// <summary>Reserve a separate fixed-width rail while keeping the chat usable.</summary>
internal static class NativeInspectorLayout
{
    internal const double SidebarWidth = 288;
    internal const double MinimumContentWidth = 600;

    internal static bool HasRoom(double workspaceWidth) =>
        double.IsFinite(workspaceWidth) && workspaceWidth >= SidebarWidth + MinimumContentWidth;
}
