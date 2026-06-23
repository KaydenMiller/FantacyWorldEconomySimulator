using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Demand;

/// <summary>Flat per-capita allowance (a faucet). Money enters the model here; the loop is closed by
/// the future wage phase. Tunable per-capita amount.</summary>
public sealed class AllowanceIncome : IConsumerIncome
{
    private readonly long _perCapitaAllowance;

    // Default tuned so a consumer can afford its Essential tier with margin (demo: bread 50bp/capita).
    public AllowanceIncome(long perCapitaAllowance = 40) => _perCapitaAllowance = perCapitaAllowance;

    public Money GrantFor(long size) => new(size * _perCapitaAllowance);
}
