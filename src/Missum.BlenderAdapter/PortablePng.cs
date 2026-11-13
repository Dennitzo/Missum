using System.IO.Compression;

namespace Missum.BlenderAdapter;

/// <summary>
/// A minimal PNG writer. The adapter is shipped inside the extension package and must stay free of
/// extra dependencies, so the picture of a window is encoded here instead of pulling in an imaging
/// library: 8-bit RGBA, no interlace, one filter byte per scan line, zlib-compressed image data.
/// </summary>
internal static class PortablePng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Writes RGBA bytes (row major, four bytes per pixel) as a PNG file. The file is written to a
    /// temporary path first, so a failed encode never leaves a half picture for vision to look at.
    /// </summary>
    public static void SaveRgba(string target, int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Ein PNG braucht Maße größer null.", nameof(width));
        if (rgba.Length < width * height * 4) throw new ArgumentException("Die Bilddaten sind zu kurz.", nameof(rgba));

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Signature);
                WriteChunk(file, "IHDR", Header(width, height));
                WriteChunk(file, "IDAT", Compress(width, height, rgba));
                WriteChunk(file, "IEND", []);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file is harmless.
            }
        }
    }

    private static byte[] Header(int width, int height)
    {
        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;  // bits per channel
        header[9] = 6;  // truecolour with alpha
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering, every scan line uses filter 0
        header[12] = 0; // no interlace
        return header;
    }

    private static byte[] Compress(int width, int height, byte[] rgba)
    {
        using var buffer = new MemoryStream(Math.Max(1_024, width * height * 2));
        using (var deflate = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var stride = width * 4;
            var scanLine = new byte[stride + 1];
            for (var row = 0; row < height; row++)
            {
                // scanLine[0] stays 0: filter "None" keeps the encoder simple and decoders happy.
                Buffer.BlockCopy(rgba, row * stride, scanLine, 1, stride);
                deflate.Write(scanLine, 0, scanLine.Length);
            }
        }

        return buffer.ToArray();
    }

    private static void WriteChunk(FileStream file, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, data.Length);
        file.Write(length);
        var name = new byte[4];
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, name, 0);
        file.Write(name);
        file.Write(data);
        Span<byte> crc = stackalloc byte[4];
        var crcValue = Crc32(name, data);
        WriteBigEndian(crc, 0, unchecked((int)crcValue));
        file.Write(crc);
    }

    private static uint Crc32(byte[] name, byte[] data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in name) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFF_FFFFu;
    }

    private static void WriteBigEndian(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)(value >>> 24);
        target[offset + 1] = (byte)(value >>> 16);
        target[offset + 2] = (byte)(value >>> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 1 ? 0xEDB8_8320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }

        return table;
    }
}
