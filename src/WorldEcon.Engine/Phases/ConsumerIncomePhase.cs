using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Demand;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>Weekly "paycheck": grants each consumer this period's income via the configured
/// <see cref="IConsumerIncome"/> strategy (default <see cref="AllowanceIncome"/>). The strategy is the
/// seam a future wage/labor phase replaces.</summary>
public sealed class ConsumerIncomePhase : ISimulationPhase
{
    private readonly IConsumerIncome _income;

    public ConsumerIncomePhase(IConsumerIncome? income = null) => _income = income ?? new AllowanceIncome();

    public string Name => "ConsumerIncome";
    public int Order => 7;
    public long CadenceTicks => Tick.DefaultMinutesPerWeek;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var worldId = ctx.World.Id;

        var fromDb = await ctx.Db.Consumers.Where(c => c.WorldId == worldId).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Consumers.Local.Where(c => c.WorldId == worldId))
            byId[local.Id] = local;

        foreach (var consumer in byId.Values.OrderBy(c => c.Id.Value))
        {
            var grant = _income.GrantFor(consumer.Size);
            consumer.Earn(grant);
            ctx.Money.Record(MoneyChannel.ConsumerAllowance, grant.Units); // faucet
        }
    }
}
