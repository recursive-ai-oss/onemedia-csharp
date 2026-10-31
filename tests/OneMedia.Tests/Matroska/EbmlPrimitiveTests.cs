using RecursiveAI.OneMedia.Matroska;
using static RecursiveAI.OneMedia.Tests.Ebl;

namespace RecursiveAI.OneMedia.Tests;

public class EbmlPrimitiveTests
{
    [Theory]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, 0x1A45DFA3u, 4)]
    [InlineData(new byte[] { 0xA3 }, 0xA3u, 1)]
    [InlineData(new byte[] { 0x42, 0x82 }, 0x4282u, 2)]
    [InlineData(new byte[] { 0x2A, 0xD7, 0xB1 }, 0x2AD7B1u, 3)]
    public void ReadsElementIds(byte[] data, uint expected, int length)
    {
        Assert.True(Ebml.TryReadId(data, out uint id, out int len));
        Assert.Equal(expected, id);
        Assert.Equal(length, len);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x08, 0x01, 0x02, 0x03, 0x04 })] // five bytes: longer than MaxIdLength
    [InlineData(new byte[] { 0x1A, 0x45 })] // cut off
    [InlineData(new byte[] { })]
    public void RejectsBadElementIds(byte[] data) => Assert.False(Ebml.TryReadId(data, out _, out _));

    [Theory]
    [InlineData(new byte[] { 0x80 }, 0L, 1)]
    [InlineData(new byte[] { 0xFE }, 126L, 1)]
    [InlineData(new byte[] { 0x40, 0x7F }, 127L, 2)] // a non-minimal form of 127 is valid
    [InlineData(new byte[] { 0x43, 0x20 }, 800L, 2)]
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05 }, 5L, 8)]
    public void ReadsSizes(byte[] data, long expected, int length)
    {
        Assert.True(Ebml.TryReadSize(data, out long size, out int len));
        Assert.Equal(expected, size);
        Assert.Equal(length, len);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x7F, 0xFF })]
    [InlineData(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void AllOnesIsUnknownSize(byte[] data)
    {
        Assert.True(Ebml.TryReadSize(data, out long size, out _));
        Assert.Equal(Ebml.UnknownSize, size);
    }

    [Fact]
    public void BadSizesAreRejected()
    {
        Assert.False(Ebml.TryReadSize(new byte[] { 0x00 }, out _, out _));
        Assert.False(Ebml.TryReadSize(new byte[] { 0x40 }, out _, out _)); // two bytes announced, one present
        Assert.False(Ebml.TryReadSize([], out _, out _));
    }

    [Fact]
    public void TrackNumberVintAcceptsAllOnes() // track 127 is written 0xFF in a block header
    {
        Assert.True(Ebml.TryReadVint(new byte[] { 0xFF }, out ulong value, out _));
        Assert.Equal(127UL, value);
    }

    [Fact]
    public void ReadsIntegers()
    {
        Assert.True(Ebml.TryReadUnsigned([], out ulong zero));
        Assert.Equal(0UL, zero);
        Assert.True(Ebml.TryReadUnsigned(new byte[] { 0x01, 0x00 }, out ulong u));
        Assert.Equal(256UL, u);
        Assert.False(Ebml.TryReadUnsigned(new byte[9], out _));
        Assert.True(Ebml.TryReadSigned(new byte[] { 0xFF }, out long s));
        Assert.Equal(-1, s);
        Assert.True(Ebml.TryReadSigned(new byte[] { 0x80, 0x00 }, out long s2));
        Assert.Equal(-32768, s2);
    }

    [Theory]
    [InlineData(3003.0, 3003)]
    [InlineData(0.4, 0)]
    [InlineData(0.5, 1)]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -3)]
    [InlineData(48000.0, 48000)]
    [InlineData(1e15, 1_000_000_000_000_000)]
    public void RoundsFloatsWithoutFloatingPoint(double value, long expected)
    {
        byte[] eight = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(eight, value);
        Assert.True(Ebml.TryReadFloatRounded(eight, out long got));
        Assert.Equal(expected, got);
        if (Math.Abs(value) < 1e6)
        {
            byte[] four = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(four, (float)value);
            Assert.True(Ebml.TryReadFloatRounded(four, out got));
            Assert.Equal(expected, got);
        }
    }

    [Fact]
    public void RejectsNonFiniteFloats()
    {
        byte[] eight = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(eight, double.NaN);
        Assert.False(Ebml.TryReadFloatRounded(eight, out _));
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(eight, double.PositiveInfinity);
        Assert.False(Ebml.TryReadFloatRounded(eight, out _));
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(eight, 1e30);
        Assert.False(Ebml.TryReadFloatRounded(eight, out _));
        Assert.False(Ebml.TryReadFloatRounded(new byte[3], out _));
    }

    [Fact]
    public void Crc32MatchesTheCheckValue()
    {
        // The CRC-32/ISO-HDLC check value of "123456789".
        Assert.Equal(0xCBF43926u, EbmlCrc32.Compute("123456789"u8));
    }

    [Fact]
    public void XiphLacedCodecPrivateSplits()
    {
        byte[] a = Fill(3, 1), b = Fill(300, 2), c = Fill(5, 3);
        byte[] priv = Cat([2], [3], [255, 45], a, b, c);
        Assert.True(MatroskaCodecPrivate.TrySplitXiphLacing(priv, out ReadOnlyMemory<byte>[] packets));
        Assert.Equal(3, packets.Length);
        Assert.Equal(a, packets[0].ToArray());
        Assert.Equal(b, packets[1].ToArray());
        Assert.Equal(c, packets[2].ToArray());
        Assert.False(MatroskaCodecPrivate.TrySplitXiphLacing(new byte[] { 2, 200, 200 }, out _));
        Assert.False(MatroskaCodecPrivate.TrySplitXiphLacing(ReadOnlyMemory<byte>.Empty, out _));
    }
}
