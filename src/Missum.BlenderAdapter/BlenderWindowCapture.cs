using System.Runtime.InteropServices;

namespace Missum.BlenderAdapter;

/// <summary>What a window picture really shows: its size, how it was obtained and how much is on it.</summary>
internal sealed record WindowPicture(int Width, int Height, string Method, int ContentColors, bool ContentMissing)
{
    /// <summary>
    /// A picture without content means the window could not be read in this moment. Saying so is better
    /// than letting vision inspect a dark rectangle and call the scene checked.
    /// </summary>
    public string HonestNote => ContentMissing
        ? $"Das Bild enthält im geprüften Mittelbereich nur {ContentColors} verschiedene Farbwerte, also "
          + "praktisch keinen Inhalt. Der Fensterinhalt war in diesem Augenblick nicht lesbar; Blender in den "
          + "Vordergrund holen und die Aufnahme einmal wiederholen. Missum hat am Fenster nichts verändert."
        : $"Das Fenster selbst wurde aufgenommen, {ContentColors} verschiedene Farbwerte im geprüften Mittelbereich.";
}

/// <summary>
/// Pictures of a Blender window that Missum does not control. Nothing is installed inside Blender:
/// the window is asked to paint itself into a memory bitmap.
/// </summary>
internal static class BlenderWindowCapture
{
    internal const string WindowMethod = "fensterdruck";
    private const uint PrintWindowFullContent = 0x0000_0002;
    private const int MaximumLongSide = 2_048;

    /// <summary>
    /// A viewport that painted anything shows gradients, edges and objects. Fewer distinct colour values
    /// than this in the middle of the picture mean the surface stayed empty.
    /// </summary>
    private const int MinimumContentColors = 12;

    public static WindowPicture Save(nint handle, string target)
    {
        if (handle == 0)
            throw new BlenderToolException("blender.window_missing",
                "Zu dieser Blender-Instanz gehört kein Fenster mehr. blender.status listet die offenen Fenster.");
        if (BlenderProcessWatch.IsMinimized(handle))
            throw new BlenderToolException("blender.window_minimized",
                "Das Blender-Fenster ist minimiert und kann nicht gesehen werden. Das Fenster wiederherstellen "
                + "und die Aufnahme einmal wiederholen; Missum ändert dafür nichts am Fenster des Nutzers.");
        if (!BlenderProcessWatch.TryGetBounds(handle, out _, out _, out var width, out var height))
            throw new BlenderToolException("blender.window_unavailable",
                "Die Größe des Blender-Fensters ließ sich nicht bestimmen. Fenster prüfen und erneut aufnehmen.");
        if (width > 8_192 || height > 8_192)
            throw new BlenderToolException("blender.window_too_large",
                $"Das Fenster ist mit {width}×{height} Pixeln zu groß für eine Aufnahme.");

        var pixels = PaintIntoBitmap(handle, width, height, out var printed)
            ?? throw new BlenderToolException("blender.capture_failed",
                "Das Blender-Fenster konnte nicht in einen Speicherbereich gezeichnet werden. "
                + "Erneut aufnehmen oder blender.status mit connect=true ein eigenes Arbeitsfenster öffnen lassen.");
        var scaled = ScaleDown(pixels, width, height, out var finalWidth, out var finalHeight);
        PortablePng.SaveRgba(target, finalWidth, finalHeight, Rgba(scaled));
        // Counted on the picture that is delivered, because that is the picture vision will look at.
        var delivered = CountContentColors(scaled, finalWidth, finalHeight);
        return new WindowPicture(finalWidth, finalHeight, WindowMethod, delivered, !printed || delivered < MinimumContentColors);
    }

