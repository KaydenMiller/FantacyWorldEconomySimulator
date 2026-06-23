using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Demand;

/// <summary>Scarcity-flexing retail pricing: the markup flexes with the town's supply/demand ratio
/// (same scarcity knobs as wholesale pricing), and the price is built off the shop's own cost basis.</summary>
public static class RetailPricing
{
    /// <summary>Per (settlement, good) scarcity multiplier in bp (10000 = 1.0), clamped to the world's
    /// price-multiplier band. demand/supply > 1 raises it; glut floors it.</summary>
    public static long ScarcityMultBp(long demand, long supply, World world)
    {
        long s = System.Math.Max(supply, 1);
        long scarcityBp = FixedMath.DivRound(demand * FixedMath.BpScale, s);
        return System.Math.Clamp(
            FixedMath.PowBpInt(scarcityBp, world.ElasticityExponent),
            world.MinPriceMultBp, world.MaxPriceMultBp);
    }

    /// <summary>retail = cost × (1 + markup×scarcityMult). Never below cost (markup/scarcity are non-negative).</summary>
    public static Money RetailPrice(Money costBasis, int markupBp, long scarcityMultBp)
    {
        long effectiveMarkupBp = FixedMath.MulBp(markupBp, scarcityMultBp);
        return new Money(costBasis.Units + FixedMath.MulBp(costBasis.Units, effectiveMarkupBp));
    }
}
