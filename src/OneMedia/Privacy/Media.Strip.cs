using RecursiveAI.OneMedia.Privacy;

namespace RecursiveAI.OneMedia;

public static partial class Media
{
    /// <summary>Writes a copy of a file with its location removed: positions in tags, XMP and EXIF, and GPS telemetry. See <see cref="PrivacyStrip"/>.</summary>
    /// <param name="input">The original, left untouched.</param>
    /// <param name="output">The new file; refused if it exists.</param>
    /// <param name="cancellationToken">Cancels the operation; checked between steps and passed to the layer below.</param>
    /// <returns>What was removed and what was found but left.</returns>
    /// <exception cref="UnsupportedMediaException">The format is not recognised.</exception>
    /// <exception cref="IOException">The output exists, or the disk failed.</exception>
    public static StripReport StripLocation(string input, string output, CancellationToken cancellationToken = default) => PrivacyStrip.SaveAs(input, output, StripOptions.LocationOnly, cancellationToken: cancellationToken);

    /// <summary>Writes a copy of a file with device identifiers removed: serial numbers, unique media and content ids, owner names. Make and model stay. See <see cref="PrivacyStrip"/>.</summary>
    /// <param name="input">The original, left untouched.</param>
    /// <param name="output">The new file; refused if it exists.</param>
    /// <param name="cancellationToken">Cancels the operation; checked between steps and passed to the layer below.</param>
    /// <returns>What was removed and what was found but left.</returns>
    /// <exception cref="UnsupportedMediaException">The format is not recognised.</exception>
    /// <exception cref="IOException">The output exists, or the disk failed.</exception>
    public static StripReport StripDeviceIds(string input, string output, CancellationToken cancellationToken = default) => PrivacyStrip.SaveAs(input, output, StripOptions.DeviceIdsOnly, cancellationToken: cancellationToken);
}
