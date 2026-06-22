using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Weekly merchant spawn phase: ensures each settlement has a population-scaled number of
/// representative merchants seated there. Spawn-only — surplus merchants are never retired here
/// (per spec §8.1, never delete in-flight). Scoped to the world; settlements iterated in stable
/// id order for determinism.
/// </summary>
public sealed class MerchantSpawnPhase : ISimulationPhase
{
    // NOTE: tunable; promote to World params later.
    private const long MerchantsPerPopulation = 10_000;
    private static readonly Money Capital = new(50_000);
    private const long CargoCapacity = 50;
    private const long Reach = 1000;

    public string Name => "MerchantSpawn";
    public int Order => 5;
    public long CadenceTicks => Tick.DefaultMinutesPerWeek;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var worldId = ctx.World.Id;

        var settlements = (await ctx.Db.Settlements
                .Where(s => s.WorldId == worldId)
                .ToListAsync())
            .OrderBy(s => s.Id.Value)
            .ToList();

        // Local-merge merchants so spawns earlier in this same advance (unsaved) are counted.
        var merchants = await LoadMerchants(ctx, worldId);
        var countBySeat = merchants
            .GroupBy(m => m.Seat)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var settlement in settlements)
        {
            long target = Math.Max(1, settlement.Population / MerchantsPerPopulation);
            long existing = countBySeat.TryGetValue(settlement.Id, out var c) ? c : 0;

            for (long i = 0; i < target - existing; i++)
            {
                var merchant = RepresentativeMerchant
                    .Create(worldId, settlement.Id, Capital, CargoCapacity, Reach).Value;
                ctx.Db.Merchants.Add(merchant);
                await ctx.Log.EmitAsync(LogEventType.MerchantGained,
                    $"A new merchant set up in {settlement.Name}", tick,
                    LogScopeKind.Merchant, merchant.Id.Value, settlement.Id);
            }
        }
    }

    /// <summary>
    /// All merchants for the world, combining saved DB rows with the local tracked set
    /// (entities added earlier in this advance but not yet saved), deduplicated by id.
    /// </summary>
    private static async Task<List<RepresentativeMerchant>> LoadMerchants(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Merchants
            .Where(m => m.WorldId == worldId)
            .ToListAsync();
        var byId = fromDb.ToDictionary(m => m.Id);
        foreach (var local in ctx.Db.Merchants.Local.Where(m => m.WorldId == worldId))
            byId[local.Id] = local;
        return byId.Values.ToList();
    }
}
