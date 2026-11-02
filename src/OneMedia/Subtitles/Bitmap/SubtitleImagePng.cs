using System.IO.Compression;

namespace RecursiveAI.OneMedia.Captions;

/// <summary>
/// Writes a decoded subtitle image as a PNG file, for looking at what a decoder produced and for handing pictures to tools that read
/// PNG. Uses only <see cref="ZLibStream"/> from the base class library; this is a debugging aid, not an image library (8-bit RGBA, no
/// filtering, no ancillary chunks).
/// </summary>
public static class SubtitleImagePng
{
    /// <summary>Encodes an image as a PNG file.</summary>
    /// <param name="image">The image; its <see cref="SubtitleImage.Stride"/> rows are copied as they are.</param>
    /// <returns>The PNG bytes (colour type 6, 8 bits per channel, straight alpha).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="ArgumentException">The image is smaller than its width, height and stride say.</exception>
    public static byte[] Encode(SubtitleImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height, row = w * 4;
        if (w < 1 || h < 1 || image.Stride < row || image.Pixels.LongLength < (long)image.Stride * (h - 1) + row)
        {
            throw new ArgumentException("The image's pixel data is smaller than its size and stride say.", nameof(image));
        }

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0); // filter type "none"
                z.Write(image.Pixels, y * image.Stride, row);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13]; // fixed small buffer
        WriteBe(ihdr, 0, w);
        WriteBe(ihdr, 4, h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // colour type: RGBA
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteBe(Span<byte> d, int o, int v)
    {
        d[o] = (byte)(v >> 24);
        d[o + 1] = (byte)(v >> 16);
        d[o + 2] = (byte)(v >> 8);
        d[o + 3] = (byte)v;
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> n = stackalloc byte[4]; // fixed small buffer
        WriteBe(n, 0, data.Length);
        s.Write(n);
        s.Write(type);
        s.Write(data);
        uint crc = Crc32(Crc32(0, type), data);
        WriteBe(n, 0, unchecked((int)crc));
        s.Write(n);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    // ISO 3309 / PNG CRC-32, continuing from a previous value.
    private static uint Crc32(uint previous, ReadOnlySpan<byte> data)
    {
        uint c = ~previous;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }
}
