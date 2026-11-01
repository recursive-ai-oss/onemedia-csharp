using RecursiveAI.OneMedia.Tags;
using Xunit;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Inputs the tag-write fuzz target found, kept as regression tests.</summary>
public sealed class TagWriteFuzzTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    [Theory]
    [InlineData("matroska-open-then-not.bin")]
    [InlineData("matroska-void-hides-seekhead.bin")]
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
        MediaLimits limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20 };
        using (MemoryMediaSource before = new(file))
        using (MediaFile.Open(before, null, new MediaOpenOptions { Limits = limits }))
        {
        }

        using MemoryMediaSource src = new(file);
        TagWritePlan plan = TagWriter.Plan(src, edits, new TagWriteOptions { Limits = limits });
        using MemoryStream output = new();
        SafeWriter.Write(plan.Plan, src, output);
        using MemoryMediaSource after = new(output.ToArray());
        // The damaged Void hides everything after it from the reader, so the new tags may not show; what matters is that the file still opens with its tracks.
        using MediaFile m = MediaFile.Open(after, null, new MediaOpenOptions { Limits = limits });
        Assert.NotEmpty(m.Tracks);
    }
}
