using System.IO.Compression;
using System.Text;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// Minimal QR code encoder for the LAN link of a Webdesign project: byte mode, error correction
/// level M, versions 1 to 10 (up to 213 bytes), automatic mask choice. Follows ISO/IEC 18004 in
/// the structure of Project Nayuki's reference implementation (MIT).
/// </summary>
public sealed class WebdesignQrCode
{
    // Level M, index = version.
    private static readonly int[] EccCodewordsPerBlock = [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] ErrorCorrectionBlocks = [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];
    private readonly bool[,] _modules;
    private readonly bool[,] _function;

    public int Version { get; }
    public int Size { get; }
    public int Mask { get; private set; }

    public bool this[int x, int y] => _modules[y, x];

    private WebdesignQrCode(int version, byte[] dataCodewords, int? mask)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(dataCodewords));
        if (mask is { } fixedMask) Mask = fixedMask;
        else
        {
            var best = int.MaxValue;
            for (var candidate = 0; candidate < 8; candidate++)
            {
                ApplyMask(candidate);
                DrawFormatBits(candidate);
                var penalty = PenaltyScore();
                if (penalty < best) { best = penalty; Mask = candidate; }
                ApplyMask(candidate); // XOR undoes it.
            }
        }
        ApplyMask(Mask);
        DrawFormatBits(Mask);
    }

    public static WebdesignQrCode Encode(string text, int? mask = null)
    {
        var data = Encoding.UTF8.GetBytes(text);
        for (var version = 1; version <= 10; version++)
        {
            var capacityBits = DataCodewords(version) * 8;
            var countBits = version <= 9 ? 8 : 16;
            var usedBits = 4 + countBits + data.Length * 8;
            if (usedBits > capacityBits) continue;
            var bits = new List<bool>();
            AppendBits(bits, 0b0100, 4);
            AppendBits(bits, data.Length, countBits);
            foreach (var value in data) AppendBits(bits, value, 8);
            AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count));
            AppendBits(bits, 0, (8 - bits.Count % 8) % 8);
            for (var pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) AppendBits(bits, pad, 8);
            var codewords = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++)
                if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return new WebdesignQrCode(version, codewords, mask);
        }
        throw new ArgumentException("Der Text ist für den QR-Code zu lang.", nameof(text));
    }

    /// <summary>Grayscale PNG with a four-module quiet zone.</summary>
    public byte[] ToPng(int scale = 8, int border = 4)
    {
        var dimension = (Size + border * 2) * scale;
        using var raw = new MemoryStream();
        for (var y = 0; y < dimension; y++)
        {
            raw.WriteByte(0); // filter: none
            var moduleY = y / scale - border;
            for (var x = 0; x < dimension; x++)
            {
                var moduleX = x / scale - border;
                var dark = moduleX >= 0 && moduleY >= 0 && moduleX < Size && moduleY < Size && _modules[moduleY, moduleX];
                raw.WriteByte(dark ? (byte)0 : (byte)255);
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zlib);
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        WriteBigEndian(header, 0, dimension);
        WriteBigEndian(header, 4, dimension);
        header[8] = 8; // bit depth
        header[9] = 0; // grayscale
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    internal static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var alignments = version / 7 + 2;
            result -= (25 * alignments - 10) * alignments - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    internal static int DataCodewords(int version) =>
        RawDataModules(version) / 8 - EccCodewordsPerBlock[version] * ErrorCorrectionBlocks[version];

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);
        var positions = AlignmentPositions(Version);
        for (var i = 0; i < positions.Length; i++)
            for (var j = 0; j < positions.Length; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == positions.Length - 1 || i == positions.Length - 1 && j == 0))
                    DrawAlignment(positions[i], positions[j]);
        DrawFormatBits(0);
        DrawVersion();
    }

    internal static int[] AlignmentPositions(int version)
    {
        if (version == 1) return [];
        var count = version / 7 + 2;
        var step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, position = version * 4 + 17 - 7; i >= 1; i--, position -= step) result[i] = position;
        return result;
    }

    /// <summary>15 format bits for level M and the mask, including BCH and the fixed XOR mask.</summary>
    internal static int FormatBits(int mask)
    {
        var data = 0 << 3 | mask; // level M = 00
        var remainder = data;
        for (var i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        return (data << 10 | remainder) ^ 0x5412;
    }

    private void DrawFormatBits(int mask)
    {
        var bits = FormatBits(mask);
        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));
        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var remainder = Version;
        for (var i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        var bits = Version << 12 | remainder;
        for (var i = 0; i < 18; i++)
        {
            var bit = Bit(bits, i);
            var a = Size - 11 + i % 3;
            var b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, distance != 2 && distance != 4);
            }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _function[y, x] = true;
    }

    private byte[] AddEccAndInterleave(byte[] data)
    {
        var blocks = ErrorCorrectionBlocks[Version];
        var eccLength = EccCodewordsPerBlock[Version];
        var raw = RawDataModules(Version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLength = raw / blocks;
        var divisor = ReedSolomonDivisor(eccLength);
        var parts = new List<byte[]>();
        for (int i = 0, offset = 0; i < blocks; i++)
        {
            var length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            var chunk = data.AsSpan(offset, length).ToArray();
            offset += length;
            var ecc = ReedSolomonRemainder(chunk, divisor);
            var block = new List<byte>(chunk);
            if (i < shortBlocks) block.Add(0);
            block.AddRange(ecc);
            parts.Add([.. block]);
        }
        var result = new List<byte>(raw);
        for (var i = 0; i < parts[0].Length; i++)
            for (var j = 0; j < parts.Count; j++)
                if (i != shortLength - eccLength || j >= shortBlocks) result.Add(parts[j][i]);
        return [.. result];
    }

    internal static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < result.Length; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    internal static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var value in data)
        {
            var factor = value ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vertical = 0; vertical < Size; vertical++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vertical : vertical;
                    if (_function[y, x] || i >= data.Length * 8) continue;
                    _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                    i++;
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_function[y, x]) _modules[y, x] = !_modules[y, x];
            }
    }

    private int PenaltyScore()
    {
        var result = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < Size; a++)
            {
                var run = 1;
                var previous = Module(pass, a, 0);
                for (var b = 1; b < Size; b++)
                {
                    var current = Module(pass, a, b);
                    if (current == previous) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                    else { run = 1; previous = current; }
                    // Finder-like 1:1:3:1:1 with four light modules on one side.
                    if (b >= 10 && IsFinderLike(pass, a, b - 10)) result += 40;
                }
            }
        for (var y = 0; y < Size - 1; y++)
            for (var x = 0; x < Size - 1; x++)
            {
                var color = _modules[y, x];
                if (color == _modules[y, x + 1] && color == _modules[y + 1, x] && color == _modules[y + 1, x + 1]) result += 3;
            }
        var dark = 0;
        foreach (var module in _modules) if (module) dark++;
        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return result + Math.Max(0, k) * 10;
    }

    private bool Module(int pass, int line, int position) => pass == 0 ? _modules[line, position] : _modules[position, line];

    private bool IsFinderLike(int pass, int line, int start)
    {
        // Patterns 10111010000 and 00001011101.
        bool[] first = [true, false, true, true, true, false, true, false, false, false, false];
        bool matchesFirst = true, matchesSecond = true;
        for (var i = 0; i < 11; i++)
        {
            var module = Module(pass, line, start + i);
            if (module != first[i]) matchesFirst = false;
            if (module != first[10 - i]) matchesSecond = false;
        }
        return matchesFirst || matchesSecond;
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

    private static void AppendBits(List<bool> bits, int value, int length)
    {
        for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32([.. typeBytes, .. data]);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        output.Write(crcBytes);
    }
}
