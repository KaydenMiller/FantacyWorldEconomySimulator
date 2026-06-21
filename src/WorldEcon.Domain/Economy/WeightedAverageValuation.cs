using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Economy;

public sealed class WeightedAverageValuation : ICostBasisValuation
{
    public Money Blend(long existingQty, Money existingUnitBasis, long incomingQty, Money incomingUnitBasis)
    {
        if (incomingQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(incomingQty), "Incoming quantity must be positive.");
        if (existingQty < 0)
            throw new ArgumentOutOfRangeException(nameof(existingQty), "Existing quantity must not be negative.");

        long totalQty = existingQty + incomingQty;
        long totalCost = checked(existingQty * existingUnitBasis.Units + incomingQty * incomingUnitBasis.Units);
        return new Money(FixedMath.DivRound(totalCost, totalQty));
    }
}
