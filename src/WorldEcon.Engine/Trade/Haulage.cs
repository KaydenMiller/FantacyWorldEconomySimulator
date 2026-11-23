using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Engine.Trade;

/// <summary>Dimensional-weight haulage math (Layer A). All integer → deterministic.</summary>
public static class Haulage
{
    /// <summary>Divisor of <see cref="Numerator"/>: grams × metres → tonne·km is 10⁶ × 10³.</summary>
    public const long Scale = 1_000_000_000;

    /// <summary>Billable weight in grams: the larger of actual mass and volumetric weight
    /// (volume cm³ × 1000 / volumetricDivisor).</summary>
    public static long DimensionalWeightGrams(long massGrams, long volumeCubicCentimeters, long volumetricDivisor)
    {
        long volumetric = volumeCubicCentimeters * 1000 / volumetricDivisor;
        return Math.Max(massGrams, volumetric);
    }

    /// <summary>grams × metres × rate × ratePct / 100, before division by <see cref="Scale"/>.</summary>
    public static Int128 Numerator(long dimensionalWeightGrams, long lengthMetres, long transportRate, int ratePct = 100)
        => (Int128)dimensionalWeightGrams * lengthMetres * transportRate * ratePct / 100;

    /// <summary>Haulage cost in copper: dimensional weight (g) × length (m) × rate × ratePct / 100 / 1,000,000,000,
    /// so the rate reads as copper per tonne·km and <paramref name="ratePct"/> is the vehicle class's share of it.</summary>
    public static long Cost(long dimensionalWeightGrams, long lengthMetres, long transportRate, int ratePct = 100)
        => (long)(Numerator(dimensionalWeightGrams, lengthMetres, transportRate, ratePct) / Scale);

    /// <summary>Haulage for <paramref name="quantity"/> units of a good on a vehicle class (its divisor and rate).</summary>
    public static long CostFor(VehicleClass vehicle, Good good, long quantity, long lengthMetres, long transportRate)
    {
        long dim = DimensionalWeightGrams(good.MassPerUnit.Grams * quantity, good.VolumePerUnit.CubicCentimeters * quantity,
            vehicle.VolumetricDivisor);
        return Cost(dim, lengthMetres, transportRate, vehicle.RatePct);
    }
}
