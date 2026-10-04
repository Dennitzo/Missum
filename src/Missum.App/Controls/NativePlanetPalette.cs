using Windows.UI;

namespace Missum.App.Controls;

/// <summary>Deterministic native planet identities. The initial palette contains 4,096 appearances.</summary>
internal static class NativePlanetPalette
{
    internal const int PaletteCount = 4096;
    internal const string PaletteVersion = "planets-v1";

    private static readonly Color[] Families =
    [
        Rgb(76, 159, 239), Rgb(237, 126, 152), Rgb(224, 169, 65), Rgb(83, 195, 161),
        Rgb(165, 128, 235), Rgb(230, 116, 65), Rgb(84, 189, 218), Rgb(205, 139, 207),
        Rgb(137, 184, 77), Rgb(218, 179, 124), Rgb(111, 132, 235), Rgb(229, 100, 124),
        Rgb(98, 200, 191), Rgb(183, 174, 236), Rgb(197, 183, 76), Rgb(140, 171, 203),
    ];

    // Later identities use an injective RGB mapping, rather than cycling back through the palette.
    // Reserve the curated colors that fall in this half of RGB space.
    private static readonly int[] ReservedColors = Families
        .Where(color => color.R >= 128)
        .Select(color => ((color.R - 128) << 16) | (color.G << 8) | color.B)
        .Order()
        .ToArray();

    private static readonly int ExtendedCapacity = 0x800000 - ReservedColors.Length;
    private static readonly int ExtendedMultiplier = CoprimeMultiplier(ExtendedCapacity);

    internal static Appearance Describe(int planetIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(planetIndex);
        // This bijection distributes colors, surfaces, rings and satellites even among the first agents.
        var mixed = ((planetIndex & 4095) * 1193 + 417) & 4095;
        var family = mixed >> 8;
        var colorKey = (planetIndex >> 12) * 16 + family;
        var body = colorKey < Families.Length ? Families[colorKey] : ExtendedColor(colorKey);
        return new(planetIndex, family, mixed & 7, (mixed >> 3) & 7, (mixed >> 6) & 3,
            body, Mix(body, Rgb(255, 250, 240), .42), Mix(body, Rgb(18, 25, 49), .47),
            Mix(body, Rgb(231, 245, 250), .65));
    }

    internal static string VisualSignatureFor(int planetIndex)
    {
        var appearance = Describe(planetIndex);
        return $"{appearance.Body.R:X2}{appearance.Body.G:X2}{appearance.Body.B:X2}:{appearance.Surface}:{appearance.Orbit}:{appearance.Satellite}";
    }

    internal static Color Mix(Color first, Color second, double amount) => Rgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static Color ExtendedColor(int colorKey)
    {
        var rank = (int)(((long)(colorKey - Families.Length) * ExtendedMultiplier) % ExtendedCapacity);
        foreach (var reserved in ReservedColors)
        {
            if (rank < reserved) break;
            rank++;
        }
        return Rgb((byte)(128 + (rank >> 16)), (byte)(rank >> 8), (byte)rank);
    }

    private static int CoprimeMultiplier(int capacity)
    {
        var multiplier = 7919;
        while (GreatestCommonDivisor(multiplier, capacity) != 1) multiplier += 2;
        return multiplier;
    }

    private static int GreatestCommonDivisor(int first, int second)
    {
        while (second != 0) (first, second) = (second, first % second);
        return first;
    }

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromArgb(255, red, green, blue);

    internal readonly record struct Appearance(int Index, int Family, int Surface, int Orbit, int Satellite,
        Color Body, Color Light, Color Dark, Color Accent);
}
