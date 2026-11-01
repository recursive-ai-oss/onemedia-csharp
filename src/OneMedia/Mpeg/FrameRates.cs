namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>Frame rates as the lowest-terms fraction, so 60000/2002 (a VUI time scale over twice the tick count) reads 30000/1001.</summary>
internal static class FrameRates
{
    public static (int Numerator, int Denominator) Reduce(int numerator, int denominator)
    {
        if (numerator <= 0 || denominator <= 0)
        {
            return (numerator, denominator);
        }

        int a = numerator, b = denominator;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return (numerator / a, denominator / a);
    }
}
