using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Globe;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Globe;
using WorldEcon.SharedKernel.Measure;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.TestFixtures;

/// <summary>
/// A small economy on a SQLite file for query tests (shared by the Application and CLI test projects):
/// <list type="bullet">
/// <item>World at tick 10,000 with a tile-frequency-2 grid recorded in <c>worldgen_runs</c> (so entrance edges resolve).</item>
/// <item>Ashford (node on tile <see cref="Built.TileA"/>) — unnamed junction — Brindle, joined by routes A→J and J→B of 50 km each on road "King's Way".</item>
/// <item>Corran: a settlement with no node, shops, merchants or knowledge.</item>
/// <item>Ashford shops Granary (grain) and Weaver (cloth, grain); Brindle shop Mill (grain).</item>
/// <item>One merchant seated at Ashford with two remembered trips.</item>
/// <item>Caravans: Outbound (Ashford→Brindle, on route A→J at tick 10,000), Inbound (Brindle→Ashford, on J→B travelling BtoA),
/// Returning (Ashford→Brindle, delivered, not home-reported), Done (delivered and home-reported).</item>
/// <item>Travel knowledge: Ashford knows Ashford→Brindle Wagon (reported at tick 9,000), Brindle→Ashford Wagon (estimate),
/// Ashford→Brindle HandCart (estimate); Brindle knows Brindle→Ashford Wagon (estimate).</item>
/// </list>
/// </summary>
internal static class EconomyTestWorld
{
    public sealed record Built(
        string Path, World World, Settlement Ashford, Settlement Brindle, Settlement Corran,
        RouteNode NodeA, RouteNode NodeB, RouteNode Junction, Route RouteAJ, Route RouteJB,
        Good Grain, Good Cloth, RepresentativeMerchant Merchant,
        Caravan Outbound, Caravan Inbound, Caravan Returning, Caravan Done, int TileA, int TileJ, int TileB) : IDisposable
    {
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(Path);
        }
    }

    public const long Now = 10_000;

    public static WorldDbContext Ctx(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    public static async Task<Built> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"we_econ_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        world.AdvanceTo(new Tick(Now));
        var continent = Continent.Create(world.Id, "C").Value;
        var country = Country.Create(world.Id, continent.Id, "Co").Value;
        var region = PoliticalRegion.Create(world.Id, "R", country.Id).Value;
        var ashford = Settlement.Create(world.Id, region.Id, "Ashford", SettlementType.Town, 0, 0, 12_400).Value;
        var brindle = Settlement.Create(world.Id, region.Id, "Brindle", SettlementType.Village, 0, 0, 900).Value;
        var corran = Settlement.Create(world.Id, region.Id, "Corran", SettlementType.Village, 0, 0, 300).Value;

        var settings = new WorldGenSettings { Grid = new() { Frequency = 2, RegionMultiplier = 1, TileMultiplier = 1 } };
        var grid = new TileGrid(2);
        const int tileA = 12;
        Span<int> nb = stackalloc int[6];
        grid.Neighbours(tileA, nb);
        var tileJ = nb[0];
        Span<int> nb2 = stackalloc int[6];
        var n2 = grid.Neighbours(tileJ, nb2);
        var tileB = -1;
        for (var i = 0; i < n2; i++)
            if (nb2[i] != tileA && !grid.IsPentagon(nb2[i])) { tileB = nb2[i]; break; }
        ashford.PinTo(new GlobeCellId(0), tileA);
        brindle.PinTo(new GlobeCellId(0), tileB);

        var nodeA = RouteNode.Create(world.Id, RouteNodeKind.Settlement, tileA, ashford.Id, ashford.Name, PortKind.None).Value;
        var nodeB = RouteNode.Create(world.Id, RouteNodeKind.Settlement, tileB, brindle.Id, brindle.Name, PortKind.River).Value;
        var junction = RouteNode.Create(world.Id, RouteNodeKind.Junction, tileJ, null, null).Value;
        var routeAJ = Route.Create(world.Id, nodeA.Id, junction.Id, RouteCategory.Land, RouteSurface.DirtRoad, 100, 0, 50_000, 100, 100, 1, 99, RouteProvenance.Generated).Value;
        var routeJB = Route.Create(world.Id, junction.Id, nodeB.Id, RouteCategory.Land, RouteSurface.DirtRoad, 100, 0, 50_000, 100, 100, 1, 99, RouteProvenance.Generated).Value;
        var road = Road.Create(world.Id, "King's Way", RouteCategory.Land, RouteProvenance.Generated,
            [(routeAJ.Id, TravelDirection.AtoB), (routeJB.Id, TravelDirection.AtoB)]).Value;

        var grain = Good.Create(world.Id, "Grain", GoodCategory.Food, new Money(50), "sack", SizeClass.Medium, 0, true).Value;
        var cloth = Good.Create(world.Id, "Cloth", GoodCategory.Material, new Money(200), "bolt", SizeClass.Medium, 0, true).Value;

        var granary = Shop.Create(world.Id, ashford.Id, "Granary", 2000, new Money(5_000)).Value;
        var weaver = Shop.Create(world.Id, ashford.Id, "Weaver", 5000, new Money(7_000)).Value;
        var mill = Shop.Create(world.Id, brindle.Id, "Mill", 1000, new Money(1_000)).Value;
        var s1 = Stockpile.CreateForShop(world.Id, granary.Id, grain.Id, 100, new Money(50)).Value;
        s1.SetMarketPrice(new Money(80));
        var s2 = Stockpile.CreateForShop(world.Id, weaver.Id, cloth.Id, 10, new Money(200)).Value;
        s2.SetMarketPrice(new Money(300));
        var s3 = Stockpile.CreateForShop(world.Id, weaver.Id, grain.Id, 20, new Money(40)).Value;
        s3.SetMarketPrice(new Money(80));
        var s4 = Stockpile.CreateForShop(world.Id, mill.Id, grain.Id, 7, new Money(30)).Value;
        s4.SetMarketPrice(new Money(60));

        var merchant = RepresentativeMerchant.Create(world.Id, ashford.Id, new Money(25_000), new Mass(500_000), new Volume(2_000_000), 400, "Wagon", 35).Value;
        merchant.Remember(new TripRecord(ashford.Id, brindle.Id, "Wagon", 2_880, 100_000, 8_000));
        merchant.Remember(new TripRecord(brindle.Id, ashford.Id, "Wagon", 3_000, 100_000, 9_000));

        static CaravanLeg Leg(int seq, Route r, TravelDirection d, long enter, long exit) => new(seq, r.Id, d, new Tick(enter), new Tick(exit), r.LengthMetres);
        var outbound = Caravan.Create(world.Id, merchant.Id, ashford.Id, brindle.Id, grain.Id, 40, new Money(50), "Wagon",
            [Leg(0, routeAJ, TravelDirection.AtoB, 9_000, 11_000), Leg(1, routeJB, TravelDirection.AtoB, 11_000, 13_000)]).Value;
        var inbound = Caravan.Create(world.Id, merchant.Id, brindle.Id, ashford.Id, cloth.Id, 5, new Money(150), "HandCart",
            [Leg(0, routeJB, TravelDirection.BtoA, 9_500, 10_500), Leg(1, routeAJ, TravelDirection.BtoA, 10_500, 11_500)]).Value;
        var returning = Caravan.Create(world.Id, merchant.Id, ashford.Id, brindle.Id, grain.Id, 12, new Money(45), "Wagon",
            [Leg(0, routeAJ, TravelDirection.AtoB, 7_000, 8_000), Leg(1, routeJB, TravelDirection.AtoB, 8_000, 9_000)]).Value;
        returning.MarkDelivered();
        var done = Caravan.Create(world.Id, merchant.Id, ashford.Id, brindle.Id, grain.Id, 3, new Money(45), "Wagon",
            [Leg(0, routeAJ, TravelDirection.AtoB, 1_000, 2_000), Leg(1, routeJB, TravelDirection.AtoB, 2_000, 3_000)]).Value;
        done.MarkDelivered();
        done.MarkHomeReported();

        var kReported = TravelKnowledge.Estimate(world.Id, ashford.Id, ashford.Id, brindle.Id, "Wagon", 3_000, 100_000).Value;
        kReported.Offer(new TripRecord(ashford.Id, brindle.Id, "Wagon", 2_880, 100_000, 9_000));
        var knowledge = new[]
        {
            kReported,
            TravelKnowledge.Estimate(world.Id, ashford.Id, brindle.Id, ashford.Id, "Wagon", 3_000, 100_000).Value,
            TravelKnowledge.Estimate(world.Id, ashford.Id, ashford.Id, brindle.Id, "HandCart", 4_320, 100_000).Value,
            TravelKnowledge.Estimate(world.Id, brindle.Id, brindle.Id, ashford.Id, "Wagon", 3_000, 100_000).Value,
        };

        await using (var ctx = Ctx(path))
        {
            await ctx.Database.MigrateAsync();
            ctx.Worlds.Add(world);
            ctx.Continents.Add(continent);
            ctx.Countries.Add(country);
            ctx.PoliticalRegions.Add(region);
            ctx.Settlements.AddRange(ashford, brindle, corran);
            ctx.Set<WorldGenRunRow>().Add(new WorldGenRunRow
            {
                WorldId = world.Id, GeneratorVersion = WorldGenerator.GeneratorVersion,
                SettingsJson = WorldGenSettingsJson.Serialize(settings), StagesJson = "[]",
            });
            ctx.RouteNodes.AddRange(nodeA, nodeB, junction);
            ctx.Routes.AddRange(routeAJ, routeJB);
            ctx.RouteTiles.AddRange(RouteTile.For(routeAJ.Id, [tileA, tileJ]));
            ctx.RouteTiles.AddRange(RouteTile.For(routeJB.Id, [tileJ, tileB]));
            ctx.Roads.Add(road);
            ctx.VehicleClasses.AddRange(VehicleClass.Defaults(world.Id));
            ctx.Goods.AddRange(grain, cloth);
            ctx.Shops.AddRange(granary, weaver, mill);
            ctx.Stockpiles.AddRange(s1, s2, s3, s4);
            ctx.Merchants.Add(merchant);
            ctx.Caravans.AddRange(outbound, inbound, returning, done);
            ctx.TravelKnowledge.AddRange(knowledge);
            await ctx.SaveChangesAsync();
        }

        return new Built(path, world, ashford, brindle, corran, nodeA, nodeB, junction, routeAJ, routeJB, grain, cloth, merchant,
            outbound, inbound, returning, done, tileA, tileJ, tileB);
    }
}
