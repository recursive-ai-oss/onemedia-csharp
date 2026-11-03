using System.Text;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Id3;
using Xunit;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>
/// Privacy: a value that was removed from a tag must not stay in the file. These tests search the bytes of the file for the removed value after every
/// way of saving, and check that leftovers from earlier edits (a frame or bytes in ID3v2 padding) are found by <see cref="TagWriter.CheckResidue(string, CancellationToken)"/>
/// and zeroed by any save.
/// </summary>
public sealed class TagResidueTests : IDisposable
{
    private const string Marker = "QZXSECRETPLACEMARKER";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onemedia-residue-" + Guid.NewGuid().ToString("N")[..8]);

    public TagResidueTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Fx(string rel) => File.ReadAllBytes(WriterTestSupport.Fixture(rel.Split('/')));

    private string Put(string name, byte[] bytes)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static bool Contains(string path) => Contains(File.ReadAllBytes(path));

    // Zeros are dropped first so that a value stored as UTF-16 (a zero after every letter) is found too.
    private static bool Contains(byte[] bytes) => Encoding.Latin1.GetString(bytes).Replace("\0", string.Empty, StringComparison.Ordinal).Contains("SECRETPLACE", StringComparison.Ordinal);

    private static TagEdits Set(CommonField field, string value)
    {
        TagEdits e = new();
        e.Set(field, value);
        return e;
    }

    private static TagEdits Remove(params CommonField[] fields)
    {
        TagEdits e = new();
        foreach (CommonField f in fields)
        {
            e.Remove(f);
        }

        return e;
    }

    // An MP3 with a title and a comment holding the marker, then the comment removed; returns the path and the bytes of the removed COMM frame.
    private (string Path, byte[] Frame) TaggedThenRemoved(string name)
    {
        string path = Put(name, Fx("audio-es/plain.mp3"));
        TagEdits add = new();
        add.Set(CommonField.Title, "kept");
        add.Set(CommonField.Comment, Marker + " comment");
        TagWriter.Save(path, add);
        byte[] bytes = File.ReadAllBytes(path);
        Id3Tag tag = Id3Tag.Read(bytes);
        Id3Frame comm = tag.Frames.First(f => f.Id == "COMM");
        byte[] frame = bytes.AsSpan((int)comm.Range.Offset, (int)comm.Range.Length).ToArray();
        TagWriter.Save(path, Remove(CommonField.Comment));
        Assert.False(Contains(path));
        return (path, frame);
    }

