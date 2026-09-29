using CSharpMath.Atom;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using System.Text;

namespace Missum.App.Controls;

/// <summary>A transparent, two-pixel-per-DIP formula image. Treat Png as read-only.</summary>
public sealed record NativeMathBitmap(byte[] Png, double Width, double Height, string? Error);

/// <summary>Typesets LaTeX without a browser or a network dependency.</summary>
public static class NativeMathRenderer
{
    private const int MaximumFormulaLength = 8192;
    private const int MaximumNesting = 32;
    private const int MaximumPixelWidth = 8192;
    private const int MaximumPixelHeight = 4096;
    private const long MaximumPixelCount = 8 * 1024 * 1024;
    private const int MaximumCacheEntries = 64;
    private const int MaximumCacheBytes = 8 * 1024 * 1024;
    private const float PixelScale = 2;
    private const float Padding = 3;
    private static readonly object Gate = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CacheItem>> Cache = [];
    private static readonly LinkedList<CacheItem> Recency = new();
    private static readonly Lazy<HashSet<string>> KnownCommands = new(() =>
        LaTeXSettings.Commands.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal));
    private static int _cachedBytes;

    public static NativeMathBitmap Render(string latex, bool display, double fontSize = 18, bool dark = true)
    {
        if (string.IsNullOrWhiteSpace(latex)) return Failure("Die Formel ist leer.");
        if (latex.Length > MaximumFormulaLength) return Failure("Die Formel ist zu lang für die Vorschau.");
        latex = NormalizeEscapedLatex(latex);
        if (ExceedsNestingLimit(latex)) return Failure("Die Formel ist zu tief verschachtelt für die Vorschau.");
        if (!double.IsFinite(fontSize) || fontSize is < 8 or > 64)
            return Failure("Die Schriftgröße der Formel liegt außerhalb des unterstützten Bereichs.");

        var key = new CacheKey(latex, display, fontSize, dark);
        // CSharpMath shares font/typeface caches. Serialize rasterization as well as the small LRU.
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Recency.Remove(cached);
                Recency.AddFirst(cached);
                return cached.Value.Image;
            }

            var image = RenderCore(key);
            var node = Recency.AddFirst(new CacheItem(key, image));
            Cache.Add(key, node);
            _cachedBytes += image.Png.Length;
            while (Cache.Count > MaximumCacheEntries || _cachedBytes > MaximumCacheBytes)
            {
                var oldest = Recency.Last!;
                _cachedBytes -= oldest.Value.Image.Png.Length;
                Cache.Remove(oldest.Value.Key);
                Recency.RemoveLast();
            }
            return image;
        }
    }

    private static NativeMathBitmap RenderCore(CacheKey key)
    {
        try
        {
            var painter = new MathPainter
            {
                FontSize = (float)key.FontSize,
                LineStyle = key.Display ? LineStyle.Display : LineStyle.Text,
                TextColor = key.Dark ? new SKColor(242, 242, 246) : new SKColor(32, 32, 36),
                DisplayErrorInline = false,
                AntiAlias = true,
                LaTeX = NormalizeCommandBoundaries(key.Latex)
            };
            if (!string.IsNullOrWhiteSpace(painter.ErrorMessage))
                return Failure("Formel konnte nicht dargestellt werden: " + ShortError(painter.ErrorMessage));

            var bounds = painter.Measure();
            var width = Math.Ceiling((bounds.Width + Padding * 2) * PixelScale);
            var height = Math.Ceiling((bounds.Height + Padding * 2) * PixelScale);
            if (!double.IsFinite(width) || !double.IsFinite(height) || bounds.Width <= 0 || bounds.Height <= 0)
                return Failure("Die Formel enthält keinen darstellbaren Inhalt.");
            if (width > MaximumPixelWidth || height > MaximumPixelHeight || width * height > MaximumPixelCount)
                return Failure("Die Formel ist zu groß für die Vorschau.");

            using var surface = SKSurface.Create(new SKImageInfo((int)width, (int)height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return Failure("Die Formelvorschau konnte nicht erstellt werden.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(PixelScale);
            // Measure reports a negative top because Draw(x, y) takes a baseline position.
            painter.Draw(canvas, Padding - bounds.Left, Padding - bounds.Top);
            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null) return Failure("Die Formelvorschau konnte nicht gespeichert werden.");
            return new NativeMathBitmap(encoded.ToArray(), width / PixelScale, height / PixelScale, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A malformed or unsupported expression must never break the surrounding conversation.
            return Failure("Formel konnte nicht dargestellt werden: " + ShortError(exception.Message));
        }
    }

    private static bool ExceedsNestingLimit(string latex)
    {
        var depth = 0;
        for (var index = 0; index < latex.Length; index++)
        {
            if (latex[index] == '\\')
            {
                if (index + 1 < latex.Length && latex[index + 1] is '{' or '}' or '\\') index++;
                continue;
            }
            if (latex[index] == '{' && ++depth > MaximumNesting) return true;
            if (latex[index] == '}' && depth > 0) depth--;
        }
        return false;
    }

    private static string NormalizeEscapedLatex(string latex)
    {
        // Some model replies retain JSON escaping. Decode only when all commands have
        // doubled slashes; genuine aligned/matrix row breaks must remain untouched.
        var escapedCommands = false;
        for (var index = 0; index < latex.Length; index++)
        {
            if (latex[index] != '\\') continue;
            var start = index;
            while (index < latex.Length && latex[index] == '\\') index++;
            var slashes = index - start;
            if (index >= latex.Length || !char.IsAsciiLetter(latex[index])) continue;
            if (slashes % 2 == 1) return latex;
            if (slashes == 2 && index + 1 < latex.Length && char.IsAsciiLetter(latex[index + 1])) escapedCommands = true;
        }
        if (!escapedCommands) return latex;

        var normalized = new StringBuilder(latex.Length);
        for (var index = 0; index < latex.Length; index++)
        {
            if (latex[index] != '\\')
            {
                normalized.Append(latex[index]);
                continue;
            }
            var start = index;
            while (index < latex.Length && latex[index] == '\\') index++;
            var slashes = index - start;
            var commandFollows = index < latex.Length &&
                (char.IsAsciiLetter(latex[index]) || latex[index] is ',' or ';' or '!' or ':' or '{' or '}');
            normalized.Append('\\', slashes >= 4 && slashes % 2 == 0 ? slashes / 2 : slashes == 2 && commandFollows ? 1 : slashes);
            index--;
        }
        return normalized.ToString();
    }

    private static string NormalizeCommandBoundaries(string latex)
    {
        // CSharpMath's command scanner also consumes =, * and ' after a control word.
        // TeX treats these as following tokens (e.g. \alpha=1 and \beta'). An ignored
        // command-terminating space preserves their meaning without rewriting text
        // or changing any genuinely registered suffixed command.
        var normalized = new StringBuilder(latex.Length);
        for (var index = 0; index < latex.Length; index++)
        {
            if (latex[index] != '\\' || index + 1 >= latex.Length)
            {
                normalized.Append(latex[index]);
                continue;
            }

            var start = index;
            normalized.Append(latex[index++]);
            if (!char.IsAsciiLetter(latex[index]) && latex[index] != '@')
            {
                normalized.Append(latex[index]);
                continue;
            }
            while (index < latex.Length && (char.IsAsciiLetter(latex[index]) || latex[index] == '@'))
                normalized.Append(latex[index++]);
            if (index < latex.Length && latex[index] is '=' or '*' or '\'')
            {
                var command = latex[start..index];
                if (KnownCommands.Value.Contains(command) && !KnownCommands.Value.Contains(command + latex[index]))
                    normalized.Append(' ');
            }
            index--;
        }
        return normalized.ToString();
    }

    private static string ShortError(string value)
    {
        var line = value.Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Unbekannter Fehler.";
        return line.Length <= 180 ? line : line[..180] + "…";
    }

    private static NativeMathBitmap Failure(string message) => new([], 0, 0, message);
    private readonly record struct CacheKey(string Latex, bool Display, double FontSize, bool Dark);
    private sealed record CacheItem(CacheKey Key, NativeMathBitmap Image);
}
