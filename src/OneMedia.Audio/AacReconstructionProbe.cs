using global::OneAudio;
using global::OneAudio.Aac;
using RecursiveAI.OneMedia.Repair.Reconstruction;

namespace RecursiveAI.OneMedia.Audio;

/// <summary>
/// Frames AAC raw data blocks for the reconstruction of cut-off recordings, with OneAudio's frame probe: it runs the decoder's own
/// syntax parser to find where each raw_data_block ends, without decoding any audio. Give <see cref="Probes"/> (or
/// <see cref="Options"/>) to <see cref="IsoReconstructor"/> so an AAC track can be recovered.
/// </summary>
public sealed class AacReconstructionProbe : IAudioFrameProbe
{
    // ISO/IEC 14496-3 Table 1.18: sampling_frequency_index and the rate it stands for, most common first for guessing.
    private static readonly (int Rate, int Index)[] s_rates =
    [
        (44100, 4), (48000, 3), (32000, 5), (22050, 7), (24000, 6), (16000, 8), (11025, 10), (12000, 9), (8000, 11), (88200, 1), (96000, 0), (64000, 2),
    ];

    /// <summary>The shared instance.</summary>
    public static AacReconstructionProbe Instance { get; } = new();

    /// <summary>The probes this package offers: AAC.</summary>
    public static IReadOnlyList<IAudioFrameProbe> Probes { get; } = [Instance];

    /// <summary>Reconstruction options that include the AAC probe.</summary>
    /// <returns>Options with <see cref="ReconstructionOptions.AudioProbes"/> set to <see cref="Probes"/>.</returns>
    public static ReconstructionOptions Options() => new() { AudioProbes = Probes };

    /// <inheritdoc />
    public CodecId Codec => CodecId.Aac;

    /// <inheritdoc />
    public IAudioFramer? CreateFramer(ReadOnlySpan<byte> config)
    {
        try
        {
            if (!AacAudioConfig.TryParse(config, out AacAudioConfig? parsed) || parsed is null)
            {
                return null;
            }

            return new Framer(new AacFrameProbe(config), parsed.FrameLength960 ? 960 : 1024);
        }
        catch (Exception e) when (e is AudioFormatException or UnsupportedAudioFormatException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IEnumerable<GuessedAudioSetup> Guesses()
    {
        // AudioSpecificConfig for AAC-LC (object type 2), no extensions: objectType(5) sampling_frequency_index(4) channel_configuration(4)
        // GASpecificConfig(3 bits: frameLengthFlag, dependsOnCoreCoder, extensionFlag, all 0).
        foreach (int channels in new[] { 2, 1 })
        {
            foreach ((int rate, int index) in s_rates)
            {
                byte[] asc = [(byte)((2 << 3) | (index >> 1)), (byte)(((index & 1) << 7) | (channels << 3))];
                yield return new GuessedAudioSetup(asc, rate, channels);
            }
        }
    }

    private sealed class Framer(AacFrameProbe probe, int samplesPerFrame) : IAudioFramer
    {
        public int SamplesPerFrame { get; } = samplesPerFrame;

        public AudioFrameResult Probe(ReadOnlySpan<byte> data, out int length)
        {
            AacProbeResult r = probe.Probe(data, out length);
            return r switch
            {
                AacProbeResult.Frame => AudioFrameResult.Frame,
                AacProbeResult.Incomplete => AudioFrameResult.Incomplete,
                _ => AudioFrameResult.NotFrame,
            };
        }
    }
}
