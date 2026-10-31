using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>The happy-path view of the metadata layer: <see cref="MediaFile"/> only forwards, so these check that the forwarding reaches the right layer.</summary>
public class MediaFileMetadataTests
{
    [Fact]
    public void QuickTimeFileGivesTagsFactsAndNativeAtoms()
    {
        using MediaFile media = Media.Open(TagTestHelpers.Path("tags_keys.mov"));
        Assert.True(media.Location.HasValue);
        Assert.Equal(37.7749, media.Location.Value.Latitude, 3);
        Assert.NotEmpty(media.Location.Candidates);
        Assert.NotNull(media.Tags);
        Assert.Same(media.Metadata.Tags, media.Tags);
        Assert.NotNull(media.Device);
        Assert.NotNull(media.CaptureTime);
        Assert.NotNull(media.Rotation);
        NativeTag first = media.Metadata.NativeTags[0];
        Assert.Equal(first.Text, media.Native[first.System, first.Key]?.Text);
        Assert.IsAssignableFrom<IMetadataProvider>(media.Native.Reader);
    }

    [Fact]
    public void MatroskaFileGivesTags()
    {
        using MediaFile media = Media.Open(TagTestHelpers.Path("mkvmerge_global.mkv"));
        Assert.Equal("Ida_Rowe", media.Tags.Director);
        Assert.Equal("2015", media.Tags.Date);
    }

    [Fact]
    public async Task AsyncOpenNeedsTheAsyncReadAndThenAnswersTheSame()
    {
        await using MediaFile media = await Media.OpenAsync(TagTestHelpers.Path("mkvmerge_global.mkv"));
        MediaMetadata m = await media.ReadMetadataAsync();
        Assert.Equal("Ida_Rowe", m.Tags.Director);
        Assert.Equal("Ida_Rowe", media.Tags.Director); // cached after the async read
    }
}
