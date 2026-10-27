using System.Globalization;

namespace Missum.App.Services;

/// <summary>Compact token counts for the native outputs sidebar.</summary>
internal static class NativeContextSummary
{
    internal static string Format(double used, double limit) =>
        Tokens(used) + " / " + (double.IsFinite(limit) && limit > 0 ? Tokens(limit) : "—");

    private static string Tokens(double value)
    {
        // Token progress can arrive before the effective limit. Do not imply a
        // default model limit or cap the actual measured use at that limit.
        var thousands = double.IsFinite(value) && value > 0 ? value / 1000 : 0;
        return Math.Round(thousands, 1, MidpointRounding.AwayFromZero)
            .ToString("0.#", CultureInfo.InvariantCulture) + "k";
    }
}
