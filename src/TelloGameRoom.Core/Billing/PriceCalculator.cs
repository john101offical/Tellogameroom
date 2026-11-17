using TelloGameRoom.Core.Enums;

namespace TelloGameRoom.Core.Billing;

/// <summary>
/// Pure billing maths. All money is handled as integer hundredths ("cents") of the currency unit so that
/// sums are exact and identical on every database engine. COP 5,000 is stored as 500000.
/// </summary>
public static class PriceCalculator
{
    public const int CentsPerUnit = 100;

    /// <summary>Converts a decimal amount in currency units to cents, rounding half away from zero.</summary>
    public static long ToCents(decimal amount) =>
        (long)Math.Round(amount * CentsPerUnit, 0, MidpointRounding.AwayFromZero);

    /// <summary>Converts cents to a decimal amount in currency units.</summary>
    public static decimal FromCents(long cents) => cents / (decimal)CentsPerUnit;

    /// <summary>
    /// Amount due for a duration at an hourly rate, with the shop's rounding rule applied.
    /// </summary>
    /// <param name="hourlyRateCents">Hourly rate in cents. Must not be negative.</param>
    /// <param name="durationSeconds">Purchased time in seconds. Must not be negative.</param>
    /// <param name="mode">Rounding mode.</param>
    /// <param name="incrementCents">Rounding step in cents (e.g. 10000 to round to COP 100). Ignored when the mode is None.</param>
    public static long AmountDue(long hourlyRateCents, long durationSeconds, RoundingMode mode, long incrementCents)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hourlyRateCents);
        ArgumentOutOfRangeException.ThrowIfNegative(durationSeconds);

        // Exact amount in cents, as a decimal to avoid losing fractions before rounding.
        var exact = hourlyRateCents * (decimal)durationSeconds / 3600m;
        return Round(exact, mode, incrementCents);
    }

    /// <summary>Rounds an exact cent amount according to the rule. Always returns whole cents.</summary>
    public static long Round(decimal exactCents, RoundingMode mode, long incrementCents)
    {
        if (mode == RoundingMode.None || incrementCents <= 1)
        {
            return (long)Math.Round(exactCents, 0, MidpointRounding.AwayFromZero);
        }

        var steps = exactCents / incrementCents;
        var roundedSteps = mode switch
        {
            RoundingMode.Up => Math.Ceiling(steps),
            RoundingMode.Down => Math.Floor(steps),
            _ => Math.Round(steps, 0, MidpointRounding.AwayFromZero),
        };
        return (long)roundedSteps * incrementCents;
    }
}
