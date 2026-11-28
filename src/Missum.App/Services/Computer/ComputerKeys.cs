using System.Runtime.InteropServices;

namespace Missum.App.Services;

/// <summary>One virtual key of a combination, with the extended-key flag Windows expects for navigation keys.</summary>
internal readonly record struct ComputerKey(ushort VirtualKey, bool Extended, bool Shift = false);

/// <summary>
/// Parses key combinations such as "ctrl+s", "alt+f4", "win+r", "f5", "ctrl+shift+esc" or "/" into
/// virtual keys. Covers modifiers, function keys F1–F24, navigation, numpad, media keys and every
/// printable character of the active keyboard layout.
/// </summary>
internal static class ComputerKeys
{
    public const int MaximumKeys = 5;

    private static readonly Dictionary<string, ComputerKey> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = new(0x11, false), ["control"] = new(0x11, false), ["strg"] = new(0x11, false),
        ["shift"] = new(0x10, false), ["umschalt"] = new(0x10, false),
        ["alt"] = new(0x12, false), ["altgr"] = new(0xA5, true),
        ["win"] = new(0x5B, true), ["windows"] = new(0x5B, true), ["lwin"] = new(0x5B, true), ["rwin"] = new(0x5C, true),
        ["meta"] = new(0x5B, true), ["cmd"] = new(0x5B, true), ["super"] = new(0x5B, true),
        ["enter"] = new(0x0D, false), ["return"] = new(0x0D, false), ["eingabe"] = new(0x0D, false),
        ["tab"] = new(0x09, false), ["escape"] = new(0x1B, false), ["esc"] = new(0x1B, false),
        ["space"] = new(0x20, false), ["leertaste"] = new(0x20, false), ["backspace"] = new(0x08, false),
        ["delete"] = new(0x2E, true), ["del"] = new(0x2E, true), ["entf"] = new(0x2E, true),
        ["insert"] = new(0x2D, true), ["ins"] = new(0x2D, true), ["einfg"] = new(0x2D, true),
        ["left"] = new(0x25, true), ["up"] = new(0x26, true), ["right"] = new(0x27, true), ["down"] = new(0x28, true),
        ["home"] = new(0x24, true), ["pos1"] = new(0x24, true), ["end"] = new(0x23, true), ["ende"] = new(0x23, true),
        ["pageup"] = new(0x21, true), ["pgup"] = new(0x21, true), ["bildauf"] = new(0x21, true),
        ["pagedown"] = new(0x22, true), ["pgdn"] = new(0x22, true), ["bildab"] = new(0x22, true),
        ["apps"] = new(0x5D, true), ["menu"] = new(0x5D, true), ["contextmenu"] = new(0x5D, true),
        ["printscreen"] = new(0x2C, true), ["print"] = new(0x2C, true), ["druck"] = new(0x2C, true),
        ["pause"] = new(0x13, false), ["capslock"] = new(0x14, false), ["numlock"] = new(0x90, true), ["scrolllock"] = new(0x91, false),
        ["numpad0"] = new(0x60, false), ["numpad1"] = new(0x61, false), ["numpad2"] = new(0x62, false), ["numpad3"] = new(0x63, false),
        ["numpad4"] = new(0x64, false), ["numpad5"] = new(0x65, false), ["numpad6"] = new(0x66, false), ["numpad7"] = new(0x67, false),
        ["numpad8"] = new(0x68, false), ["numpad9"] = new(0x69, false), ["multiply"] = new(0x6A, false), ["add"] = new(0x6B, false),
        ["subtract"] = new(0x6D, false), ["decimal"] = new(0x6E, false), ["divide"] = new(0x6F, true),
        ["volumemute"] = new(0xAD, true), ["volumedown"] = new(0xAE, true), ["volumeup"] = new(0xAF, true),
        ["medianext"] = new(0xB0, true), ["mediaprev"] = new(0xB1, true), ["mediastop"] = new(0xB2, true), ["mediaplaypause"] = new(0xB3, true),
        ["browserback"] = new(0xA6, true), ["browserforward"] = new(0xA7, true), ["browserrefresh"] = new(0xA8, true),
        ["plus"] = new(0xBB, false), ["minus"] = new(0xBD, false),
    };

    /// <summary>Parses a combination; single printable characters use the active keyboard layout.</summary>
    public static IReadOnlyList<ComputerKey> Parse(string combination, Func<char, short>? scanLayout = null)
    {
        if (string.IsNullOrWhiteSpace(combination)) throw new ArgumentException("Leere Tastenkombination.");
        var trimmed = combination.Trim();
        // "+" alone or as the last key ("ctrl++") means the plus key itself.
        var parts = trimmed == "+" ? ["plus"]
            : trimmed.EndsWith("++", StringComparison.Ordinal)
                ? [.. trimmed[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "plus"]
                : trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > MaximumKeys) throw new ArgumentException($"Eine Tastenkombination umfasst 1 bis {MaximumKeys} Tasten.");
        var keys = new List<ComputerKey>(parts.Length);
        foreach (var part in parts) keys.Add(Key(part, scanLayout ?? VkKeyScan));
        return keys;
    }

    private static ComputerKey Key(string name, Func<char, short> scanLayout)
    {
        if (Named.TryGetValue(name, out var named)) return named;
        if (name.Length >= 2 && (name[0] is 'f' or 'F') && int.TryParse(name[1..], out var function) && function is >= 1 and <= 24)
            return new((ushort)(0x70 + function - 1), false);
        if (name.Length == 1)
        {
            var character = name[0];
            if (char.IsAsciiLetter(character)) return new((ushort)char.ToUpperInvariant(character), false);
            if (char.IsAsciiDigit(character)) return new(character, false);
            var scan = scanLayout(character);
            if (scan != -1 && (scan & 0xFF) != 0xFF)
                return new((ushort)(scan & 0xFF), false, Shift: (scan & 0x100) != 0);
        }
        throw new ArgumentException($"Die Taste '{name}' wird nicht unterstützt.");
    }

    private static short VkKeyScan(char character) => VkKeyScanW(character);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanW(char character);
}
