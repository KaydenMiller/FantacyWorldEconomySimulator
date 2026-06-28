using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Trade;

/// <summary>How many units of a single good fit in a merchant's hauling capacity — the smaller of the
/// weight-limited and volume-limited counts (dimensional capacity).</summary>
public static class CargoFit
{
    public static long MaxUnits(Mass weightCapacity, Volume volumeCapacity, Mass unitMass, Volume unitVolume)
    {
        long byWeight = weightCapacity.Grams / unitMass.Grams;
        long byVolume = volumeCapacity.CubicCentimeters / unitVolume.CubicCentimeters;
        return Math.Min(byWeight, byVolume);
    }
}
