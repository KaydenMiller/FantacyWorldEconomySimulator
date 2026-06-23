using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Demand;

/// <summary>How a consumer's periodic income is computed. Phase 2 ships <see cref="AllowanceIncome"/>;
/// a later wage/labor phase swaps in an implementation derived from production labor.</summary>
public interface IConsumerIncome
{
    /// <summary>Income granted this period to a consumer representing <paramref name="size"/> people.</summary>
    Money GrantFor(long size);
}
