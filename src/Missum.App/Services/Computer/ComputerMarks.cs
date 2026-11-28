namespace Missum.App.Services;

/// <summary>A numbered target drawn on the observation image (image pixel coordinates).</summary>
internal readonly record struct ComputerMark(int Index, int X, int Y, int Width, int Height);

/// <summary>
/// Set-of-Mark rendering for desktop observations (the robust browser-use pattern applied to
/// Windows): every UI Automation target gets a coloured outline and a number label, so a vision
/// model can act on "element 12" instead of guessing pixel coordinates. Images are BGRA.
/// </summary>
internal static class ComputerMarks
{
    // Distinct, high-contrast outline colours (B, G, R).
    private static readonly (byte B, byte G, byte R)[] Palette =
    [
        (0x00, 0x8C, 0xFF), (0x30, 0xC8, 0x00), (0xFF, 0x50, 0x2A), (0xD0, 0x20, 0xC8),
        (0x00, 0xC8, 0xE6), (0x10, 0x10, 0xE6), (0xE0, 0xA0, 0x00), (0x80, 0x40, 0xFF),
    ];

    // 5x7 pixel digits.
    private static readonly string[] Digits =
    [
        "01110100011001110101110011000101110", "00100011000010000100001000010001110",
        "01110100010000100010001000100011111", "11110000010000101110000010000111110",
        "00010001100101010010111110001000010", "11111100001111000001000011000101110",
        "00110010001000011110100011000101110", "11111000010001000100010000100001000",
        "01110100011000101110100011000101110", "01110100011000101111000010001001100",
    ];

