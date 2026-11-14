using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Missum.App.Services;

/// <summary>Win32 input adapter. Screenshots and input use physical virtual-desktop coordinates.</summary>
internal sealed class WindowsComputerDesktopPlatform(DesktopScreenshotService screenshots) : IComputerDesktopPlatform
{
    private const int MaxImageBytes = 7 * 1024 * 1024;
    private const int MaxCapturePixels = 40_000_000;
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost", "wt", "regedit", "Taskmgr",
        "CredentialUIBroker", "LockApp", "LogonUI", "SecurityHealthSystray", "Missum Labs",
    };

    public Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken) => CaptureAsync(false, cancellationToken);

    public Task<ComputerFrame> CaptureWindowAsync(CancellationToken cancellationToken) => CaptureAsync(true, cancellationToken);

    private async Task<ComputerFrame> CaptureAsync(bool focusedWindow, CancellationToken cancellationToken)
    {
        EnsureInteractiveDesktop();
        var targets = screenshots.ListTargets();
        var desktop = targets.SingleOrDefault(t => t.Kind == DesktopCaptureTargetKind.VirtualDesktop)
            ?? throw new InvalidOperationException("Der interaktive Desktop ist nicht verfügbar.");
        if ((long)desktop.Width * desktop.Height > MaxCapturePixels)
            throw new InvalidOperationException("Die Desktopfläche ist für eine sichere Aufnahme zu groß.");
        var before = CurrentForeground(targets);
        var focus = GetFocus(before);
        ComputerElement? focusedElement = null;
        try { focusedElement = ReadAutomationElement(null); }
        catch (InvalidOperationException) { /* Text input fails closed when UIA is unavailable. */ }
        var pixels = DesktopScreenshotService.CaptureVideoFrame(desktop, desktop.Width, desktop.Height);
        cancellationToken.ThrowIfCancellationRequested();
        var after = CurrentForeground(screenshots.ListTargets());
        if (before != after || focus != GetFocus(after))
            throw new InvalidOperationException("Der Fokus hat sich während der Aufnahme geändert. Bitte erneut beobachten.");
        // Crop the captured desktop pixels, not a separate off-screen window
        // render: the model sees precisely what is actually visible on screen.
        var region = focusedWindow
            ? VisibleWindowRegion(desktop.X, desktop.Y, desktop.Width, desktop.Height, before)
            : new ComputerImageRegion(desktop.X, desktop.Y, desktop.Width, desktop.Height);
        var source = focusedWindow
            ? Crop(pixels, desktop.Width, desktop.Height, region.X - desktop.X, region.Y - desktop.Y, region.Width, region.Height)
            : pixels;
        var width = region.Width;
        var height = region.Height;
        var scale = Math.Min(1d, Math.Min(4096d / width, 4096d / height));
        byte[] png;
        do
        {
            width = Math.Max(1, (int)Math.Floor(region.Width * scale));
            height = Math.Max(1, (int)Math.Floor(region.Height * scale));
            var sample = width == region.Width && height == region.Height ? source : Resize(source, region.Width, region.Height, width, height);
            png = await DesktopScreenshotService.EncodePngAsync(sample, width, height, cancellationToken).ConfigureAwait(false);
            scale *= 0.8;
            if (scale < 0.05 && png.Length > MaxImageBytes)
                throw new InvalidOperationException("Der Desktop kann nicht innerhalb des Bildlimits aufgenommen werden.");
        } while (png.Length > MaxImageBytes);
        var windows = targets.Where(t => t.Kind == DesktopCaptureTargetKind.Window)
            .Select(ToWindow).Where(w => w is not null).Cast<ComputerWindow>().ToArray();
        var observation = new ComputerObservation(Guid.NewGuid().ToString("N"), png, "image/png", width, height,
            desktop.X, desktop.Y, desktop.Width, desktop.Height, before?.Title ?? "", before?.ProcessId ?? 0, windows,
            focusedWindow ? region : null);
        return new ComputerFrame(observation, pixels, before, focus, focusedElement, DateTimeOffset.UtcNow);
    }

    public Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInteractiveDesktop();
        var kind = action.Kind.ToLowerInvariant();
        if (kind == "wait") return Task.CompletedTask;
        var targets = screenshots.ListTargets();
        var desktop = targets.SingleOrDefault(t => t.Kind == DesktopCaptureTargetKind.VirtualDesktop)
            ?? throw Changed();
        var view = frame.Public;
        if (desktop.X != view.DesktopX || desktop.Y != view.DesktopY || desktop.Width != view.DesktopWidth || desktop.Height != view.DesktopHeight)
            throw Changed();
        var target = kind == "focus"
            ? view.Windows.FirstOrDefault(w => w.Id == action.WindowId) ?? throw Changed()
            : frame.Foreground ?? throw Changed();
        EnsureWindow(target, targets);
        EnsurePermittedProcess(target.ProcessId);
        var foreground = CurrentForeground(targets);
        if (kind == "focus")
        {
            // Window switching is navigation, but only from the exact observed
            // foreground to a window whose identity and bounds still match.
            if (foreground != frame.Foreground) throw Changed();
            if (foreground != target && SetForegroundWindow(ParseHandle(target.Id)) == 0) throw Changed();
            if (CurrentForeground(screenshots.ListTargets()) != target) throw Changed();
            return Task.CompletedTask;
        }
        // The confirmation UI may have foregrounded Missum. Restore only the exact original
        // target after consent, then recheck identity, target pixels and focus before input.
        if (foreground != target)
        {
            if (!approved || foreground?.ProcessId != Environment.ProcessId || SetForegroundWindow(ParseHandle(target.Id)) == 0)
                throw Changed();
            targets = screenshots.ListTargets();
            EnsureWindow(target, targets);
            if (CurrentForeground(targets) != target) throw Changed();
        }
        if (kind is "type" or "keypress")
        {
            if (frame.FocusHandle == 0 || GetFocus(target) != frame.FocusHandle) throw Changed();
            var focused = ReadAutomationElement(null);
            if (string.IsNullOrWhiteSpace(focused.RuntimeId) || focused != frame.FocusedElement) throw Changed();
            if (focused.IsPassword) throw new UnauthorizedAccessException("Passwortfelder können nicht durch Missum bedient werden.");
            if (!focused.IsEnabled) throw new InvalidOperationException("Das ausgewählte Steuerelement ist deaktiviert.");
        }

        var x = 0; var y = 0;
        if (action.X.HasValue && action.Y.HasValue)
        {
            var region = GetImageRegion(view);
            x = ToScreenCoordinate(action.X.Value, view.Width, region.X, region.Width);
            y = ToScreenCoordinate(action.Y.Value, view.Height, region.Y, region.Height);
            if (GetAncestor(WindowFromPoint(new Point(x, y)), 2) != ParseHandle(target.Id))
                throw new InvalidOperationException("Der Bildpunkt gehört nicht zum beobachteten Vordergrundfenster. Bitte erneut beobachten.");
            var current = DesktopScreenshotService.CaptureVideoFrame(desktop, desktop.Width, desktop.Height);
            if (!MatchesPatch(frame.PhysicalPixels, current, desktop.Width, desktop.Height, x - desktop.X, y - desktop.Y))
                throw new InvalidOperationException("Der Bereich um das Ziel hat sich geändert. Bitte erneut beobachten.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        switch (kind)
        {
            case "move": Move(x, y, desktop); break;
            case "click": Move(x, y, desktop); cancellationToken.ThrowIfCancellationRequested(); Mouse(0x0002); Mouse(0x0004); break;
            case "double_click": Move(x, y, desktop); cancellationToken.ThrowIfCancellationRequested(); Mouse(0x0002); Mouse(0x0004); cancellationToken.ThrowIfCancellationRequested(); Mouse(0x0002); Mouse(0x0004); break;
            case "drag":
                var region = GetImageRegion(view);
                var endX = ToScreenCoordinate(action.EndX!.Value, view.Width, region.X, region.Width);
                var endY = ToScreenCoordinate(action.EndY!.Value, view.Height, region.Y, region.Height);
                var endPixels = DesktopScreenshotService.CaptureVideoFrame(desktop, desktop.Width, desktop.Height);
                if (!MatchesPatch(frame.PhysicalPixels, endPixels, desktop.Width, desktop.Height, endX - desktop.X, endY - desktop.Y)) throw Changed();
                for (var step = 0; step <= 20; step++)
                {
                    var intermediateX = x + (int)((long)(endX - x) * step / 20);
                    var intermediateY = y + (int)((long)(endY - y) * step / 20);
                    if (GetAncestor(WindowFromPoint(new Point(intermediateX, intermediateY)), 2) != ParseHandle(target.Id)) throw Changed();
                }
                Move(x, y, desktop); cancellationToken.ThrowIfCancellationRequested(); Mouse(0x0002);
                try { cancellationToken.ThrowIfCancellationRequested(); Move(endX, endY, desktop); } finally { Mouse(0x0004); }
                break;
            case "scroll": Move(x, y, desktop); cancellationToken.ThrowIfCancellationRequested(); Mouse(0x0800, unchecked((uint)ToMouseWheelDelta(action.ScrollY!.Value))); break;
            case "type": TypeText(action.Text!, cancellationToken); break;
            case "keypress": PressKey(action.Key!, cancellationToken); break;
            default: throw new ArgumentException("Nicht unterstützte Computeraktion.");
        }
        return Task.CompletedTask;
    }

    public Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInteractiveDesktop();
        var targets = screenshots.ListTargets();
        var desktop = targets.SingleOrDefault(t => t.Kind == DesktopCaptureTargetKind.VirtualDesktop) ?? throw Changed();
        var view = frame.Public;
        if (desktop.X != view.DesktopX || desktop.Y != view.DesktopY || desktop.Width != view.DesktopWidth || desktop.Height != view.DesktopHeight)
            throw Changed();
        var target = frame.Foreground ?? throw Changed();
        EnsureWindow(target, targets);
        EnsurePermittedProcess(target.ProcessId);
        if (CurrentForeground(targets) != target) throw Changed();
        var region = GetImageRegion(view);
        var x = ToScreenCoordinate(action.X!.Value, view.Width, region.X, region.Width);
        var y = ToScreenCoordinate(action.Y!.Value, view.Height, region.Y, region.Height);
        if (GetAncestor(WindowFromPoint(new Point(x, y)), 2) != ParseHandle(target.Id)) throw Changed();
        var current = DesktopScreenshotService.CaptureVideoFrame(desktop, desktop.Width, desktop.Height);
        if (!MatchesPatch(frame.PhysicalPixels, current, desktop.Width, desktop.Height, x - desktop.X, y - desktop.Y)) throw Changed();
        var result = ReadAutomationElement(new Point(x, y));
        // Do not expose passwords, even if a broken provider supplies a name containing their value.
        if (result.IsPassword) result = result with { Name = "Geschütztes Eingabefeld" };
        return Task.FromResult(result);
    }

    private static ComputerElement ReadAutomationElement(Point? point)
    {
        object? instance = null;
        IAutomationElement? element = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), throwOnError: true)!;
            instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("UI Automation ist nicht verfügbar.");
            var automation = (IAutomation)instance;
            element = point.HasValue ? automation.ElementFromPoint(point.Value) : automation.GetFocusedElement();
            if (element is null) throw new InvalidOperationException("Das UI-Element konnte nicht sicher bestimmt werden.");
            var password = element.GetIsPassword();
            var name = password ? "Geschütztes Eingabefeld" : element.GetName() ?? "";
            if (name.Length > 500) name = name[..500];
            var runtimeId = element.GetRuntimeId();
            return new ComputerElement(name, element.GetControlType(), element.GetProcessId(), element.GetIsEnabled(), password,
                runtimeId is { Length: > 0 } ? string.Join('.', runtimeId) : "");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            throw new InvalidOperationException("UI Automation konnte das Element nicht sicher bestimmen.", ex);
        }
        finally
        {
            if (element is not null && Marshal.IsComObject(element)) Marshal.FinalReleaseComObject(element);
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }

    internal static int ToScreenCoordinate(double value, int imageSize, int origin, int extent) =>
        checked(origin + Math.Clamp((int)Math.Floor(value * extent / imageSize), 0, extent - 1));

    // The tool uses screen coordinates (positive is down); Win32 wheel input
    // uses the opposite sign (positive is up).
    internal static int ToMouseWheelDelta(int scrollY) => checked(-scrollY);

    private static ComputerImageRegion GetImageRegion(ComputerObservation observation) => observation.ImageRegion
        ?? new(observation.DesktopX, observation.DesktopY, observation.DesktopWidth, observation.DesktopHeight);

    internal static ComputerImageRegion VisibleWindowRegion(int desktopX, int desktopY, int desktopWidth, int desktopHeight, ComputerWindow? window)
    {
        if (window is null || window.Width <= 0 || window.Height <= 0)
            throw new InvalidOperationException("Kein sichtbares Vordergrundfenster für die Fensteraufnahme verfügbar.");
        var left = Math.Max((long)desktopX, window.X);
        var top = Math.Max((long)desktopY, window.Y);
        var right = Math.Min((long)desktopX + desktopWidth, (long)window.X + window.Width);
        var bottom = Math.Min((long)desktopY + desktopHeight, (long)window.Y + window.Height);
        if (right <= left || bottom <= top)
            throw new InvalidOperationException("Das Vordergrundfenster liegt außerhalb des sichtbaren Desktops.");
        return new(checked((int)left), checked((int)top), checked((int)(right - left)), checked((int)(bottom - top)));
    }

    internal static byte[] Crop(byte[] source, int width, int height, int x, int y, int cropWidth, int cropHeight)
    {
        if (source.LongLength != (long)width * height * 4 || x < 0 || y < 0 || cropWidth <= 0 || cropHeight <= 0
            || (long)x + cropWidth > width || (long)y + cropHeight > height)
            throw new ArgumentException("Ungültiger Bildausschnitt.");
        var output = new byte[checked(cropWidth * cropHeight * 4)];
        for (var row = 0; row < cropHeight; row++)
            Buffer.BlockCopy(source, checked(((y + row) * width + x) * 4), output, checked(row * cropWidth * 4), checked(cropWidth * 4));
        return output;
    }

    internal static bool MatchesPatch(byte[] expected, byte[] actual, int width, int height, int x, int y)
    {
        if (expected.Length != actual.Length || expected.Length != (long)width * height * 4) return false;
        // Compare a small actionable area rather than unrelated animations on other monitors.
        for (var row = Math.Max(0, y - 12); row < Math.Min(height, y + 13); row++)
        {
            var left = Math.Max(0, x - 12);
            var right = Math.Min(width, x + 13);
            var offset = checked((row * width + left) * 4);
            if (!expected.AsSpan(offset, (right - left) * 4).SequenceEqual(actual.AsSpan(offset, (right - left) * 4))) return false;
        }
        return true;
    }

    private static byte[] Resize(byte[] source, int sw, int sh, int dw, int dh)
    {
        var output = new byte[checked(dw * dh * 4)];
        for (var y = 0; y < dh; y++)
            for (var x = 0; x < dw; x++)
                Buffer.BlockCopy(source, checked((((int)((long)y * sh / dh)) * sw + (int)((long)x * sw / dw)) * 4), output, (y * dw + x) * 4, 4);
        return output;
    }

    private static ComputerWindow? CurrentForeground(IReadOnlyList<DesktopCaptureTarget> targets)
    {
        var h = GetForegroundWindow();
        var target = targets.FirstOrDefault(t => t.Kind == DesktopCaptureTargetKind.Window && t.Handle == h);
        return target is null ? null : ToWindow(target);
    }

    private static ComputerWindow? ToWindow(DesktopCaptureTarget target)
    {
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            return new(target.Id, target.DisplayName, target.ProcessId, process.StartTime.ToUniversalTime().Ticks,
                target.X, target.Y, target.Width, target.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    private static void EnsureWindow(ComputerWindow expected, IReadOnlyList<DesktopCaptureTarget> targets)
    {
        var current = targets.FirstOrDefault(t => t.Id == expected.Id && t.Kind == DesktopCaptureTargetKind.Window);
        if (current is null || ToWindow(current) != expected) throw Changed();
    }

    private static void EnsurePermittedProcess(int pid)
    {
        if (pid == Environment.ProcessId) throw new UnauthorizedAccessException("Missum kann sich nicht selbst steuern.");
        try
        {
            using var process = Process.GetProcessById(pid);
            if (BlockedProcesses.Contains(process.ProcessName))
                throw new UnauthorizedAccessException("Dieses Programm ist für die Computersteuerung gesperrt.");
        }
        catch (ArgumentException) { throw Changed(); }
    }

    private static nint GetFocus(ComputerWindow? window)
    {
        if (window is null) return 0;
        var thread = GetWindowThreadProcessId(ParseHandle(window.Id), out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
    }

    private static nint ParseHandle(string id) => id.StartsWith("window-", StringComparison.Ordinal)
        && long.TryParse(id[7..], System.Globalization.NumberStyles.HexNumber, null, out var value) ? (nint)value : 0;
    private static InvalidOperationException Changed() => new("Der Desktopzustand hat sich geändert. Bitte erneut beobachten.");

    private static void EnsureInteractiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) throw new InvalidOperationException("Der interaktive Desktop ist gesperrt oder nicht verfügbar.");
        try
        {
            var name = new char[256];
            if (!GetUserObjectInformation(desktop, 2, name, 512, out _) || !string.Equals(new string(name).TrimEnd('\0'), "Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Geschützte oder gesperrte Windows-Desktops können nicht gesteuert werden.");
        }
        finally { CloseDesktop(desktop); }
    }

    private static void Move(int x, int y, DesktopCaptureTarget desktop) => Mouse(0x0001 | 0x8000 | 0x4000,
        0, (int)Math.Clamp(Math.Round(((double)x - desktop.X + 0.5) * 65536 / desktop.Width), 0, 65535),
        (int)Math.Clamp(Math.Round(((double)y - desktop.Y + 0.5) * 65536 / desktop.Height), 0, 65535));

    private static void Mouse(uint flags, uint data = 0, int x = 0, int y = 0)
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Dx = x, Dy = y, Flags = flags, Data = data } };
        Send(input);
    }

    private static void TypeText(string text, CancellationToken cancellationToken)
    {
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = new Input { Type = 1, Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 } };
            Send(input); input.Keyboard.Flags |= 0x0002; Send(input);
        }
    }

    private static void PressKey(string key, CancellationToken cancellationToken)
    {
        var names = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length is 0 or > 4) throw new ArgumentException("Ungültige Tastenkombination.");
        var codes = names.Select(KeyCode).ToArray();
        if (codes.Contains((ushort)0x11) && codes.Contains((ushort)0x12) && codes.Contains((ushort)0x2E))
            throw new UnauthorizedAccessException("Diese Tastenkombination ist für die Computersteuerung gesperrt.");
        var pressed = new List<ushort>();
        try
        {
            foreach (var code in codes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Send(new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = code } });
                pressed.Add(code);
            }
        }
        finally
        {
            foreach (var code in pressed.AsEnumerable().Reverse())
                Send(new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = code, Flags = 0x0002 } });
        }
    }

    private static ushort KeyCode(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return (ushort)char.ToUpperInvariant(key[0]);
        return key.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11, "shift" => 0x10, "alt" => 0x12,
            "enter" => 0x0D, "tab" => 0x09, "escape" or "esc" => 0x1B, "space" => 0x20,
            "backspace" => 0x08, "delete" => 0x2E, "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
            "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
            _ => throw new ArgumentException("Diese Taste wird nicht unterstützt."),
        };
    }

    private static void Send(Input input)
    {
        if (SendInput(1, in input, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows hat die Eingabe abgelehnt.");
    }

    // Minimal UIAutomation COM projections. The unused slots preserve the system interface's vtable order.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement(); void ElementFromHandle();
        [return: MarshalAs(UnmanagedType.Interface)] IAutomationElement ElementFromPoint(Point point);
        [return: MarshalAs(UnmanagedType.Interface)] IAutomationElement GetFocusedElement();
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomationElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
        void FindFirst(); void FindAll(); void FindFirstBuildCache();
        void FindAllBuildCache(); void BuildUpdatedCache(); void GetCurrentPropertyValue(); void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue(); void GetCachedPropertyValueEx(); void GetCurrentPatternAs(); void GetCachedPatternAs();
        void GetCurrentPattern(); void GetCachedPattern(); void GetCachedParent(); void GetCachedChildren();
        int GetProcessId(); int GetControlType();
        [return: MarshalAs(UnmanagedType.BStr)] string GetLocalizedControlType();
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        [return: MarshalAs(UnmanagedType.BStr)] string GetAcceleratorKey();
        [return: MarshalAs(UnmanagedType.BStr)] string GetAccessKey();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetHasKeyboardFocus();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetIsKeyboardFocusable();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetIsEnabled();
        [return: MarshalAs(UnmanagedType.BStr)] string GetAutomationId();
        [return: MarshalAs(UnmanagedType.BStr)] string GetClassName();
        [return: MarshalAs(UnmanagedType.BStr)] string GetHelpText();
        int GetCulture();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetIsControlElement();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetIsContentElement();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetIsPassword();
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, in Input input, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, [Out] char[] info, int length, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
}
