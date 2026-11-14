using System.Runtime.InteropServices;

namespace Missum.ComputerControl.TestTarget;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var title = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_WINDOW") ?? "Missum Computer Test";
        var window = CreateWindowEx(0, "STATIC", title, 0x00CF0000 | 0x10000000,
            160, 160, 1100, 600, 0, 0, 0, 0);
        if (window == 0) return 2;
        nint input;
        if (Environment.GetEnvironmentVariable("MISSUM_COMPUTER_SCROLL_TEST") == "1")
        {
            _ = CreateWindowEx(0, "STATIC", "Scrolltest: Liste mit nummerierten Zeilen", 0x50000000,
                40, 22, 850, 28, window, 0, 0, 0);
            input = CreateWindowEx(0x00000200, "LISTBOX", "", 0x50A10101,
                40, 60, 850, 400, window, (nint)1003, 0, 0);
            for (var index = 1; index <= 120; index++)
                _ = SendMessage(input, 0x0180, 0, $"Zeile {index:000} - sichtbarer Testinhalt");
        }
        else
        {
            input = CreateWindowEx(0x00000200, "EDIT", "", 0x50010080,
                40, 60, 850, 60, window, (nint)1001, 0, 0);
            _ = CreateWindowEx(0, "BUTTON", "Testoption", 0x50010000,
                40, 160, 220, 48, window, (nint)1002, 0, 0);
        }
        if (Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_NO_ACTIVATE") != "1")
        {
            _ = SetForegroundWindow(window);
            _ = SetFocus(input);
        }
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            _ = TranslateMessage(in message);
            _ = DispatchMessage(in message);
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window; public uint Value; public nuint WParam; public nint LParam;
        public uint Time; public int X, Y; public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern int SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint wParam, string lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern int TranslateMessage(in Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(in Message message);
}
