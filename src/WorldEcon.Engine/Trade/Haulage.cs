namespace WorldEcon.Engine.Trade;

/// <summary>Dimensional-weight haulage math (Layer A). All integer → deterministic.</summary>
public static class Haulage
{
    /// <summary>Billable weight in grams: the larger of actual mass and volumetric weight
    /// (volume cm³ × 1000 / volumetricDivisor).</summary>
    public static long DimensionalWeightGrams(long massGrams, long volumeCubicCentimeters, long volumetricDivisor)
    {
        long volumetric = volumeCubicCentimeters * 1000 / volumetricDivisor;
        return Math.Max(massGrams, volumetric);
    }

    /// <summary>Haulage cost in copper: dimensional weight (g) × distance × rate / 1_000_000
    /// (so rate reads as copper per 1000 kg·distance).</summary>
    public static long Cost(long dimensionalWeightGrams, long distance, long transportRate)
        => dimensionalWeightGrams * distance * transportRate / 1_000_000;
}
