using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Economy;

/// <summary>
/// Computes per-unit cost basis on deposit. Weighted-average now; per-lot/FIFO promotable
/// behind this seam later (spec §3.2, §6.4).
/// </summary>
public interface ICostBasisValuation
{
    /// <summary>New per-unit basis after merging <paramref name="incomingQty"/> units at
    /// <paramref name="incomingUnitBasis"/> into <paramref name="existingQty"/> units at
    /// <paramref name="existingUnitBasis"/>. <paramref name="incomingQty"/> must be &gt; 0.</summary>
    Money Blend(long existingQty, Money existingUnitBasis, long incomingQty, Money incomingUnitBasis);
}
