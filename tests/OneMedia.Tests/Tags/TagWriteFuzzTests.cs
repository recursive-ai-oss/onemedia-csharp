using RecursiveAI.OneMedia.Tags;
using Xunit;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Inputs the tag-write fuzz target found, kept as regression tests. Each is edited twice, as the target does.</summary>
public sealed class TagWriteFuzzTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20 };

    [Theory]
    [InlineData("matroska-open-then-not.bin")]
    [InlineData("matroska-void-hides-seekhead.bin")]
    [InlineData("matroska-second-pass.bin")]
    public void AFileThatOpenedStillOpensAfterTheTagWrite(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "tagwrite", name));
        TagEdits edits = new();
        edits.Set(CommonField.Title, "Fuzz title");
        edits.Set(CommonField.Artist, "Fuzz artist");
        edits.Set(CommonField.Comment, "Fuzz comment");
        edits.Set(CommonField.Description, "Fuzz description");
        edits.Set(CommonField.TrackNumber, 3);
        edits.SetPicture(Png, "image/png");
        edits.Remove(CommonField.Genre);
        byte[]? once = Pass(file, edits, new TagWriteOptions { Limits = Limits });
        if (once is null)
        {
            return;
        }

        TagEdits xml = new();
        xml.Set(CommonField.Title, "Second <&> title");
        xml.SetNative(TagSystems.IXml, "PROJECT", "Fuzz");
        byte[]? twice = Pass(once, xml, new TagWriteOptions { Limits = Limits, Systems = [TagSystems.Xmp, TagSystems.IXml] });
        if (twice is null)
        {
            return;
        }

        _ = Pass(twice, xml, new TagWriteOptions { Limits = Limits, Systems = [TagSystems.Xmp, TagSystems.IXml] });
    }

    // Plans and writes one pass, or returns null on a typed refusal; the result must still open with its tracks (a damaged Void can hide new tags from the reader, which is fine).
    private static byte[]? Pass(byte[] file, TagEdits edits, TagWriteOptions options)
    {
        using MemoryMediaSource before = new(file);
        using (MediaFile.Open(before, null, new MediaOpenOptions { Limits = Limits }))
        {
        }

        using MemoryMediaSource src = new(file);
        TagWritePlan plan;
        try
        {
            plan = TagWriter.Plan(src, edits, options);
        }
        catch (MediaException)
        {
            return null; // a typed refusal (a damaged SeekHead whose positions cannot be moved) is fine; a broken file is not
        }

        using MemoryStream output = new();
        SafeWriter.Write(plan.Plan, src, output);
        byte[] result = output.ToArray();
        using MemoryMediaSource after = new(result);
        using MediaFile m = MediaFile.Open(after, null, new MediaOpenOptions { Limits = Limits });
        Assert.NotEmpty(m.Tracks);
        return result;
    }
}
