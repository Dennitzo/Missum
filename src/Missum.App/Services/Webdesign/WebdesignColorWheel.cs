using System.Globalization;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// Colour wheel of the fine-tuning: hue runs clockwise from red at the top (yellow, green,
/// cyan at the bottom, blue, magenta), saturation grows from white in the centre to the rim.
/// Brightness is a separate value, so dark text colours stay reachable.
/// </summary>
public static class WebdesignColorWheel
{
    /// <summary>Colour at a point of a wheel with the given diameter, or null outside the circle.</summary>
    public static (byte R, byte G, byte B)? ColorAt(double x, double y, double diameter, double brightness = 1)
    {
        var radius = diameter / 2;
        var dx = x - radius;
        var dy = y - radius;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > radius + 0.5) return null;
        var hue = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        return FromHsv(hue, Math.Clamp(distance / radius, 0, 1), Math.Clamp(brightness, 0, 1));
    }

    /// <summary>Where a colour sits on the wheel (for the marker) and its brightness.</summary>
    public static (double X, double Y, double Brightness) Locate(byte r, byte g, byte b, double diameter)
    {
        var max = Math.Max(r, Math.Max(g, b)) / 255d;
        var min = Math.Min(r, Math.Min(g, b)) / 255d;
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            var (rf, gf, bf) = (r / 255d, g / 255d, b / 255d);
            hue = max == rf ? 60 * (((gf - bf) / delta) % 6) : max == gf ? 60 * ((bf - rf) / delta + 2) : 60 * ((rf - gf) / delta + 4);
            if (hue < 0) hue += 360;
        }
        var saturation = max <= 0 ? 0 : delta / max;
        var radius = diameter / 2;
        var angle = hue * Math.PI / 180;
        return (radius + Math.Sin(angle) * saturation * radius, radius - Math.Cos(angle) * saturation * radius, max);
    }

    public static (byte R, byte G, byte B) FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return (Channel(r + m), Channel(g + m), Channel(b + m));
    }

    public static string Hex((byte R, byte G, byte B) color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    /// <summary>#RGB or #RRGGBB (with or without #) as a colour, or null.</summary>
    public static (byte R, byte G, byte B)? ParseHex(string? text)
    {
        var value = (text ?? "").Trim().TrimStart('#');
        if (value.Length == 3) value = string.Concat(value.Select(static c => new string(c, 2)));
        if (value.Length != 6 || !value.All(char.IsAsciiHexDigit)) return null;
        return (Convert.ToByte(value[..2], 16), Convert.ToByte(value[2..4], 16), Convert.ToByte(value[4..6], 16));
    }

    /// <summary>The wheel as premultiplied BGRA pixels (WriteableBitmap), transparent outside.</summary>
    public static byte[] Bgra(int diameter, double brightness = 1)
    {
        var pixels = new byte[diameter * diameter * 4];
        var radius = diameter / 2d;
        for (var y = 0; y < diameter; y++)
            for (var x = 0; x < diameter; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x + 0.5 - radius, 2) + Math.Pow(y + 0.5 - radius, 2));
                var alpha = Math.Clamp(radius - distance + 0.5, 0, 1); // soft one-pixel edge
                if (alpha <= 0) continue;
                var hue = (Math.Atan2(x + 0.5 - radius, -(y + 0.5 - radius)) * 180 / Math.PI + 360) % 360;
                var (r, g, b) = FromHsv(hue, Math.Min(1, distance / radius), Math.Clamp(brightness, 0, 1));
                var index = (y * diameter + x) * 4;
                pixels[index] = (byte)Math.Round(b * alpha);
                pixels[index + 1] = (byte)Math.Round(g * alpha);
                pixels[index + 2] = (byte)Math.Round(r * alpha);
                pixels[index + 3] = (byte)Math.Round(255 * alpha);
            }
        return pixels;
    }
}
