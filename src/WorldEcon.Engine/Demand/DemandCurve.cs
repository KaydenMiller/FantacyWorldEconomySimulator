using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Demand;

/// <summary>
/// A consumer's per-unit demand curve: willingness-to-pay declines with each additional unit
/// (diminishing marginal value). The first (most-needed) unit is worth up to the good's peak
/// willingness multiple of base value; willingness falls linearly to 1× base at the desired quantity.
/// A higher peak = a more inelastic good. All integer, deterministic.
/// </summary>
public static class DemandCurve
{
    /// <summary>Reservation price (in base currency units) for the <paramref name="unitIndex"/>-th unit
    /// (1-based) of a good, given its base value, the good's peak willingness multiple (basis points of
    /// base value), and the consumer's desired quantity.</summary>
    public static long UnitReservationPrice(Money baseValue, long peakWillingnessMultipleBasisPoints,
        long desiredQuantity, long unitIndex)
    {
        long quantity = System.Math.Max(desiredQuantity, 1);
        long denominator = System.Math.Max(quantity - 1, 1);
        long willingnessBasisPoints = peakWillingnessMultipleBasisPoints
            - (peakWillingnessMultipleBasisPoints - 10_000) * (unitIndex - 1) / denominator;
        return baseValue.Units * willingnessBasisPoints / 10_000;
    }
}
