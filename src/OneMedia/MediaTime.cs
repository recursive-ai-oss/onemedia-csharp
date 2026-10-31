using System.Globalization;
using System.Numerics;

namespace RecursiveAI.OneMedia;

/// <summary>How an inexact rational result is turned into a whole number of ticks.</summary>
public enum MediaRounding
{
    /// <summary>Toward negative infinity.</summary>
    Floor,

    /// <summary>Toward positive infinity.</summary>
    Ceiling,

    /// <summary>Toward zero (truncation).</summary>
    TowardZero,

    /// <summary>Away from zero.</summary>
    AwayFromZero,

    /// <summary>To the nearest tick; exact halves go away from zero.</summary>
    NearestAwayFromZero,

    /// <summary>To the nearest tick; exact halves go to the even tick.</summary>
    NearestEven,
}

/// <summary>
/// An exact point or span of media time: a whole number of ticks of a fixed rational unit,
/// <c>1 / <see cref="Timescale"/></c> second. Containers store time this way (a 90 kHz clock, a sample rate, a
/// nanosecond scale), so values keep the unit they were read in and are only rescaled on request, with an explicit
/// <see cref="MediaRounding"/>. No floating point is involved anywhere; intermediates are 128-bit so no pair of valid
/// values overflows on the way.
/// </summary>
/// <remarks>
/// <para>The default value is zero ticks at a timescale of 1. <see cref="Unknown"/> marks a missing timestamp (a packet without a
/// decode time, a track without a duration); test for it with <see cref="IsKnown"/> before doing arithmetic, which treats it as an
/// ordinary (very negative) value.</para>
/// <para>Equality and ordering compare the rational value, not the representation: 1 tick at 1000 equals 90 ticks at 90000.</para>
/// </remarks>
public readonly struct MediaTime : IEquatable<MediaTime>, IComparable<MediaTime>, IComparable
{
    private readonly long _ticks;
    private readonly long _timescale; // 0 stands for 1, so default(MediaTime) is a valid zero.

    /// <summary>Creates a time of <paramref name="ticks"/> ticks, each <c>1 / timescale</c> second.</summary>
    /// <param name="ticks">Number of ticks; may be negative.</param>
    /// <param name="timescale">Ticks per second; must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timescale"/> is zero or negative.</exception>
    public MediaTime(long ticks, long timescale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timescale);
        _ticks = ticks;
        _timescale = timescale;
    }

    /// <summary>The marker for "no timestamp": <see cref="long.MinValue"/> ticks at a timescale of 1.</summary>
    public static MediaTime Unknown => new(long.MinValue, 1);

    /// <summary>Zero seconds.</summary>
    public static MediaTime Zero => default;

    /// <summary>The number of ticks.</summary>
    public long Ticks => _ticks;

    /// <summary>Ticks per second; always positive.</summary>
    public long Timescale => _timescale == 0 ? 1 : _timescale;

    /// <summary>False for <see cref="Unknown"/>.</summary>
    public bool IsKnown => !(_ticks == long.MinValue && Timescale == 1);

    /// <summary>True when the value is exactly zero.</summary>
    public bool IsZero => _ticks == 0;

    /// <summary>A whole number of seconds, at a timescale of 1.</summary>
    public static MediaTime FromSeconds(long seconds) => new(seconds, 1);

    /// <summary>Converts a <see cref="TimeSpan"/> exactly (its unit is 100 ns). Meant for the edges of the API only.</summary>
    public static MediaTime FromTimeSpan(TimeSpan value) => new(value.Ticks, TimeSpan.TicksPerSecond);

    /// <summary>
    /// Converts to a <see cref="TimeSpan"/> (100 ns resolution), rounding as asked. Meant for the edges of the API only.
    /// </summary>
    /// <param name="rounding">How to round to a whole 100 ns tick.</param>
    /// <exception cref="OverflowException">The value is outside the <see cref="TimeSpan"/> range.</exception>
    public TimeSpan ToTimeSpan(MediaRounding rounding = MediaRounding.NearestAwayFromZero) =>
        new(Rescale(TimeSpan.TicksPerSecond, rounding).Ticks);

    /// <summary>The value in ticks of another timescale, rounded as asked.</summary>
    /// <param name="timescale">Target ticks per second; must be positive.</param>
    /// <param name="rounding">How to round an inexact result.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timescale"/> is zero or negative.</exception>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public long ToTicks(long timescale, MediaRounding rounding) => Rescale(timescale, rounding).Ticks;

    /// <summary>The same instant expressed at another timescale.</summary>
    /// <param name="timescale">Target ticks per second; must be positive.</param>
    /// <param name="rounding">How to round an inexact result.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timescale"/> is zero or negative.</exception>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public MediaTime Rescale(long timescale, MediaRounding rounding)
    {
        if (!TryRescale(timescale, rounding, out MediaTime result))
        {
            throw new OverflowException("The rescaled time does not fit in 64 bits.");
        }

        return result;
    }

    /// <summary>Like <see cref="Rescale"/> but reports overflow instead of throwing.</summary>
    /// <param name="timescale">Target ticks per second; must be positive.</param>
    /// <param name="rounding">How to round an inexact result.</param>
    /// <param name="result">The rescaled time, or zero on overflow.</param>
    /// <returns>False when the result does not fit in 64 bits.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timescale"/> is zero or negative.</exception>
    public bool TryRescale(long timescale, MediaRounding rounding, out MediaTime result)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timescale);
        // ticks * new / old: |ticks| <= 2^63 and new < 2^63, so the product needs at most 126 bits.
        Int128 numerator = (Int128)_ticks * timescale;
        if (DivideRounded(numerator, Timescale, rounding) is Int128 q && q >= long.MinValue && q <= long.MaxValue)
        {
            result = new MediaTime((long)q, timescale);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Sum in this value's timescale, rounding when the other value's unit does not divide it.
    /// </summary>
    /// <param name="other">The value to add.</param>
    /// <param name="rounding">How to round an inexact result.</param>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public MediaTime Add(MediaTime other, MediaRounding rounding) => Combine(other, rounding, subtract: false);

    /// <summary>Difference (this minus <paramref name="other"/>) in this value's timescale, rounding when needed.</summary>
    /// <param name="other">The value to subtract.</param>
    /// <param name="rounding">How to round an inexact result.</param>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public MediaTime Subtract(MediaTime other, MediaRounding rounding) => Combine(other, rounding, subtract: true);

    private MediaTime Combine(MediaTime other, MediaRounding rounding, bool subtract)
    {
        long scale = Timescale;
        long otherScale = other.Timescale;
        Int128 otherTicks = subtract ? -(Int128)other._ticks : other._ticks;

        // (a/s + b/t) in units of 1/s  =  a + b*s/t
        Int128 added = DivideRounded(otherTicks * scale, otherScale, rounding);
        Int128 sum = _ticks + added;
        if (sum < long.MinValue || sum > long.MaxValue)
        {
            throw new OverflowException("The sum does not fit in 64 bits.");
        }

        return new MediaTime((long)sum, scale);
    }

    /// <summary>Exact sum, expressed in the left operand's timescale.</summary>
    /// <exception cref="ArithmeticException">The result is not a whole number of the left operand's ticks; use <see cref="Add"/> with a rounding mode.</exception>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public static MediaTime operator +(MediaTime left, MediaTime right) => left.ExactCombine(right, subtract: false);

    /// <summary>Exact difference, expressed in the left operand's timescale.</summary>
    /// <exception cref="ArithmeticException">The result is not a whole number of the left operand's ticks; use <see cref="Subtract"/> with a rounding mode.</exception>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public static MediaTime operator -(MediaTime left, MediaTime right) => left.ExactCombine(right, subtract: true);

    private MediaTime ExactCombine(MediaTime other, bool subtract)
    {
        Int128 scaled = (Int128)other._ticks * Timescale;
        if (scaled % other.Timescale != 0)
        {
            throw new ArithmeticException("The result is not a whole number of ticks in the left operand's timescale.");
        }

        return Combine(other, MediaRounding.TowardZero, subtract);
    }

    /// <summary>Negation in the same timescale.</summary>
    /// <exception cref="OverflowException">The value is the most negative tick count.</exception>
    public static MediaTime operator -(MediaTime value) => new(checked(-value._ticks), value.Timescale);

    /// <summary>Compares the rational values, whatever their timescales.</summary>
    public int CompareTo(MediaTime other)
    {
        // a/s vs b/t  <=>  a*t vs b*s  (both timescales are positive)
        return ((Int128)_ticks * other.Timescale).CompareTo((Int128)other._ticks * Timescale);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        MediaTime other => CompareTo(other),
        _ => throw new ArgumentException("The object is not a MediaTime.", nameof(obj)),
    };

    /// <summary>True when both values denote the same instant, even at different timescales.</summary>
    public bool Equals(MediaTime other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MediaTime other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Hash the reduced fraction so equal values at different timescales hash alike.
        long scale = Timescale;
        long gcd = (long)BigInteger.GreatestCommonDivisor(_ticks, scale);
        if (gcd == 0)
        {
            gcd = 1;
        }

        return HashCode.Combine(_ticks / gcd, scale / gcd);
    }

    /// <summary>Equality of values.</summary>
    public static bool operator ==(MediaTime left, MediaTime right) => left.Equals(right);

    /// <summary>Inequality of values.</summary>
    public static bool operator !=(MediaTime left, MediaTime right) => !left.Equals(right);

    /// <summary>Ordering of values.</summary>
    public static bool operator <(MediaTime left, MediaTime right) => left.CompareTo(right) < 0;

    /// <summary>Ordering of values.</summary>
    public static bool operator >(MediaTime left, MediaTime right) => left.CompareTo(right) > 0;

    /// <summary>Ordering of values.</summary>
    public static bool operator <=(MediaTime left, MediaTime right) => left.CompareTo(right) <= 0;

    /// <summary>Ordering of values.</summary>
    public static bool operator >=(MediaTime left, MediaTime right) => left.CompareTo(right) >= 0;

    /// <summary>Formats as <c>ticks/timescale</c>, for example <c>90000/90000</c>; <see cref="Unknown"/> prints <c>unknown</c>.</summary>
    public override string ToString() => IsKnown
        ? string.Create(CultureInfo.InvariantCulture, $"{_ticks}/{Timescale}")
        : "unknown";

    /// <summary>The larger of two values.</summary>
    public static MediaTime Max(MediaTime a, MediaTime b) => a >= b ? a : b;

    /// <summary>The smaller of two values.</summary>
    public static MediaTime Min(MediaTime a, MediaTime b) => a <= b ? a : b;

    private static Int128 DivideRounded(Int128 numerator, long denominator, MediaRounding rounding)
    {
        Int128 q = numerator / denominator;
        Int128 r = numerator % denominator;
        if (r == 0)
        {
            return q;
        }

        // q is truncated toward zero; decide whether to step one further out.
        bool negative = numerator < 0;
        Int128 magnitude = r < 0 ? -r : r;
        bool away = rounding switch
        {
            MediaRounding.Floor => negative,
            MediaRounding.Ceiling => !negative,
            MediaRounding.TowardZero => false,
            MediaRounding.AwayFromZero => true,
            MediaRounding.NearestAwayFromZero => magnitude * 2 >= denominator,
            MediaRounding.NearestEven => magnitude * 2 > denominator || (magnitude * 2 == denominator && !Int128.IsEvenInteger(q)),
            _ => throw new ArgumentOutOfRangeException(nameof(rounding)),
        };

        return away ? (negative ? q - 1 : q + 1) : q;
    }
}
