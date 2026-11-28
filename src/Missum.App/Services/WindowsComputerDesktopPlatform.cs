using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>
/// Win32 Computer-Use platform. Screenshots and input use physical virtual-desktop coordinates.
/// Observations carry a Set-of-Mark: numbered UI Automation targets drawn on the image plus a
/// compact element list, so the model can act on an element index instead of guessing pixels.
/// The desktop is controlled directly; the only refusal is input into Missum's own windows.
/// </summary>
internal sealed class WindowsComputerDesktopPlatform(DesktopScreenshotService screenshots) : IComputerDesktopPlatform
{
    private const int MaxImageBytes = 7 * 1024 * 1024;
    private const int MaxCapturePixels = 60_000_000;
    // Local vision models read a 1920 px image quickly and precisely; marks keep small controls addressable.
    private const int MaxImageSide = 1920;
    private static readonly TimeSpan AutomationBudget = TimeSpan.FromSeconds(4);
    private static readonly JsonSerializerOptions ElementJson = new(JsonSerializerDefaults.Web);

    public Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken) => CaptureAsync(false, cancellationToken);

    public Task<ComputerFrame> CaptureWindowAsync(CancellationToken cancellationToken) => CaptureAsync(true, cancellationToken);

    private async Task<ComputerFrame> CaptureAsync(bool focusedWindow, CancellationToken cancellationToken)
    {
        EnsureInteractiveDesktop();
        var targets = screenshots.ListTargets();
        var desktop = targets.SingleOrDefault(t => t.Kind == DesktopCaptureTargetKind.VirtualDesktop)
            ?? throw new InvalidOperationException("Der interaktive Desktop ist nicht verfügbar.");
        if ((long)desktop.Width * desktop.Height > MaxCapturePixels)
            throw new InvalidOperationException("Die Desktopfläche ist für eine Aufnahme zu groß.");
        var foreground = CurrentForeground(targets);
        // When Missum itself is in front (the user just sent the prompt), the subject is the
        // window right behind it; Missum is never the target of Computer-Use.
        var subject = foreground is { } front && front.ProcessId != Environment.ProcessId ? front : NextWindowBelowMissum(targets);
        var pixels = DesktopScreenshotService.CaptureVideoFrame(desktop, desktop.Width, desktop.Height);
        cancellationToken.ThrowIfCancellationRequested();
        var region = focusedWindow && subject is not null
            ? VisibleWindowRegion(desktop.X, desktop.Y, desktop.Width, desktop.Height, subject)
            : new ComputerImageRegion(desktop.X, desktop.Y, desktop.Width, desktop.Height);
        var source = region.Width == desktop.Width && region.Height == desktop.Height
            ? pixels : Crop(pixels, desktop.Width, desktop.Height, region.X - desktop.X, region.Y - desktop.Y, region.Width, region.Height);

        // Set-of-Mark: actionable controls of the subject window, its popups (menus, Slate
        // dropdowns) and, on the full desktop, the taskbar.
        var windows = new List<(nint, ComputerUiSource)>();
        if (subject is not null)
        {
            foreach (var popup in PopupWindows(subject)) windows.Add((popup, ComputerUiSource.Popup));
            windows.Add((ParseHandle(subject.Id), ComputerUiSource.Window));
        }
        if (!focusedWindow && FindWindow("Shell_TrayWnd", null) is var taskbar and not 0) windows.Add((taskbar, ComputerUiSource.Taskbar));
        var candidates = await ComputerUiAutomation.CollectAsync(windows, AutomationBudget, cancellationToken).ConfigureAwait(false);

        var scale = Math.Min(1d, (double)MaxImageSide / Math.Max(region.Width, region.Height));
        byte[] png;
        IReadOnlyList<ComputerUiTarget> marks;
        int width, height;
        bool grid;
        do
        {
            width = Math.Max(1, (int)Math.Floor(region.Width * scale));
            height = Math.Max(1, (int)Math.Floor(region.Height * scale));
            var image = ComputerMarks.Resample(source, region.Width, region.Height, width, height);
            marks = ComputerUiAutomation.SelectTargets(candidates, region, width, height, IsMissumPoint);
            // Programs without accessible controls (Unreal viewport, games, canvas apps) get a coordinate grid.
            grid = subject is not null && marks.Count(mark => mark.Candidate.Source != ComputerUiSource.Taskbar) < 5;
            if (grid) ComputerMarks.DrawGrid(image, width, height);
            ComputerMarks.Draw(image, width, height, marks.Select(mark => new ComputerMark(mark.Index, mark.ImageX, mark.ImageY, mark.ImageWidth, mark.ImageHeight)));
            png = await DesktopScreenshotService.EncodePngAsync(image, width, height, cancellationToken).ConfigureAwait(false);
            scale *= 0.85;
            if (scale < 0.05 && png.Length > MaxImageBytes)
                throw new InvalidOperationException("Der Desktop kann nicht innerhalb des Bildlimits aufgenommen werden.");
        } while (png.Length > MaxImageBytes);

        var windowList = targets.Where(t => t.Kind == DesktopCaptureTargetKind.Window)
            .Select(ToWindow).Where(w => w is not null).Cast<ComputerWindow>()
            .Where(w => w.ProcessId != Environment.ProcessId)
            .Take(40).ToArray();
        var app = subject is null ? null : ComputerAppRecognizer.Recognize(subject.Process, subject.Title);
        var elements = JsonSerializer.SerializeToElement(marks.Select(mark => new
        {
            i = mark.Index,
            type = mark.Candidate.Type,
            name = mark.Candidate.Name,
            value = mark.Candidate.Value,
            enabled = mark.Candidate.Enabled ? (bool?)null : false,
            area = mark.Candidate.Source switch { ComputerUiSource.Popup => "popup", ComputerUiSource.Taskbar => "taskbar", _ => (string?)null },
            x = mark.ImageX, y = mark.ImageY, w = mark.ImageWidth, h = mark.ImageHeight,
        }), ElementJson);
        var observation = new ComputerObservation(Guid.NewGuid().ToString("N"), png, "image/png", width, height,
            desktop.X, desktop.Y, desktop.Width, desktop.Height, subject?.Title ?? "", subject?.ProcessId ?? 0, windowList,
            focusedWindow ? region : null, Elements: elements, App: app, Grid: grid);
        return new ComputerFrame(observation, pixels, foreground, GetFocus(subject), null, DateTimeOffset.UtcNow,
            Targets: marks, Subject: subject);
    }

    public async Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInteractiveDesktop();
        var kind = action.Kind.ToLowerInvariant();
        if (kind == "wait") { await Task.Delay(1500, cancellationToken).ConfigureAwait(false); return; }
        if (kind == "launch")
        {
            var launch = ComputerAppRecognizer.ResolveLaunch(action.Target!, action.Arguments);
            using var process = ComputerAppRecognizer.Start(launch);
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
            return;
        }
        var desktop = screenshots.ListTargets().SingleOrDefault(t => t.Kind == DesktopCaptureTargetKind.VirtualDesktop) ?? throw Changed();
        var view = frame.Public;
        if (kind is "focus" or "window")
        {
            var handle = action.WindowId is { Length: > 0 } id ? ParseHandle(id) : ParseHandle(frame.Subject?.Id ?? "");
            if (handle == 0) throw new InvalidOperationException("Kein Fenster für diese Aktion gefunden. Beobachte erneut.");
            EnsureNotMissum(handle);
            ApplyWindowAction(handle, kind == "focus" ? "focus" : action.WindowAction!);
            return;
        }

        var target = action.ElementIndex is { } index ? frame.Targets?.FirstOrDefault(item => item.Index == index) : null;
        if (action.ElementIndex is not null && target is null) throw new InvalidOperationException("Das Element gehört nicht zur aktuellen Beobachtung.");
        var (x, y) = target is not null ? (target.CenterX, target.CenterY) : (0, 0);
        if (target is null && action.X.HasValue && action.Y.HasValue)
        {
            var region = GetImageRegion(view);
            x = ToScreenCoordinate(action.X.Value, view.Width, region.X, region.Width);
            y = ToScreenCoordinate(action.Y.Value, view.Height, region.Y, region.Height);
        }
        var pointAction = kind is "move" or "click" or "double_click" or "right_click" or "middle_click" or "drag" or "scroll";
        if (pointAction && IsMissumPoint(x, y)) throw new UnauthorizedAccessException("Missum kann sich nicht selbst steuern.");

        if (kind is "type" or "keypress" or "set_value")
        {
            // Keyboard input goes to the subject: give the chosen element focus, otherwise make sure
            // the observed window (not Missum) is in front.
            if (target is not null)
            {
                if (kind == "set_value")
                {
                    BringToFront(ParseHandle(frame.Subject?.Id ?? ""));
                    if (await ComputerUiAutomation.TrySetValueAsync(target.Candidate.Element, action.Text!).ConfigureAwait(false)) return;
                    Move(x, y, desktop); Click(0x0002, 0x0004);
                    PressKey("ctrl+a", cancellationToken);
                    if (action.Text!.Length > 0) TypeText(action.Text, cancellationToken); else PressKey("delete", cancellationToken);
                    return;
                }
                if (!await ComputerUiAutomation.TryFocusAsync(target.Candidate.Element).ConfigureAwait(false)) { Move(x, y, desktop); Click(0x0002, 0x0004); }
            }
            else if (GetForegroundProcessId() == Environment.ProcessId && frame.Subject is { } subject)
                BringToFront(ParseHandle(subject.Id));
            if (GetForegroundProcessId() == Environment.ProcessId) throw new UnauthorizedAccessException("Missum kann sich nicht selbst steuern.");
            if (kind == "type") TypeText(action.Text!, cancellationToken); else PressKey(action.Key!, cancellationToken);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        switch (kind)
        {
            case "move": Move(x, y, desktop); break;
            case "click": Move(x, y, desktop); Click(0x0002, 0x0004); break;
            case "double_click": Move(x, y, desktop); Click(0x0002, 0x0004); await Task.Delay(60, cancellationToken).ConfigureAwait(false); Click(0x0002, 0x0004); break;
            case "right_click": Move(x, y, desktop); Click(0x0008, 0x0010); break;
            case "middle_click": Move(x, y, desktop); Click(0x0020, 0x0040); break;
            case "drag":
                var dragRegion = GetImageRegion(view);
                var endX = ToScreenCoordinate(action.EndX!.Value, view.Width, dragRegion.X, dragRegion.Width);
                var endY = ToScreenCoordinate(action.EndY!.Value, view.Height, dragRegion.Y, dragRegion.Height);
                if (IsMissumPoint(endX, endY)) throw new UnauthorizedAccessException("Missum kann sich nicht selbst steuern.");
                Move(x, y, desktop); Mouse(0x0002);
                try
                {
                    // Interpolated moves: sliders, viewports and drag-and-drop need intermediate positions.
                    for (var step = 1; step <= 24; step++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Move(x + (int)((long)(endX - x) * step / 24), y + (int)((long)(endY - y) * step / 24), desktop);
                        await Task.Delay(12, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally { Mouse(0x0004); }
                break;
            case "scroll":
                Move(x, y, desktop);
                if (action.ScrollY is { } vertical and not 0) Mouse(0x0800, unchecked((uint)ToMouseWheelDelta(vertical)));
                if (action.ScrollX is { } horizontal and not 0) Mouse(0x1000, unchecked((uint)horizontal));
                break;
            default: throw new ArgumentException("Nicht unterstützte Computeraktion.");
        }
    }

    public async Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action.ElementIndex is { } index && frame.Targets?.FirstOrDefault(item => item.Index == index) is { } target)
            return new ComputerElement($"{target.Candidate.Name}{(target.Candidate.Value is { } value ? " · Wert: " + value : "")}",
                0, frame.Subject?.ProcessId ?? 0, target.Candidate.Enabled, false, $"{target.Candidate.Type}#{index}");
        var region = GetImageRegion(frame.Public);
        var x = ToScreenCoordinate(action.X!.Value, frame.Public.Width, region.X, region.Width);
        var y = ToScreenCoordinate(action.Y!.Value, frame.Public.Height, region.Y, region.Height);
        return await Task.Run(() => ReadAutomationElement(new Point(x, y)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComputerAfter?> DescribeAsync(ComputerFrame frame, CancellationToken cancellationToken)
    {
        // Give the UI a moment to react (menus, dialogs, launched programs) before describing it.
        await Task.Delay(450, cancellationToken).ConfigureAwait(false);
        var targets = screenshots.ListTargets();
        var windows = targets.Where(t => t.Kind == DesktopCaptureTargetKind.Window && t.ProcessId != Environment.ProcessId).ToArray();
        var before = frame.Public.Windows.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        var now = windows.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        var foreground = CurrentForeground(targets);
        var focus = await ComputerUiAutomation.DescribeFocusAsync().ConfigureAwait(false);
        return new ComputerAfter(foreground?.Title ?? "", foreground?.Process ?? "",
            windows.Where(window => !before.Contains(window.Id)).Select(window => window.DisplayName).Take(10).ToArray(),
            frame.Public.Windows.Where(window => !now.Contains(window.Id)).Select(window => window.Title).Take(10).ToArray(),
            focus);
    }

    private static ComputerElement ReadAutomationElement(Point point)
    {
        object? instance = null;
        IAutomationElement? element = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), throwOnError: true)!;
            instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("UI Automation ist nicht verfügbar.");
            element = ((IAutomation)instance).ElementFromPoint(point) ?? throw new InvalidOperationException("Das UI-Element konnte nicht bestimmt werden.");
            var name = element.GetName() ?? "";
            if (name.Length > 500) name = name[..500];
            var runtimeId = element.GetRuntimeId();
            return new ComputerElement(name, element.GetControlType(), element.GetProcessId(), element.GetIsEnabled(), element.GetIsPassword(),
                runtimeId is { Length: > 0 } ? string.Join('.', runtimeId) : "");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            throw new InvalidOperationException("UI Automation konnte das Element nicht bestimmen.", ex);
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
        for (var row = Math.Max(0, y - 12); row < Math.Min(height, y + 13); row++)
        {
            var left = Math.Max(0, x - 12);
            var right = Math.Min(width, x + 13);
            var offset = checked((row * width + left) * 4);
            if (!expected.AsSpan(offset, (right - left) * 4).SequenceEqual(actual.AsSpan(offset, (right - left) * 4))) return false;
        }
        return true;
    }

    private static ComputerWindow? CurrentForeground(IReadOnlyList<DesktopCaptureTarget> targets)
    {
        var h = GetForegroundWindow();
        var target = targets.FirstOrDefault(t => t.Kind == DesktopCaptureTargetKind.Window && t.Handle == h);
        return target is null ? null : ToWindow(target);
    }

    /// <summary>The topmost listed window below Missum in z-order (what the user sees next to Missum).</summary>
    private static ComputerWindow? NextWindowBelowMissum(IReadOnlyList<DesktopCaptureTarget> targets)
    {
        var listed = targets.Where(t => t.Kind == DesktopCaptureTargetKind.Window && t.ProcessId != Environment.ProcessId)
            .ToDictionary(t => t.Handle);
        for (var handle = GetTopWindow(0); handle != 0; handle = GetWindow(handle, 2))
            if (listed.TryGetValue(handle, out var target)) return ToWindow(target);
        return null;
    }

    private static ComputerWindow? ToWindow(DesktopCaptureTarget target)
    {
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            long started;
            try { started = process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { started = 0; } // Elevated processes hide StartTime.
            return new(target.Id, target.DisplayName, target.ProcessId, started, target.X, target.Y, target.Width, target.Height, process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    /// <summary>Visible top-level windows of the subject's process (menus, dropdowns, Slate popups).</summary>
    private static List<nint> PopupWindows(ComputerWindow subject)
    {
        var main = ParseHandle(subject.Id);
        var popups = new List<nint>();
        EnumWindows((handle, _) =>
        {
            if (handle != main && IsWindowVisible(handle) && !IsIconic(handle)
                && GetWindowThreadProcessId(handle, out var pid) != 0 && pid == (uint)subject.ProcessId
                && GetWindowRect(handle, out var rect) && rect.Right - rect.Left >= 8 && rect.Bottom - rect.Top >= 8)
                popups.Add(handle);
            return popups.Count < 6;
        }, 0);
        return popups;
    }

    private static bool IsMissumPoint(int x, int y)
    {
        var root = GetAncestor(WindowFromPoint(new Point(x, y)), 2);
        return root != 0 && GetWindowThreadProcessId(root, out var pid) != 0 && pid == (uint)Environment.ProcessId;
    }

    private static void EnsureNotMissum(nint handle)
    {
        if (GetWindowThreadProcessId(handle, out var pid) != 0 && pid == (uint)Environment.ProcessId)
            throw new UnauthorizedAccessException("Missum kann sich nicht selbst steuern.");
    }

    private static int GetForegroundProcessId() =>
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid) != 0 ? (int)pid : 0;

    private static void ApplyWindowAction(nint handle, string windowAction)
    {
        switch (windowAction)
        {
            case "focus": BringToFront(handle); break;
            case "maximize": ShowWindow(handle, 3); BringToFront(handle); break;
            case "minimize": ShowWindow(handle, 6); break;
            case "restore": ShowWindow(handle, 9); BringToFront(handle); break;
            case "close": PostMessage(handle, 0x0010, 0, 0); break;
            default: throw new ArgumentException("Unbekannte Fensteraktion.");
        }
    }

    /// <summary>Brings a window to the front even when another process owns the foreground.</summary>
    private static void BringToFront(nint handle)
    {
        if (handle == 0 || GetForegroundWindow() == handle) return;
        if (IsIconic(handle)) ShowWindow(handle, 9);
        // A synthetic Alt tap lets this process change the foreground window (Windows foreground lock).
        Send(new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = 0x12 } });
        Send(new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = 0x12, Flags = 0x0002 } });
        if (SetForegroundWindow(handle) == 0)
            BringWindowToTop(handle); // Fallback when Windows refuses the foreground switch.
        Thread.Sleep(120);
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
        if (desktop == 0) throw new InvalidOperationException("Der interaktive Desktop ist gesperrt oder nicht verfügbar (Sperrbildschirm oder UAC).");
        try
        {
            var name = new char[256];
            if (!GetUserObjectInformation(desktop, 2, name, 512, out _) || !string.Equals(new string(name).TrimEnd('\0'), "Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Windows zeigt gerade einen geschützten Desktop (Sperrbildschirm oder UAC); dort nimmt Windows keine Eingaben anderer Programme an.");
        }
        finally { CloseDesktop(desktop); }
    }

    private static void Move(int x, int y, DesktopCaptureTarget desktop) => Mouse(0x0001 | 0x8000 | 0x4000,
        0, (int)Math.Clamp(Math.Round(((double)x - desktop.X + 0.5) * 65536 / desktop.Width), 0, 65535),
        (int)Math.Clamp(Math.Round(((double)y - desktop.Y + 0.5) * 65536 / desktop.Height), 0, 65535));

    private static void Click(uint down, uint up) { Mouse(down); Mouse(up); }

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
            if (character == '\n') { PressKey("enter", cancellationToken); continue; }
            if (character == '\r') continue;
            if (character == '\t') { PressKey("tab", cancellationToken); continue; }
            var input = new Input { Type = 1, Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 } };
            Send(input); input.Keyboard.Flags |= 0x0002; Send(input);
        }
    }

    private static void PressKey(string combination, CancellationToken cancellationToken)
    {
        var keys = ComputerKeys.Parse(combination);
        var pressed = new List<ComputerKey>();
        try
        {
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key.Shift) { KeyDown(new ComputerKey(0x10, false)); pressed.Add(new ComputerKey(0x10, false)); }
                KeyDown(key);
                pressed.Add(key);
            }
        }
        finally
        {
            foreach (var key in pressed.AsEnumerable().Reverse()) KeyUp(key);
        }
    }

    private static void KeyDown(ComputerKey key) =>
        Send(new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key.VirtualKey, Flags = key.Extended ? 0x0001u : 0 } });

    private static void KeyUp(ComputerKey key) =>
        Send(new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key.VirtualKey, Flags = 0x0002u | (key.Extended ? 0x0001u : 0) } });

    private static void Send(Input input)
    {
        if (SendInput(1, in input, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows hat die Eingabe abgelehnt (z. B. Fenster mit Administratorrechten).");
    }

    // Minimal UIAutomation COM projections for point inspection. Unused slots preserve the vtable order.
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

    private delegate bool EnumWindowsProc(nint handle, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, in Input input, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, [Out] char[] info, int length, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
}