    /// <summary>Area-averaging downscale (sharp, readable text) or copy when no scaling is needed.</summary>
    public static byte[] Resample(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.LongLength != (long)sourceWidth * sourceHeight * 4 || width <= 0 || height <= 0)
            throw new ArgumentException("Ungültige Bildmaße.");
        if (width == sourceWidth && height == sourceHeight) return (byte[])source.Clone();
        var output = new byte[checked(width * height * 4)];
        var xScale = (double)sourceWidth / width;
        var yScale = (double)sourceHeight / height;
        for (var y = 0; y < height; y++)
        {
            var top = (int)(y * yScale);
            var bottom = Math.Max(top + 1, Math.Min(sourceHeight, (int)Math.Ceiling((y + 1) * yScale)));
            for (var x = 0; x < width; x++)
            {
                var left = (int)(x * xScale);
                var right = Math.Max(left + 1, Math.Min(sourceWidth, (int)Math.Ceiling((x + 1) * xScale)));
                long b = 0, g = 0, r = 0, a = 0;
                for (var sy = top; sy < bottom; sy++)
                {
                    var offset = (sy * sourceWidth + left) * 4;
                    for (var sx = left; sx < right; sx++, offset += 4)
                    {
                        b += source[offset]; g += source[offset + 1]; r += source[offset + 2]; a += source[offset + 3];
                    }
                }
                var count = (right - left) * (bottom - top);
                var target = (y * width + x) * 4;
                output[target] = (byte)(b / count); output[target + 1] = (byte)(g / count);
                output[target + 2] = (byte)(r / count); output[target + 3] = (byte)(a / count);
            }
        }
        return output;
    }

    /// <summary>Draws outlines and number labels for every mark onto a BGRA image in place.</summary>
    public static void Draw(byte[] image, int width, int height, IEnumerable<ComputerMark> marks)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.LongLength != (long)width * height * 4) throw new ArgumentException("Ungültige Bildmaße.");
        foreach (var mark in marks)
        {
            var color = Palette[(mark.Index - 1) % Palette.Length];
            Outline(image, width, height, mark.X, mark.Y, mark.Width, mark.Height, color);
            Label(image, width, height, mark, color);
        }
    }

    /// <summary>
    /// Coordinate grid for windows without accessible elements (Unreal viewport, games, canvas apps):
    /// dotted lines every 100 normalized1000 units with their value at the top and left edge.
    /// </summary>
    public static void DrawGrid(byte[] image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.LongLength != (long)width * height * 4) throw new ArgumentException("Ungültige Bildmaße.");
        (byte B, byte G, byte R) line = (0xF0, 0xE0, 0x30);
        for (var step = 1; step < 10; step++)
        {
            var x = (int)Math.Round(width * step / 10d);
            var y = (int)Math.Round(height * step / 10d);
            for (var py = 0; py < height; py += 3) if (x < width) Set(image, width, x, py, line);
            for (var px = 0; px < width; px += 3) if (y < height) Set(image, width, px, y, line);
            Label(image, width, height, new ComputerMark(step * 100, x + 2, 18, 1, 1), line);
            Label(image, width, height, new ComputerMark(step * 100, 2, y + 18, 1, 1), line);
        }
    }

    private static void Outline(byte[] image, int width, int height, int x, int y, int w, int h, (byte B, byte G, byte R) color)
    {
        for (var thickness = 0; thickness < 2; thickness++)
        {
            HorizontalLine(image, width, height, x, x + w - 1, y + thickness, color);
            HorizontalLine(image, width, height, x, x + w - 1, y + h - 1 - thickness, color);
            VerticalLine(image, width, height, x + thickness, y, y + h - 1, color);
            VerticalLine(image, width, height, x + w - 1 - thickness, y, y + h - 1, color);
        }
    }

    private static void Label(byte[] image, int width, int height, ComputerMark mark, (byte B, byte G, byte R) color)
    {
        var text = mark.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const int scale = 2, glyphWidth = 5 * scale, glyphHeight = 7 * scale, gap = scale, padding = 2;
        var labelWidth = text.Length * glyphWidth + (text.Length - 1) * gap + padding * 2;
        var labelHeight = glyphHeight + padding * 2;
        // Above the top-left corner when there is room, otherwise inside the box.
        var left = Math.Clamp(mark.X, 0, Math.Max(0, width - labelWidth));
        var top = mark.Y - labelHeight >= 0 ? mark.Y - labelHeight : Math.Clamp(mark.Y, 0, Math.Max(0, height - labelHeight));
        Fill(image, width, height, left, top, labelWidth, labelHeight, color);
        // Dark digits on light labels, white digits on dark ones.
        var luminance = 0.0722 * color.B + 0.7152 * color.G + 0.2126 * color.R;
        var ink = luminance > 140 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255);
        var cursor = left + padding;
        foreach (var character in text)
        {
            var glyph = Digits[character - '0'];
            for (var row = 0; row < 7; row++)
                for (var column = 0; column < 5; column++)
                    if (glyph[row * 5 + column] == '1')
                        Fill(image, width, height, cursor + column * scale, top + padding + row * scale, scale, scale, ink);
            cursor += glyphWidth + gap;
        }
    }

    private static void HorizontalLine(byte[] image, int width, int height, int x0, int x1, int y, (byte B, byte G, byte R) color)
    {
        if (y < 0 || y >= height) return;
        for (var x = Math.Max(0, x0); x <= Math.Min(width - 1, x1); x++) Set(image, width, x, y, color);
    }

    private static void VerticalLine(byte[] image, int width, int height, int x, int y0, int y1, (byte B, byte G, byte R) color)
    {
        if (x < 0 || x >= width) return;
        for (var y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++) Set(image, width, x, y, color);
    }

    private static void Fill(byte[] image, int width, int height, int x, int y, int w, int h, (byte B, byte G, byte R) color)
    {
        for (var row = Math.Max(0, y); row < Math.Min(height, y + h); row++)
            for (var column = Math.Max(0, x); column < Math.Min(width, x + w); column++) Set(image, width, column, row, color);
    }

    private static void Set(byte[] image, int width, int x, int y, (byte B, byte G, byte R) color)
    {
        var offset = (y * width + x) * 4;
        image[offset] = color.B; image[offset + 1] = color.G; image[offset + 2] = color.R; image[offset + 3] = 255;
    }
}
