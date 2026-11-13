using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Missum.BlenderAdapter;

/// <summary>
/// A Blender window that is already open on this machine, found without any add-on, plug-in or
/// setting inside Blender. A window like this can be looked at but not commanded: nothing inside
/// that process answers requests, so it is reported as observed instead of controllable.
/// </summary>
internal sealed record ObservedBlenderWindow(
    int ProcessId,
    nint WindowHandle,
    string Title,
    string ExecutablePath,
    string ApplicationVersion,
    string? DetectedProjectFile,
    int Width,
    int Height);

/// <summary>
/// Scans the machine for running Blender processes and the windows that belong to them. This is the
/// automatic discovery the integration is asked for: a user who simply opened Blender by hand is
/// found, named after the window title and can be shown to vision, all without installing anything.
/// </summary>
internal static class BlenderProcessWatch
{
    private const uint GwOwner = 4;
    private const int MinimumWidth = 120;
    private const int MinimumHeight = 80;

    public static IReadOnlyList<ObservedBlenderWindow> Observe()
    {
        var running = RunningProcesses();
        if (running.Count == 0) return [];

        var best = new Dictionary<int, ObservedBlenderWindow>();
        EnumWindowsCallback callback = (handle, _) =>
        {
            if (!GetWindowThreadProcessId(handle, out var processId) || processId == 0) return true;
            if (!running.TryGetValue((int)processId, out var process)) return true;
            if (!QualifiesAsMainWindow(handle, out var title, out var width, out var height)) return true;
            var candidate = new ObservedBlenderWindow(
                (int)processId, handle, title, process.ExecutablePath, process.ApplicationVersion,
                DetectedProjectFromTitle(title), width, height);
            if (!best.TryGetValue(candidate.ProcessId, out var current) || Score(candidate) > Score(current))
                best[candidate.ProcessId] = candidate;
            return true;
        };

        EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return best.Values
            .OrderByDescending(Score)
            .ThenBy(static window => window.ProcessId)
            .ToArray();
    }

    /// <summary>
    /// Blender puts the open file into the window title, for example
    /// <c>Tisch.blend - Scene, ViewLayer - Blender 5.2.2 LTS</c>. Only the file name is reported:
    /// a title never proves a full path, and guessing one could open the wrong file.
    /// </summary>
    public static string? DetectedProjectFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        const string marker = ".blend";
        for (var index = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = title.IndexOf(marker, index + marker.Length, StringComparison.OrdinalIgnoreCase))
        {
            var start = index;
            while (start > 0 && !char.IsWhiteSpace(title[start - 1])) start--;
            var candidate = title[start..(index + marker.Length)];
            if (candidate.Length <= marker.Length) continue;
            var name = Path.GetFileName(candidate.Replace('/', Path.DirectorySeparatorChar));
            if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return name;
        }

        return null;
    }

    /// <summary>Window text of a handle, used when a caller wants the current title again.</summary>
    public static string TitleOf(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var buffer = new char[length + 1];
        var written = GetWindowText(handle, buffer, buffer.Length);
        return written <= 0 ? string.Empty : new string(buffer, 0, written);
    }

    public static bool IsMinimized(nint handle) => handle != 0 && IsIconic(handle);

    /// <summary>Physical pixel bounds of the window, or null when the window is gone.</summary>
    public static bool TryGetBounds(nint handle, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (handle == 0 || !GetWindowRect(handle, out var rect)) return false;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        x = rect.Left;
        y = rect.Top;
        return width > 0 && height > 0;
    }

    private static int Score(ObservedBlenderWindow window)
    {
        var score = window.Width * window.Height / 1_000;
        if (window.Title.Contains("Blender", StringComparison.OrdinalIgnoreCase)) score += 500_000;
        if (window.DetectedProjectFile is not null) score += 50_000;
        return score;
    }

    private static bool QualifiesAsMainWindow(nint handle, out string title, out int width, out int height)
    {
        title = string.Empty;
        width = height = 0;
        if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != 0) return false;
        if (!GetWindowRect(handle, out var rect)) return false;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        if (width < MinimumWidth || height < MinimumHeight) return false;
        title = TitleOf(handle);
        return title.Trim().Length > 0;
    }

    private sealed record RunningProcess(string ExecutablePath, string ApplicationVersion);

    private static Dictionary<int, RunningProcess> RunningProcesses()
    {
        var processes = new Dictionary<int, RunningProcess>();
        foreach (var process in Process.GetProcessesByName("blender"))
        {
            using (process)
            {
                var path = string.Empty;
                var version = "unbekannt";
                try
                {
                    path = process.MainModule?.FileName ?? string.Empty;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
                    or NotSupportedException or ArgumentException)
                {
                    // A 32/64 or access restriction only hides the path, the window is still there.
                }

                if (path.Length > 0)
                {
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(path);
                        if (info.FileMajorPart > 0)
                            version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
                    }
                    catch (Exception exception) when (exception is Win32Exception or IOException or ArgumentException)
                    {
                        // Version is a nicety for the status line.
                    }
                }

                processes[process.Id] = new RunningProcess(path, version);
            }
        }

        return processes;
    }

    private delegate bool EnumWindowsCallback(nint hWnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, char[] text, int maximumCount);
}