    /// <summary>Asks the window to draw itself into a 32-bit bitmap and returns the raw BGRA bytes.</summary>
    private static byte[]? PaintIntoBitmap(nint handle, int width, int height, out bool printed)
    {
        printed = false;
        var windowDc = GetWindowDC(handle);
        if (windowDc == 0) return null;
        nint memoryDc = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            memoryDc = CreateCompatibleDC(windowDc);
            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (memoryDc == 0 || bitmap == 0) return null;
            previous = SelectObject(memoryDc, bitmap);
            printed = PrintWindow(handle, memoryDc, PrintWindowFullContent);
            return ReadBits(memoryDc, bitmap, width, height);
        }
        finally
        {
            if (previous != 0 && bitmap != 0) _ = SelectObject(memoryDc, previous);
            if (bitmap != 0) _ = DeleteObject(bitmap);
            if (memoryDc != 0) _ = DeleteDC(memoryDc);
            _ = ReleaseDC(handle, windowDc);
        }
    }

    private static byte[]? ReadBits(nint memoryDc, nint bitmap, int width, int height)
    {
        var info = new BitmapInfo
        {
            HeaderSize = (uint)Marshal.SizeOf<BitmapInfo>(),
            Width = width,
            // Negative height means top-down rows, exactly the order the PNG writer expects.
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        var pixels = new byte[width * height * 4];
        return GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, 0) <= 0 ? null : pixels;
    }

    /// <summary>Nearest-neighbour shrink so a huge window still fits into one vision upload.</summary>
    private static byte[] ScaleDown(byte[] source, int width, int height, out int finalWidth, out int finalHeight)
    {
        finalWidth = width;
        finalHeight = height;
        var longest = Math.Max(width, height);
        if (longest <= MaximumLongSide) return source;
        var factor = MaximumLongSide / (double)longest;
        finalWidth = Math.Max(160, (int)Math.Round(width * factor));
        finalHeight = Math.Max(160, (int)Math.Round(height * factor));
        var target = new byte[finalWidth * finalHeight * 4];
        for (var row = 0; row < finalHeight; row++)
        {
            var sourceRow = Math.Min(height - 1, row * height / finalHeight);
            for (var column = 0; column < finalWidth; column++)
            {
                var sourceColumn = Math.Min(width - 1, column * width / finalWidth);
                var from = (sourceRow * width + sourceColumn) * 4;
                var to = (row * finalWidth + column) * 4;
                target[to] = source[from];
                target[to + 1] = source[from + 1];
                target[to + 2] = source[from + 2];
                target[to + 3] = 255;
            }
        }

        return target;
    }

    /// <summary>Windows hands out BGRA, PNG wants RGBA: swap the two colour channels.</summary>
    private static byte[] Rgba(byte[] source)
    {
        var result = new byte[source.Length];
        for (var index = 0; index + 3 < source.Length; index += 4)
        {
            result[index] = source[index + 2];
            result[index + 1] = source[index + 1];
            result[index + 2] = source[index];
            result[index + 3] = 255;
        }

        return result;
    }

    /// <summary>
    /// Counts distinct colour values in the middle half of the picture. The outer border is left out on
    /// purpose: window frame and title bar paint themselves even when the view inside stayed empty.
    /// </summary>
    private static int CountContentColors(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length < 4) return 0;
        var left = width / 4;
        var right = Math.Max(left + 1, width * 3 / 4);
        var top = height / 4;
        var bottom = Math.Max(top + 1, height * 3 / 4);
        var seen = new HashSet<int>();
        for (var row = top; row < bottom; row += 3)
        {
            var offset = (row * width + left) * 4;
            for (var column = left; column < right; column++, offset += 4)
            {
                if (offset + 2 >= pixels.Length) break;
                // Five bits per channel: a painted view keeps its gradients, an empty surface stays one bucket.
                seen.Add(((pixels[offset] >> 3) << 10) | ((pixels[offset + 1] >> 3) << 5) | (pixels[offset + 2] >> 3));
            }
        }

        return seen.Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint item);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint bitmap, uint startScanLine, uint lineCount,
        byte[] pixels, ref BitmapInfo info, uint usage);
}
