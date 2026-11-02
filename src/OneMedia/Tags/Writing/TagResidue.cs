namespace RecursiveAI.OneMedia.Tags;

/// <summary>What kind of leftover <see cref="TagWriter.CheckResidue(string, System.Threading.CancellationToken)"/> found.</summary>
public enum TagResidueKind
{
    /// <summary>The padding of a tag holds bytes that are not zero, but they do not read as a frame.</summary>
    NonZeroPadding,

    /// <summary>A frame identifier sits in the padding: a frame that was removed or cut off by an earlier edit.</summary>
    StaleFrame,

    /// <summary>Frame-like bytes follow the tag where audio should start.</summary>
    FrameAfterTag,
}

/// <summary>One leftover found in a file's tag region. It names where the bytes are, never what they say.</summary>
/// <param name="System">The tag system, for example <see cref="TagSystems.Id3v2"/>.</param>
/// <param name="Kind">What was found.</param>
/// <param name="Offset">Where it starts in the file.</param>
/// <param name="Length">How many bytes from there are involved.</param>
/// <param name="Detail">One sentence in plain words.</param>
public sealed record TagResidue(string System, TagResidueKind Kind, long Offset, long Length, string Detail);
