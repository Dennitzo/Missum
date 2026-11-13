<# Messfrage fuer Phase 2: Liefert PrintWindow den Blender-Viewport, oder nur das
   Bildschirmgebiet an der Fensterstelle zeigt ihn? Zaehlt verschiedene Farben im
   Mittelbereich beider Aufnahmen. Aendert nichts am Projekt. #>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$native = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CapProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint Size; public int Width; public int Height; public short Planes; public short BitCount;
        public uint Compression; public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
        public uint ClrUsed; public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint Masks; }

    private static byte[] Grab(IntPtr windowDc, int width, int height, bool printWindow, IntPtr hwnd, int srcX, int srcY)
    {
        IntPtr mem = CreateCompatibleDC(windowDc);
        IntPtr bmp = CreateCompatibleBitmap(windowDc, width, height);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (printWindow) { if (!PrintWindow(hwnd, mem, 2)) { return null; } }
            else { if (!BitBlt(mem, 0, 0, width, height, windowDc, srcX, srcY, 0x00CC0020)) { return null; } }
            BITMAPINFO info = new BITMAPINFO();
            info.Header.Size = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            info.Header.Width = width;
            info.Header.Height = -height;
            info.Header.Planes = 1;
            info.Header.BitCount = 32;
            byte[] pixels = new byte[width * height * 4];
            int lines = GetDIBits(mem, bmp, 0, (uint)height, pixels, ref info, 0);
            if (lines <= 0) { return null; }
            return pixels;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
        }
    }

    public static string Probe(IntPtr hwnd, string printPath, string screenPath)
    {
        SetProcessDPIAware();
        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) { return "kein Fensterrechteck"; }
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        IntPtr windowDc = GetWindowDC(hwnd);
        int printColors = -1;
        int screenColors = -1;
        try
        {
            byte[] printed = Grab(windowDc, width, height, true, hwnd, 0, 0);
            if (printed != null)
            {
                printColors = CountColors(printed, width, height);
                Save(printed, width, height, printPath);
            }
        }
        finally { ReleaseDC(hwnd, windowDc); }

        SetForegroundWindow(hwnd);
        System.Threading.Thread.Sleep(600);
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            byte[] shot = Grab(screen, width, height, false, hwnd, rect.Left, rect.Top);
            if (shot != null)
            {
                screenColors = CountColors(shot, width, height);
                Save(shot, width, height, screenPath);
            }
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }

        return "rechteck=" + width + "x" + height
            + " farbenFensterdruck=" + printColors
            + " farbenBildschirm=" + screenColors;
    }

    private static int CountColors(byte[] pixels, int width, int height)
    {
        System.Collections.Generic.HashSet<int> seen = new System.Collections.Generic.HashSet<int>();
        int x0 = width / 4, x1 = width * 3 / 4, y0 = height / 4, y1 = height * 3 / 4;
        for (int y = y0; y < y1; y += 3)
        {
            for (int x = x0; x < x1; x += 3)
            {
                int i = (y * width + x) * 4;
                int key = (pixels[i] >> 3) << 10 | (pixels[i + 1] >> 3) << 5 | (pixels[i + 2] >> 3);
                seen.Add(key);
            }
        }
        return seen.Count;
    }

    private static void Save(byte[] pixels, int width, int height, string path)
    {
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Bitmap bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            bmp.Save(path, ImageFormat.Png);
            bmp.Dispose();
        }
        finally { handle.Free(); }
    }
}
'@

Add-Type -TypeDefinition $native -ReferencedAssemblies System.Drawing

$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$outDir = Join-Path 'C:\Users\AMD\Documents\GitHub\Missum\tmp' 'capture-compare'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$running = Get-Process -Name blender -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($null -eq $running) {
  Write-Host 'Blender wird gestartet (wie von Hand geoeffnet, ohne Add-on)'
  $started = Start-Process -FilePath $blender -PassThru
  for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $running = Get-Process -Id $started.Id -ErrorAction SilentlyContinue
    if ($null -ne $running -and $running.MainWindowHandle -ne 0) { break }
  }
}

if ($null -eq $running -or $running.MainWindowHandle -eq 0) { Write-Host 'FEHLER: kein Blender-Fenster'; exit 1 }
Write-Host ("PID=" + $running.Id + " Titel='" + $running.MainWindowTitle + "'")

# Dem Viewport Zeit fuer mehrere gezeichnete Bilder lassen.
Start-Sleep -Seconds 4
$report = [CapProbe]::Probe($running.MainWindowHandle, (Join-Path $outDir 'fensterdruck.png'), (Join-Path $outDir 'bildschirmbereich.png'))
Write-Host $report

foreach ($name in 'fensterdruck', 'bildschirmbereich') {
  $file = Join-Path $outDir ($name + '.png')
  if (Test-Path $file) { Write-Host ($name + '.png bytes=' + (Get-Item $file).Length) } else { Write-Host ($name + '.png fehlt') }
}
exit 0
