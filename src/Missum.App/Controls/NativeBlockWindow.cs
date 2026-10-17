namespace Missum.App.Controls;

/// <summary>Pure geometry for the native, two-screen overscan window.</summary>
internal static class NativeBlockWindow
{
    internal static (int First, int Last) Select(double[] offsets, double top, double screen)
    {
        var count = offsets.Length - 1;
        if (count <= 0 || top + screen * 3 < 0) return (0, 0);
        if (top - screen * 2 > offsets[^1]) return (count, count);
        static int Find(double[] values, double offset)
        {
            var index = Array.BinarySearch(values, offset);
            return Math.Clamp(index >= 0 ? index : ~index - 1, 0, Math.Max(0, values.Length - 2));
        }
        var first = Find(offsets, Math.Max(0, top - screen * 2));
        var last = Math.Min(count, Find(offsets, top + screen * 3) + 1);
        return (first, Math.Max(first, last));
    }
}
