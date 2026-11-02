namespace RecursiveAI.OneMedia.Metadata.Telemetry.Gpmf;

/// <summary>
/// Axis order of GoPro IMU streams. The components of <c>ACCL</c>, <c>GYRO</c> and <c>MAGN</c> samples are stored in the order the sensor wrote them,
/// which differs between camera generations; <c>ORIN</c> names that order (one letter per component, upper case for the positive direction of an axis and lower
/// case for the negative) and <c>ORIO</c> the order the camera asks readers to present, usually <c>ZXY</c>. The decoder keeps the stored order; this helper
/// maps a sample to the requested one.
/// </summary>
public static class GpmfAxes
{
    /// <summary>
    /// Reorders and re-signs one sample from the input order to the output order: output component <c>i</c> is the input component that names the same axis
    /// as <c>output[i]</c>, negated when the two letters differ in case.
    /// </summary>
    /// <param name="stored">The sample as stored, one value per letter of <paramref name="input"/>.</param>
    /// <param name="input">The <c>ORIN</c> string, for example <c>YxZ</c>.</param>
    /// <param name="output">The <c>ORIO</c> string, for example <c>ZXY</c>.</param>
    /// <param name="destination">Receives one value per letter of <paramref name="output"/>.</param>
    /// <returns>False when the strings do not name the same axes, or a span is too short; <paramref name="destination"/> is then unchanged.</returns>
    public static bool TryRemap(ReadOnlySpan<double> stored, ReadOnlySpan<char> input, ReadOnlySpan<char> output, Span<double> destination)
    {
        if (input.Length != output.Length || stored.Length < input.Length || destination.Length < output.Length)
        {
            return false;
        }

        for (int i = 0; i < output.Length; i++)
        {
            if (IndexOfAxis(input, output[i]) < 0)
            {
                return false;
            }
        }

        for (int i = 0; i < output.Length; i++)
        {
            int from = IndexOfAxis(input, output[i]);
            destination[i] = input[from] == output[i] ? stored[from] : -stored[from];
        }

        return true;
    }

    private static int IndexOfAxis(ReadOnlySpan<char> order, char axis)
    {
        char upper = char.ToUpperInvariant(axis);
        for (int i = 0; i < order.Length; i++)
        {
            if (char.ToUpperInvariant(order[i]) == upper)
            {
                return i;
            }
        }

        return -1;
    }
}
