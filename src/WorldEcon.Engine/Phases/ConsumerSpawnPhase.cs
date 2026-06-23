using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>Weekly: ensure each settlement has population-scaled representative consumers seated there.
/// Spawn-only; new consumers start with an empty budget (the income phase funds them). Mirror of
/// <see cref="MerchantSpawnPhase"/>.</summary>
public sealed class ConsumerSpawnPhase : ISimulationPhase
{
    // NOTE: tunable; promote to World params later.
    public const long DefaultConsumerSize = 1000;

    public string Name => "ConsumerSpawn";
    public int Order => 6;                       // after MerchantSpawn (5), before ConsumerIncome (7)
    public long CadenceTicks => Tick.DefaultMinutesPerWeek;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var worldId = ctx.World.Id;

        var settlements = (await ctx.Db.Settlements.Where(s => s.WorldId == worldId).ToListAsync())
            .OrderBy(s => s.Id.Value).ToList();

        var consumers = await LoadConsumers(ctx, worldId);
        var countBySeat = consumers.GroupBy(c => c.Seat).ToDictionary(g => g.Key, g => g.Count());

        foreach (var settlement in settlements)
        {
            long target = Math.Max(1, settlement.Population / DefaultConsumerSize);
            long existing = countBySeat.TryGetValue(settlement.Id, out var c) ? c : 0;
            for (long i = 0; i < target - existing; i++)
            {
                var consumer = RepresentativeConsumer.Create(worldId, settlement.Id, DefaultConsumerSize, Money.Zero).Value;
                ctx.Db.Consumers.Add(consumer);
            }
        }
    }

    private static async Task<List<RepresentativeConsumer>> LoadConsumers(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Consumers.Where(c => c.WorldId == worldId).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Consumers.Local.Where(c => c.WorldId == worldId))
            byId[local.Id] = local;
        return byId.Values.ToList();
    }
}
