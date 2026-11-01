using System;
using System.Globalization;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>How the command line writes frame rates and HE-AAC notes.</summary>
internal static class CliFormat
{
    // Matroska states a frame rate as a default duration in whole nanoseconds, so 24 fps arrives as 1000000000/41666666 (or similar). A rate
    // within 0.05% of a broadcast rate is shown as that rate; anything else is reduced and shown with its decimal value.
    internal static (long Numerator, long Denominator) FrameRateOf(long numerator, long denominator)
    {
        long gcd = Gcd(numerator, denominator);
        (long n, long d) = (numerator / gcd, denominator / gcd);
        if (d <= 1001)
        {
            return (n, d);
        }

        double fps = (double)numerator / denominator;
        foreach (long divisor in new[] { 1L, 1001L })
        {
            foreach (long whole in new[] { 12L, 15L, 24L, 25L, 30L, 48L, 50L, 60L, 72L, 90L, 100L, 120L, 144L, 240L })
            {
                double candidate = divisor == 1 ? whole : whole * 1000.0 / 1001;
                if (Math.Abs(fps - candidate) / candidate < 0.0005)
                {
                    return divisor == 1 ? (whole, 1) : (whole * 1000, 1001);
                }
            }
        }

        return (n, d);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }

    internal static string FrameRate(long numerator, long denominator)
    {
        (long n, long d) = FrameRateOf(numerator, denominator);
        string ratio = d == 1 ? n.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{n}/{d}");
        return d == 1 ? ratio : string.Create(CultureInfo.InvariantCulture, $"{ratio} ({(double)n / d:0.###})");
    }

    // Says what a track's AAC configuration implies, in words a reader can act on: which rate the core coder runs at, which the output has,
    // and how the file signals it; OneAudio decodes the high band.
    internal static string? HeAac(MediaTrack t)
    {
        if (t.DescribeCodec() is not { } p || !(p.SbrPresent || p.ContainerDeclaresDoubleRate))
        {
            return null;
        }

        string core = p.CoreSampleRate.ToString("N0", CultureInfo.InvariantCulture);
        string output = p.SampleRate.ToString("N0", CultureInfo.InvariantCulture);
        if (!p.SbrPresent)
        {
            return $"container declares {output} Hz, twice the AAC core rate of {core} Hz; this is a hint, not proof of SBR";
        }

        string how = p.SbrSignalling == RecursiveAI.OneMedia.Codecs.SbrSignalling.Explicit ? "explicit" : "backward-compatible";
        return $"AAC-LC core {core} Hz; {(p.PsPresent ? "HE-AACv2" : "HE-AAC")} output {output} Hz ({how} signalling)";
    }
}
