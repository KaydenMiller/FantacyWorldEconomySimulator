using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Repositories;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Persistence.Tests.Unit;

public class RoadGraphPersistenceTests
{
    private static WorldDbContext Ctx(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task WithDb(Func<string, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_rg_{Guid.NewGuid():N}.db");
        try
        {
            await using (var ctx = Ctx(path)) await ctx.Database.MigrateAsync();
            await body(path);
        }
        finally { File.Delete(path); }
    }

    private static Route MakeRoute(WorldId w, RouteNodeId a, RouteNodeId b, RouteCategory cat = RouteCategory.Land)
        => Route.Create(w, a, b, cat, Route.DefaultSurface(cat), 120, 2, 50_000, 90, 110, 1, 2, RouteProvenance.Generated).Value;

    [Test]
    public async Task Route_node_route_tiles_road_round_trip()
    {
        await WithDb(async path =>
        {
            var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
            var a = RouteNode.Create(world.Id, RouteNodeKind.Site, 5, null, "Ruin").Value;
            var b = RouteNode.Create(world.Id, RouteNodeKind.Junction, 9, null, null).Value;
            var r1 = MakeRoute(world.Id, a.Id, b.Id);
            var r2 = MakeRoute(world.Id, b.Id, a.Id, RouteCategory.Air);
            var road = Road.Create(world.Id, "Old Way", RouteCategory.Land, RouteProvenance.Generated,
                [(r1.Id, TravelDirection.AtoB), (r2.Id, TravelDirection.BtoA)]).Value;
            await using (var ctx = Ctx(path))
            {
                ctx.Worlds.Add(world);
                await new RouteRepository(ctx).AddRangeAsync([a, b], [r1, r2],
                    RouteTile.For(r1.Id, [5, 7, 8, 9]).Concat(RouteTile.For(r2.Id, [9, 5])), [road]);
                ctx.VehicleClasses.AddRange(VehicleClass.Defaults(world.Id));
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = Ctx(path))
            {
                var routes = await new RouteRepository(ctx).ListByWorldAsync(world.Id);
                routes.Should().HaveCount(2);
                var lr = routes.Single(r => r.Id == r1.Id);
                (lr.NodeA, lr.Surface, lr.SpeedFactorAtoB, lr.SpeedFactorBtoA, lr.MaxTier, lr.LengthMetres)
                    .Should().Be((a.Id, RouteSurface.DirtRoad, 90, 110, 2, 50_000L));
                routes.Single(r => r.Id == r2.Id).Category.Should().Be(RouteCategory.Air);
                (await ctx.RouteTiles.Where(t => t.RouteId == r1.Id).OrderBy(t => t.Sequence).Select(t => t.TileId).ToListAsync())
                    .Should().Equal(5, 7, 8, 9);
                var lroad = await ctx.Roads.SingleAsync();
                lroad.Routes.Select(e => (e.RouteId, e.Direction)).Should().Equal((r1.Id, TravelDirection.AtoB), (r2.Id, TravelDirection.BtoA));
                (await ctx.RouteNodes.SingleAsync(n => n.Id == a.Id)).Name.Should().Be("Ruin");
                (await ctx.VehicleClasses.Where(v => v.WorldId == world.Id).ToListAsync())
                    .Select(v => (v.Name, v.Category, v.Tier)).Should().Contain(("Airship", RouteCategory.Air, 1));
            }
        });
    }

    [Test]
    public async Task RoutesOnTile_returns_both_routes_sharing_a_tile()
    {
        await WithDb(async path =>
        {
            var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
            var a = RouteNode.Create(world.Id, RouteNodeKind.Site, 1, null, "a").Value;
            var b = RouteNode.Create(world.Id, RouteNodeKind.Site, 4, null, "b").Value;
            var r1 = MakeRoute(world.Id, a.Id, b.Id);
            var r2 = MakeRoute(world.Id, a.Id, b.Id);
            await using (var ctx = Ctx(path))
            {
                ctx.Worlds.Add(world);
                await new RouteRepository(ctx).AddRangeAsync([a, b], [r1, r2],
                    RouteTile.For(r1.Id, [1, 2, 4]).Concat(RouteTile.For(r2.Id, [1, 3, 2, 4])), []);
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = Ctx(path))
            {
                var repo = new RouteRepository(ctx);
                (await repo.RoutesOnTileAsync(world.Id, 2)).Should().BeEquivalentTo([r1.Id, r2.Id]);
                (await repo.RoutesOnTileAsync(world.Id, 3)).Should().Equal(r2.Id);
            }
        });
    }

    [Test]
    public async Task Caravan_legs_round_trip_in_sequence()
    {
        await WithDb(async path =>
        {
            var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
            var r1 = RouteId.New(); var r2 = RouteId.New();
            var caravan = Caravan.Create(world.Id, MerchantId.New(), SettlementId.New(), SettlementId.New(), GoodId.New(),
                5, new Money(3), "Longboat",
                [new CaravanLeg(0, r1, TravelDirection.AtoB, new Tick(0), new Tick(100), 1000),
                 new CaravanLeg(1, r2, TravelDirection.BtoA, new Tick(100), new Tick(250), 1500)]).Value;
            await using (var ctx = Ctx(path))
            {
                ctx.Worlds.Add(world);
                ctx.Caravans.Add(caravan);
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = Ctx(path))
            {
                var c = await ctx.Caravans.SingleAsync();
                c.VehicleClassName.Should().Be("Longboat");
                c.Legs.Select(l => (l.Sequence, l.RouteId, l.Direction, l.ExitTick.Value, l.LengthMetres))
                    .Should().Equal((0, r1, TravelDirection.AtoB, 100L, 1000L), (1, r2, TravelDirection.BtoA, 250L, 1500L));
                c.ArriveTick.Should().Be(new Tick(250));
            }
        });
    }

    [Test]
    public void Voyage_path_encoding_round_trips_and_is_compact()
    {
        int[] tiles = [5_000_000, 5_000_001, 4_999_950, 4_999_951, 12, 2_000_000_000];
        VoyagePath.Decode(VoyagePath.Encode(tiles)).Should().Equal(tiles);
        var straight = Enumerable.Range(1_000_000, 2_000).ToArray();
        VoyagePath.Encode(straight).Length.Should().BeLessThan(200);
    }

    [Test]
    public async Task Knowledge_merchant_trips_and_voyage_paths_round_trip()
    {
        await WithDb(async path =>
        {
            var world = World.Create("w", 1, CalendarDefinition.Default, "1").Value;
            var a = SettlementId.New(); var b = SettlementId.New();
            var know = TravelKnowledge.Estimate(world.Id, a, a, b, "Longboat", 2000, 90_000).Value;
            var merchant = RepresentativeMerchant.Create(world.Id, a, Money.Zero, new WorldEcon.SharedKernel.Measure.Mass(1),
                new WorldEcon.SharedKernel.Measure.Volume(1), 10, "Longboat", 25).Value;
            merchant.Remember(new TripRecord(a, b, "Wagon", 100, 1_000, 500));
            merchant.Remember(new TripRecord(b, a, "Longboat", 200, 2_000, 900));
            var node = RouteNode.Create(world.Id, RouteNodeKind.Settlement, 3, a, "Port", PortKind.Coastal).Value;
            var routeId = RouteId.New();
            await using (var ctx = Ctx(path))
            {
                ctx.Worlds.Add(world);
                ctx.TravelKnowledge.Add(know);
                ctx.Merchants.Add(merchant);
                ctx.RouteNodes.Add(node);
                ctx.VoyagePaths.Add(new VoyagePath(routeId, [3, 4, 5, 9]));
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = Ctx(path))
            {
                var k = await ctx.TravelKnowledge.SingleAsync();
                k.Offer(new TripRecord(a, b, "Longboat", 1500, 90_000, 4000)).Should().BeTrue();
                var m = await ctx.Merchants.SingleAsync();
                m.RiskAversion.Should().Be(25);
                m.Trips.OrderBy(t => t.Slot).Select(t => t.VehicleClass).Should().Equal("Longboat", "Wagon");
                m.Remember(new TripRecord(a, b, "Airship", 50, 3_000, 1000));
                (await ctx.RouteNodes.SingleAsync()).PortKind.Should().Be(PortKind.Coastal);
                (await ctx.VoyagePaths.SingleAsync()).Decode().Should().Equal(3, 4, 5, 9);
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = Ctx(path))
            {
                var k = await ctx.TravelKnowledge.SingleAsync();
                (k.Ticks, k.Source, k.UpdatedTick).Should().Be((1500L, KnowledgeSource.Reported, 4000L));
                (await ctx.Merchants.SingleAsync()).Trips.OrderBy(t => t.Slot).Select(t => t.VehicleClass).Should().Equal("Airship", "Longboat");
            }
        });
    }
}
