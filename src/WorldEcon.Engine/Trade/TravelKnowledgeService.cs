using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;

namespace WorldEcon.Engine.Trade;

/// <summary>
/// The one way travel knowledge changes: a settlement hears about a trip (<see cref="Report"/>). Merchants' trips arrive
/// here now; later events (storms, pirate sightings) report through the same method. Reads merge EF <c>Local</c> with the
/// database so reports made earlier in the same advance are seen.
/// </summary>
public static class TravelKnowledgeService
{
    /// <summary>Gives <paramref name="knower"/> the trip when it is newer than its row for the pair and class. True when it changed.</summary>
    public static async Task<bool> Report(WorldDbContext db, WorldId world, SettlementId knower, TripRecord trip)
    {
        if (trip.From == trip.To) return false;
        var row = db.TravelKnowledge.Local.FirstOrDefault(k => k.KnowerSettlementId == knower && k.FromSettlementId == trip.From
                      && k.ToSettlementId == trip.To && k.VehicleClass == trip.VehicleClass)
                  ?? await db.TravelKnowledge.FirstOrDefaultAsync(k => k.KnowerSettlementId == knower && k.FromSettlementId == trip.From
                      && k.ToSettlementId == trip.To && k.VehicleClass == trip.VehicleClass);
        if (row is not null) return row.Offer(trip);
        db.TravelKnowledge.Add(TravelKnowledge.FromReport(world, knower, trip));
        return true;
    }

    /// <summary>What <paramref name="seat"/> knows about trips starting at itself (database rows merged with unsaved ones).</summary>
    public static async Task<List<TravelKnowledge>> FromSeat(WorldDbContext db, SettlementId seat)
    {
        var rows = await db.TravelKnowledge.Where(k => k.KnowerSettlementId == seat && k.FromSettlementId == seat).ToListAsync();
        var byKey = rows.ToDictionary(k => (k.ToSettlementId, k.VehicleClass));
        foreach (var k in db.TravelKnowledge.Local.Where(k => k.KnowerSettlementId == seat && k.FromSettlementId == seat))
            byKey[(k.ToSettlementId, k.VehicleClass)] = k;
        return byKey.Values.OrderBy(k => k.ToSettlementId.Value).ThenBy(k => k.VehicleClass, StringComparer.Ordinal).ToList();
    }
}
