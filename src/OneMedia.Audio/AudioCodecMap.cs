namespace RecursiveAI.OneMedia.Audio;

/// <summary>
/// Maps OneMedia's neutral codec names onto OneAudio's. The audio codec names are the same strings on both sides, so the mapping
/// is the identity; it lives here so that code on either side never depends on that coincidence.
/// </summary>
public static class AudioCodecMap
{
    /// <summary>Converts a OneMedia codec id to the OneAudio codec id with the same name.</summary>
    /// <param name="codec">The OneMedia codec id.</param>
    /// <returns>The OneAudio codec id.</returns>
    public static global::OneAudio.CodecId ToOneAudio(CodecId codec) => new(codec.Name);

    /// <summary>Converts a OneAudio codec id to the OneMedia codec id with the same name.</summary>
    /// <param name="codec">The OneAudio codec id.</param>
    /// <returns>The OneMedia codec id.</returns>
    public static CodecId FromOneAudio(global::OneAudio.CodecId codec) => new(codec.Name);
}
