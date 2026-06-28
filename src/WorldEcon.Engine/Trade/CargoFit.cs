using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Trade;

/// <summary>How many units of a single good fit in a merchant's hauling capacity — the smaller of the
/// weight-limited and volume-limited counts (dimensional capacity).</summary>
public static class CargoFit
{
    /// <summary>Units of a single good that fit, given the merchant's weight and volume capacity and the
    /// good's per-unit mass and volume — the smaller of the two limits. Caller guarantees unit mass and
    /// volume are ≥ 1 (Good enforces this), so the integer divisions cannot divide by zero.</summary>
    public static long MaxUnits(Mass weightCapacity, Volume volumeCapacity, Mass unitMass, Volume unitVolume)
    {
        long byWeight = weightCapacity.Grams / unitMass.Grams;
        long byVolume = volumeCapacity.CubicCentimeters / unitVolume.CubicCentimeters;
        return Math.Min(byWeight, byVolume);
    }
}