    // The shape an interrupted or older edit leaves: a whole frame, or only some bytes, inside the zero padding.
    private string WithLeftover(string name, bool asFrame)
    {
        (string path, byte[] frame) = TaggedThenRemoved(name);
        byte[] bytes = File.ReadAllBytes(path);
        Id3Tag tag = Id3Tag.Read(bytes);
        Assert.False(tag.Padding.IsNone);
        byte[] payload = asFrame ? frame : [1, .. Encoding.ASCII.GetBytes(Marker)];
        payload.CopyTo(bytes, (int)tag.Padding.Offset + (asFrame ? 12 : 30));
        File.WriteAllBytes(path, bytes);
        Assert.True(Contains(path));
        using MediaFile m = Media.Open(path);
        Assert.Null(m.Tags.Comment); // readers do not show it: it is only dead bytes
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CheckFindsLeftoversInPaddingAndNothingInACleanFile(bool asFrame)
    {
        string clean = TaggedThenRemoved("clean.mp3").Path;
        Assert.Empty(TagWriter.CheckResidue(clean));

        string path = WithLeftover("left.mp3", asFrame);
        TagResidue r = Assert.Single(TagWriter.CheckResidue(path));
        Assert.Equal(TagSystems.Id3v2, r.System);
        Assert.Equal(asFrame ? TagResidueKind.StaleFrame : TagResidueKind.NonZeroPadding, r.Kind);
        Assert.DoesNotContain("SECRET", r.Detail, StringComparison.Ordinal);
    }

    private static (int Exit, string Out) RunTags(params string[] args)
    {
        using StringWriter captured = new();
        int exit = RecursiveAI.OneMedia.Tool.TagsCommand.Run(args, captured);
        return (exit, captured.ToString());
    }

    [Fact]
    public void CheckSaysWhatItCoversAndAbsentFindsAFieldThatIsStillALiveFrame()
    {
        // The shape preview.1 left: the field was meant to be removed but is still an ordinary frame of the tag.
        string path = Put("live.mp3", Fx("audio-es/plain.mp3"));
        TagEdits add = new();
        add.Set(CommonField.Title, "kept");
        add.Set(CommonField.Comment, Marker + " comment");
        TagWriter.Save(path, add);
        Assert.Empty(TagWriter.CheckResidue(path));

        (int exit, string text) = RunTags(path, "--check");
        Assert.Equal(0, exit);
        Assert.Contains("no leftovers in ID3v2 padding; fields still present in the tag are not checked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("clean", text, StringComparison.Ordinal);

        (exit, text) = RunTags(path, "--check", "--absent", "comment,composer");
        Assert.Equal(1, exit);
        Assert.Contains("Comment: still present in id3v2:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Composer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);

        // Once it is really removed both checks pass.
        TagWriter.Save(path, Remove(CommonField.Comment));
        (exit, text) = RunTags(path, "--check", "--absent", "comment,composer");
        Assert.Equal(0, exit);
        Assert.Contains("fields absent in every tag system: Comment, Composer", text, StringComparison.Ordinal);
        Assert.Empty(TagWriter.FindPresent(path, [CommonField.Comment]));
        Assert.Equal(CommonField.Title, Assert.Single(TagWriter.FindPresent(path, [CommonField.Title, CommonField.Comment])).Field);
    }

    [Fact]
    public void TheLeftoverHintNamesTheFile()
    {
        string path = WithLeftover("hint.mp3", asFrame: true);
        (int exit, string text) = RunTags(path, "--check");
        Assert.Equal(1, exit);
        string shown = path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;
        Assert.Contains("onemedia tags " + shown + " --replace", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<file>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckFindsAFrameRightAfterTheTag()
    {
        string path = Put("after.mp3", Fx("audio-es/plain.mp3"));
        TagWriter.Save(path, Set(CommonField.Title, "kept"));
        byte[] bytes = File.ReadAllBytes(path);
        long end = Id3Tag.Read(bytes).Range.Length;
        Assert.Empty(TagWriter.CheckResidue(path));
        File.WriteAllBytes(path, [.. bytes.AsSpan(0, (int)end), .. "COMM"u8, .. bytes.AsSpan((int)end)]);
        Assert.Contains(TagWriter.CheckResidue(path), r => r.Kind == TagResidueKind.FrameAfterTag && r.Offset == end);
    }

    public enum SaveWay
    {
        PlainSave,
        PlainSaveAs,
        PlainSaveInPlace,
        SaveEditingAnotherField,
        SaveRemovingAnotherField,
        SaveInPlaceEditingAnotherField,
    }

    [Theory]
    [InlineData(SaveWay.PlainSave, true)]
    [InlineData(SaveWay.PlainSaveAs, true)]
    [InlineData(SaveWay.PlainSaveInPlace, true)]
    [InlineData(SaveWay.SaveEditingAnotherField, true)]
    [InlineData(SaveWay.SaveRemovingAnotherField, true)]
    [InlineData(SaveWay.SaveInPlaceEditingAnotherField, true)]
    [InlineData(SaveWay.PlainSave, false)]
    [InlineData(SaveWay.PlainSaveAs, false)]
    [InlineData(SaveWay.PlainSaveInPlace, false)]
    [InlineData(SaveWay.SaveEditingAnotherField, false)]
    [InlineData(SaveWay.SaveRemovingAnotherField, false)]
    [InlineData(SaveWay.SaveInPlaceEditingAnotherField, false)]
    public void AnySaveZeroesLeftoversInId3Padding(SaveWay how, bool asFrame)
    {
        string path = WithLeftover("any.mp3", asFrame);
        string result = path;
        switch (how)
        {
            case SaveWay.PlainSave:
                TagWriter.Save(path, new TagEdits());
                break;
            case SaveWay.PlainSaveAs:
                result = Path.Combine(_dir, "as.mp3");
                TagWriter.SaveAs(path, result, new TagEdits());
                break;
            case SaveWay.PlainSaveInPlace:
                TagWriter.SaveInPlace(path, new TagEdits());
                break;
            case SaveWay.SaveEditingAnotherField:
                TagWriter.Save(path, Set(CommonField.Title, "another title"));
                break;
            case SaveWay.SaveRemovingAnotherField:
                TagWriter.Save(path, Remove(CommonField.Artist));
                break;
            default:
                TagWriter.SaveInPlace(path, Set(CommonField.Title, "kept"));
                break;
        }

        Assert.False(Contains(result), $"{how}: the removed value is still in the file");
        Assert.Empty(TagWriter.CheckResidue(result));
        using MediaFile m = Media.Open(result);
        Assert.Null(m.Tags.Comment);
        Assert.NotNull(m.Tags.Title);
    }

    // An ID3v2.3 tag with one title frame and 1,070 bytes of padding, in front of audio that ends in an APEv2 tag; optionally a whole stale frame inside the padding.
    private string PaddedWithApe(string name, bool stale)
    {
        byte[] apeFile = Fx("audio-es/ext-ape-mp3gain.mp3");
        int oldTag = 10 + (apeFile[6] << 21 | apeFile[7] << 14 | apeFile[8] << 7 | apeFile[9]);
        byte[] frame = [.. "TIT2"u8, 0, 0, 0, 6, 0, 0, 0, .. "Title"u8];
        byte[] body = new byte[frame.Length + 1070];
        frame.CopyTo(body, 0);
        if (stale)
        {
            byte[] comm = [.. "COMM"u8, 0, 0, 0, 39, 0, 0, 0, .. "eng"u8, 0, .. Encoding.ASCII.GetBytes(Marker + " stale comment")];
            comm.CopyTo(body, frame.Length + 20);
        }

        byte[] head = [.. "ID3"u8, 3, 0, 0, (byte)(body.Length >> 21 & 0x7F), (byte)(body.Length >> 14 & 0x7F), (byte)(body.Length >> 7 & 0x7F), (byte)(body.Length & 0x7F)];
        return Put(name, [.. head, .. body, .. apeFile.AsSpan(oldTag)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInPlaceEditOfTheId3TagFitsItsPaddingWithAnApeTagAtTheEnd(bool stale)
    {
        string path = PaddedWithApe("fits.mp3", stale);
        long length = new FileInfo(path).Length;
        TagWriteOptions onlyId3 = new() { Systems = [TagSystems.Id3v2] };
        TagWriter.SaveInPlace(path, Set(CommonField.Title, "Other"), onlyId3);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.False(Contains(path));
        Assert.Empty(TagWriter.CheckResidue(path));
        using MediaFile r = Media.Open(path);
        Assert.Equal("Other", r.Tags.Title);
    }

    // The same edit written to every tag system the file has would make the APEv2 tag at the end grow, and an APE tag has no padding. The refusal is
    // right, and it must name that tag rather than blame the ID3v2 padding, which has room.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInPlaceEditThatMakesTheApeTagGrowIsRefusedAndNamesTheApeTag(bool stale)
    {
        string path = PaddedWithApe("ape-grows.mp3", stale);
        byte[] before = File.ReadAllBytes(path);
        PaddingRequiredException e = Assert.Throws<PaddingRequiredException>(() => TagWriter.SaveInPlace(path, Set(CommonField.Title, "Other")));
        Assert.Equal("APEv2 tag", e.ShortfallIn);
        Assert.Contains("APEv2 tag", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, e.Available);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void MediaFileSaveWithNoEditsZeroesLeftoversInThePadding()
    {
        string path = PaddedWithApe("media-save.mp3", stale: true);
        Assert.True(Contains(path));
        using (MediaFile m = Media.Open(path))
        {
            m.Save();
        }

        Assert.False(Contains(path));
        Assert.Empty(TagWriter.CheckResidue(path));
    }

    [Fact]
    public void ASaveOfACleanFileWithNoEditsChangesNothing()
    {
        string path = TaggedThenRemoved("same.mp3").Path;
        byte[] before = File.ReadAllBytes(path);
        TagWriter.Save(path, new TagEdits());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ARemovedLastFrameIsGoneAfterAnInPlaceRemoval()
    {
        // Removing the last frames in place used to leave them where the padding starts.
        string path = Put("inplace.mp3", Fx("audio-es/plain.mp3"));
        TagEdits add = new();
        add.Set(CommonField.Title, "kept");
        add.Set(CommonField.Comment, Marker);
        add.Set(CommonField.Composer, Marker);
        TagWriter.Save(path, add);
        Assert.True(Contains(path));
        TagWriter.SaveInPlace(path, Remove(CommonField.Comment, CommonField.Composer));
        Assert.False(Contains(path));
        Assert.Empty(TagWriter.CheckResidue(path));
        using MediaFile m = Media.Open(path);
        Assert.Null(m.Tags.Comment);
        Assert.Null(m.Tags.Composer);
    }

    // Every writer: after removing fields, by each way of saving, the value is not in the file (ID3 chunks, Vorbis comments, FLAC and Ogg padding,
    // ilst, Matroska Tags and Void, RIFF INFO and JUNK).
    [Theory]
    [InlineData("audio-es/plain.mp3")]
    [InlineData("pcm/aiff-id3.aiff")]
    [InlineData("pcm/bwf-v0.wav")]
    [InlineData("xiph-flv/tags.flac")]
    [InlineData("xiph-flv/tags.opus")]
    [InlineData("audio/opus.ogg")]
    [InlineData("metadata/tags_ilst.mp4")]
    [InlineData("metadata/mkv_ffmpeg_tags.mkv")]
    public void RemovedFieldsAreGoneAfterEveryWayOfSaving(string fixture)
    {
        string ext = Path.GetExtension(fixture);
        TagEdits add = new();
        add.Set(CommonField.Title, "kept");
        add.Set(CommonField.Comment, Marker + " comment");
        add.Set(CommonField.Composer, Marker + " composer");

        string Prepared(string name)
        {
            string p = Put(name + ext, Fx(fixture));
            TagWriter.Save(p, add);
            Assert.True(Contains(p), "setup: the marker should be in the file");
            return p;
        }

        // Save after removing.
        string a = Prepared("a");
        TagWriter.Save(a, Remove(CommonField.Comment, CommonField.Composer));
        Assert.False(Contains(a), "Save after removing");

        // SaveAs.
        string b = Prepared("b");
        string bOut = Path.Combine(_dir, "b-out" + ext);
        TagWriter.SaveAs(b, bOut, Remove(CommonField.Comment, CommonField.Composer));
        Assert.False(Contains(bOut), "SaveAs");

        // Save after editing another field, on a file whose fields were removed in place.
        string c = Prepared("c");
        TagWriter.SaveInPlace(c, Remove(CommonField.Comment, CommonField.Composer));
        Assert.False(Contains(c), "in-place removal");
        TagWriter.Save(c, Set(CommonField.Title, "edited"));
        Assert.False(Contains(c), "Save after editing another field");

        // A plain Save with no edits, after the in-place removal.
        string d = Prepared("d");
        TagWriter.SaveInPlace(d, Remove(CommonField.Comment));
        TagWriter.Save(d, new TagEdits());
        Assert.DoesNotContain(Marker + " comment", Encoding.Latin1.GetString(File.ReadAllBytes(d)).Replace("\0", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
