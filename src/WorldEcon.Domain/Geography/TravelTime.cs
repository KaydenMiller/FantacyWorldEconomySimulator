namespace WorldEcon.Domain.Geography;

/// <summary>Integer travel time over one route in one direction.</summary>
public static class TravelTime
{
    /// <summary>
    /// ceil(lengthMetres × ticksPerDay × 100 × difficulty / (surfaceSpeed × vehicleSpeedFactor × directionFactor)),
    /// at least 1. <paramref name="difficulty"/> is a percentage (100 = no slowdown); the two factors are percentages
    /// of the surface speed. Throws when <paramref name="directionFactor"/> ≤ 0: callers drop impassable directions first.
    /// </summary>
    public static long Ticks(long lengthMetres, long surfaceSpeedMetresPerDay, int vehicleSpeedFactor,
        int directionFactor, int difficulty, long ticksPerDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(directionFactor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceSpeedMetresPerDay);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vehicleSpeedFactor);
        ArgumentOutOfRangeException.ThrowIfNegative(lengthMetres);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(difficulty);

        // Int128 keeps the 6-factor numerator from overflowing on planet-scale lengths.
        Int128 num = (Int128)lengthMetres * ticksPerDay * 100 * difficulty;
        Int128 den = (Int128)surfaceSpeedMetresPerDay * vehicleSpeedFactor * directionFactor;
        Int128 ticks = (num + den - 1) / den;
        return ticks < 1 ? 1 : (long)ticks;
    }
}
