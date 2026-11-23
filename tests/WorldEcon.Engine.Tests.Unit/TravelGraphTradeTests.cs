using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.Engine.Trade;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Tests.Unit;

/// <summary>
/// Trade over the travel graph and travel knowledge: legs, traffic, which vehicle a seat offers, bulky versus dense cargo,
/// ports, reports to the destination and home, gambles, and route upgrades.
/// </summary>
public class TravelGraphTradeTests
{
    private static WorldDbContext Ctx(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private sealed record World3(string Path, WorldId WorldId, Settlement A, Settlement B, Settlement C, MerchantId Merchant,
        Route[] Routes);

    private sealed record Options(string Vehicle = "Wagon", int Risk = 100, PortKind PortA = PortKind.None,
        PortKind PortB = PortKind.None, PortKind PortC = PortKind.None, long GoodMassG = 1_000, long GoodVolumeCm3 = 1_000,
        long PriceC = 400, int MaxExplorePct = 20, int MaxLossMarginPct = 50, bool WithNodes = true, bool WithEstimates = true);

    /// <summary>
    /// Settlements A, B, C. The good is cheap at A (100, stock 100) and dear at C; B is priced like A. A merchant sits at A.
    /// <paramref name="addRoutes"/> builds the graph over nodes [A, B, C]; estimates are written as world creation does.
    /// </summary>
    private static async Task<World3> SeedAsync(Func<WorldDbContext, WorldId, RouteNode[], Route[]> addRoutes, Options? o = null)
    {
        o ??= new Options();
        var path = Path.Combine(Path.GetTempPath(), $"we_tg_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        world.SetTradeRisk(o.MaxExplorePct, o.MaxLossMarginPct, World.DefaultKnowledgeRadiusTicks);
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = PoliticalRegion.Create(world.Id, "Region", country.Id).Value;
        var a = Settlement.Create(world.Id, region.Id, "A", SettlementType.Village, 1, 1, 100).Value;
        var b = Settlement.Create(world.Id, region.Id, "B", SettlementType.Village, 2, 2, 100).Value;
        var c = Settlement.Create(world.Id, region.Id, "C", SettlementType.Village, 3, 3, 100).Value;
        var good = Good.Create(world.Id, "Goods", GoodCategory.Material, new Money(100), "unit",
            SizeClass.Small, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 0,
            massPerUnit: new Mass(o.GoodMassG), volumePerUnit: new Volume(o.GoodVolumeCm3)).Value;
        var merchant = RepresentativeMerchant.Create(world.Id, a.Id, new Money(100_000),
            new Mass(500_000), new Volume(1_000_000), 86_400, o.Vehicle, o.Risk).Value;

        await using var ctx = Ctx(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world); ctx.Continents.Add(continent); ctx.Countries.Add(country); ctx.PoliticalRegions.Add(region);
        ctx.Settlements.AddRange(a, b, c);
        ctx.Goods.Add(good);
        foreach (var (s, price, qty) in new[] { (a, 100L, 100L), (b, 100L, 0L), (c, o.PriceC, 0L) })
        {
            var shop = Shop.Create(world.Id, s.Id, $"{s.Name} Market", 0, Money.Zero).Value;
            var stock = Stockpile.CreateForShop(world.Id, shop.Id, good.Id, qty, new Money(100)).Value;
            stock.SetMarketPrice(new Money(price));
            ctx.Shops.Add(shop);
            ctx.Stockpiles.Add(stock);
        }
        ctx.Merchants.Add(merchant);
        Route[] routes = [];
        if (o.WithNodes)
        {
            var nodes = RoadGraphFixture.AddNodes(ctx, world.Id, (a, o.PortA), (b, o.PortB), (c, o.PortC));
            routes = addRoutes(ctx, world.Id, nodes);
            if (o.WithEstimates) RoadGraphFixture.AddEstimates(ctx, world, nodes, routes);
        }
        await ctx.SaveChangesAsync();
        return new World3(path, world.Id, a, b, c, merchant.Id, routes);
    }

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = Ctx(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        await new TickEngine(new ISimulationPhase[] { new Phases.TradePhase() }).AdvanceAsync(sim, ticks);
    }

    private static Func<WorldDbContext, WorldId, RouteNode[], Route[]> Chain(long metres, int maxTier = 99)
        => (ctx, w, n) => [RoadGraphFixture.AddRoute(ctx, w, n[0], n[1], metres, maxTier: maxTier),
                           RoadGraphFixture.AddRoute(ctx, w, n[1], n[2], metres, maxTier: maxTier)];

    private static Func<WorldDbContext, WorldId, RouteNode[], Route[]> SeaAndAir(long metres, int seaMaxTier = 99)
        => (ctx, w, n) => [RoadGraphFixture.AddRoute(ctx, w, n[0], n[2], metres, RouteCategory.Sea, seaMaxTier),
                           RoadGraphFixture.AddRoute(ctx, w, n[0], n[2], metres, RouteCategory.Air)];

    private static async Task<Caravan?> OnlyCaravan(string path)
    {
        await using var ctx = Ctx(path);
        return await ctx.Caravans.SingleOrDefaultAsync();
    }

    [Test]
    public async Task Dispatch_writes_legs_matching_path_and_arrive_tick()
    {
        var s = await SeedAsync(Chain(30_000));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            var c = (await OnlyCaravan(s.Path))!;
            c.DestinationId.Should().Be(s.C.Id);
            c.VehicleClassName.Should().Be("Wagon");
            c.Legs.Select(l => l.RouteId).Should().Equal(s.Routes[0].Id, s.Routes[1].Id);
            (c.Legs[0].ExitTick.Value - c.Legs[0].EnterTick.Value).Should().Be(1440); // 30 km of dirt road at 30 km/day
            c.ArriveTick.Value.Should().Be(c.DepartTick.Value + 2880);
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Dispatch_increments_traffic_on_each_route()
    {
        var s = await SeedAsync(Chain(30_000));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            await using var ctx = Ctx(s.Path);
            (await ctx.Routes.ToListAsync()).Should().OnlyContain(r => r.TrafficCount == 1);
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Tier_1_road_is_travelled_by_hand_cart_even_for_a_wagon_merchant()
    {
        var s = await SeedAsync(Chain(30_000, maxTier: 1));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            (await OnlyCaravan(s.Path))!.VehicleClassName.Should().Be("HandCart");
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Bulky_cargo_sails_and_dense_cargo_flies()
    {
        // 100 km by sea and by air between two coastal ports.
        // Bulky (1 kg, 60 L): the airship bills 30 kg a unit and holds 25; the merchant vessel bills 6 kg and holds 100.
        var bulky = await SeedAsync(SeaAndAir(100_000), new Options(PortA: PortKind.Coastal, PortC: PortKind.Coastal, GoodVolumeCm3: 60_000));
        // Dense (1 kg, 1 L): both hold all 100 units; the airship is about twice as fast, so it earns more per day.
        var dense = await SeedAsync(SeaAndAir(100_000), new Options(PortA: PortKind.Coastal, PortC: PortKind.Coastal));
        try
        {
            await AdvanceAsync(bulky.Path, bulky.WorldId, 1440);
            await AdvanceAsync(dense.Path, dense.WorldId, 1440);
            (await OnlyCaravan(bulky.Path))!.VehicleClassName.Should().Be("MerchantVessel");
            (await OnlyCaravan(dense.Path))!.VehicleClassName.Should().Be("Airship");
        }
        finally { File.Delete(bulky.Path); File.Delete(dense.Path); }
    }

    [Test]
    public async Task Bulky_cargo_pays_more_haulage_by_air()
    {
        var vessel = VehicleClass.Defaults(WorldId.New()).Single(v => v.Name == "MerchantVessel");
        var airship = VehicleClass.Defaults(WorldId.New()).Single(v => v.Name == "Airship");
        var cloth = Good.Create(WorldId.New(), "Cloth", GoodCategory.Material, new Money(10), "bolt", SizeClass.Small, 0, true, 0,
            massPerUnit: new Mass(2_000), volumePerUnit: new Volume(60_000)).Value;
        // 10 bolts over 100 km: the airship bills 30 kg a bolt at 400 %, the vessel 6 kg at 30 %.
        Haulage.CostFor(airship, cloth, 10, 100_000, 1).Should().Be(300 * 100 * 4 / 1000);
        Haulage.CostFor(vessel, cloth, 10, 100_000, 1).Should().Be(60 * 100 * 3 / 10 / 1000);
    }

    [Test]
    public async Task River_port_merchant_sails_by_longboat()
    {
        var s = await SeedAsync((ctx, w, n) => [RoadGraphFixture.AddRoute(ctx, w, n[0], n[2], 100_000, RouteCategory.Sea, maxTier: 1)],
            new Options(PortA: PortKind.River, PortC: PortKind.Coastal));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            (await OnlyCaravan(s.Path))!.VehicleClassName.Should().Be("Longboat");
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Inland_seat_cannot_sail()
    {
        var s = await SeedAsync((ctx, w, n) => [RoadGraphFixture.AddRoute(ctx, w, n[0], n[2], 100_000, RouteCategory.Sea)],
            new Options(PortC: PortKind.Coastal));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            (await OnlyCaravan(s.Path)).Should().BeNull();
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Without_knowledge_a_merchant_does_not_travel()
    {
        var s = await SeedAsync(Chain(30_000), new Options(WithEstimates: false));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            (await OnlyCaravan(s.Path)).Should().BeNull("merchants choose from what their seat knows");
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Arrival_reports_to_destination_and_return_reports_home()
    {
        // A dirt road A–B (downhill A→B) so the way back is slower; B is the dear market here.
        var s = await SeedAsync((ctx, w, n) =>
        {
            var r = Route.Create(w, n[0].Id, n[1].Id, RouteCategory.Land, RouteSurface.DirtRoad, 100, 0, 30_000, 120, 80, 1, 99,
                RouteProvenance.Generated).Value;
            ctx.Routes.Add(r);
            return [r];
        }, new Options(PriceC: 100));
        try
        {
            await using (var ctx = Ctx(s.Path))
            {
                var bStock = await ctx.Stockpiles.Where(x => x.Quantity == 0).ToListAsync();
                foreach (var st in bStock) st.SetMarketPrice(new Money(400)); // B and C both dear; only B is reachable
                await ctx.SaveChangesAsync();
            }
            await AdvanceAsync(s.Path, s.WorldId, 1440); // dispatch at tick 1440: 30 km at 36 km/day = 1200 ticks
            await AdvanceAsync(s.Path, s.WorldId, 1440); // tick 2880 ≥ arrive 2640: delivered, B told
            await using (var ctx = Ctx(s.Path))
            {
                var c = await ctx.Caravans.SingleAsync();
                c.Delivered.Should().BeTrue();
                (c.ArriveTick.Value - c.DepartTick.Value).Should().Be(1200);
                var atB = await ctx.TravelKnowledge.Where(k => k.KnowerSettlementId == s.B.Id).ToListAsync();
                var forward = atB.Single(k => k.FromSettlementId == s.A.Id && k.VehicleClass == "Wagon");
                (forward.Source, forward.Ticks, forward.UpdatedTick).Should().Be((KnowledgeSource.Reported, 1200L, c.ArriveTick.Value));
                var back = atB.Single(k => k.FromSettlementId == s.B.Id && k.ToSettlementId == s.A.Id && k.VehicleClass == "Wagon");
                (back.Source, back.Ticks).Should().Be((KnowledgeSource.Reported, 1800L)); // 30 km at 24 km/day back uphill
                var m = await ctx.Merchants.SingleAsync();
                m.Trips.Should().ContainSingle().Which.To.Should().Be(s.B.Id);
                (await ctx.TravelKnowledge.SingleAsync(k => k.KnowerSettlementId == s.A.Id && k.ToSettlementId == s.B.Id && k.VehicleClass == "Wagon"))
                    .Source.Should().Be(KnowledgeSource.Estimated, "the caravan is not home until its return tick");
                c.ReturnTick.Value.Should().Be(c.ArriveTick.Value + 1200);
                c.HomeReported.Should().BeFalse();
            }
            await AdvanceAsync(s.Path, s.WorldId, 1440); // tick 4320 ≥ return tick 3840: home
            await using (var ctx = Ctx(s.Path))
            {
                var c = await ctx.Caravans.OrderBy(x => x.DepartTick).FirstAsync();
                c.HomeReported.Should().BeTrue();
                var home = await ctx.TravelKnowledge.SingleAsync(k => k.KnowerSettlementId == s.A.Id && k.ToSettlementId == s.B.Id && k.VehicleClass == "Wagon");
                (home.Source, home.Ticks).Should().Be((KnowledgeSource.Reported, 1200L));
            }
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Reports_and_choices_do_not_depend_on_advance_step_size()
    {
        var one = await SeedAsync(Chain(30_000), new Options(Risk: 0, MaxExplorePct: 50));
        var steps = one.Path.Replace(".db", "-steps.db");
        File.Copy(one.Path, steps);
        try
        {
            await AdvanceAsync(one.Path, one.WorldId, 1440 * 8);
            for (int i = 0; i < 8; i++) await AdvanceAsync(steps, one.WorldId, 1440);
            async Task<List<string>> Snapshot(string path)
            {
                await using var ctx = Ctx(path);
                var k = (await ctx.TravelKnowledge.ToListAsync())
                    .Select(x => $"{x.KnowerSettlementId.Value} {x.FromSettlementId.Value}>{x.ToSettlementId.Value} {x.VehicleClass} {x.Ticks} {x.Source} {x.UpdatedTick}");
                var c = (await ctx.Caravans.ToListAsync())
                    .Select(x => $"{x.DestinationId.Value} {x.VehicleClassName} {x.Quantity} {x.DepartTick.Value} {x.ArriveTick.Value} {x.HomeReported}");
                return k.Concat(c).Order().ToList();
            }
            var a = await Snapshot(one.Path);
            a.Should().Contain(r => r.Contains("Reported"));
            a.Should().Equal(await Snapshot(steps));
        }
        finally { File.Delete(one.Path); File.Delete(steps); }
    }

    [Test]
    public void Gamble_weights_fall_with_expected_loss_and_stop_at_the_limit()
    {
        var world = World.Create("w", 7, CalendarDefinition.Default, "1").Value;
        world.SetTradeRisk(100, 50, World.DefaultKnowledgeRadiusTicks);
        var bold = RepresentativeMerchant.Create(world.Id, SettlementId.New(), Money.Zero, new Mass(1), new Volume(1), 1, riskAversion: 0).Value;
        var cautious = RepresentativeMerchant.Create(world.Id, SettlementId.New(), Money.Zero, new Mass(1), new Volume(1), 1, riskAversion: 100).Value;
        Phases.TradePhase.Gambles(world, cautious, new Tick(0)).Should().BeFalse();
        Phases.TradePhase.Gambles(world, bold, new Tick(0)).Should().BeTrue("explore chance 100 % at risk aversion 0");

        var vehicle = VehicleClass.Defaults(world.Id)[1];
        var good = Good.Create(world.Id, "g", GoodCategory.Material, new Money(1), "u", SizeClass.Small, 0, true, 0).Value;
        Phases.TradePhase.TripCandidate Cand(long marginBp) => new(good, SettlementId.New(), vehicle, 1, 1, marginBp, 10_000, marginBp, 0);
        // Losses at or past the 50 % limit are never drawn, at any tick.
        for (long t = 0; t < 200; t++)
            Phases.TradePhase.PickGamble(world, bold, new Tick(t), [Cand(-5_000), Cand(-9_000)]).Should().BeNull();
        // A small loss is drawn far more often than a large one.
        int small = 0, large = 0;
        for (long t = 0; t < 2_000; t++)
        {
            var pick = Phases.TradePhase.PickGamble(world, bold, new Tick(t), [Cand(-500), Cand(-4_500)])!;
            if (pick.MarginBp == -500) small++; else large++;
        }
        small.Should().BeGreaterThan(large * 5);
        // A cautious-ish merchant (risk 80) has a 10 % limit: a 20 % loss is never taken.
        var careful = RepresentativeMerchant.Create(world.Id, SettlementId.New(), Money.Zero, new Mass(1), new Volume(1), 1, riskAversion: 80).Value;
        for (long t = 0; t < 200; t++)
            Phases.TradePhase.PickGamble(world, careful, new Tick(t), [Cand(-2_000)]).Should().BeNull();
    }

    [Test]
    public async Task Gambler_sometimes_takes_a_worse_trip()
    {
        // Two destinations by the same road: C (dear) is best; B (a little dear) is the gamble. A bold merchant with a 100 %
        // explore chance never takes the best trip when another profitable one exists.
        var s = await SeedAsync(Chain(30_000), new Options(Risk: 0, MaxExplorePct: 100));
        try
        {
            await using (var ctx = Ctx(s.Path))
            {
                var shopB = await ctx.Shops.SingleAsync(x => x.SettlementId == s.B.Id);
                var stockB = await ctx.Stockpiles.SingleAsync(x => x.OwnerId == shopB.Id.Value);
                stockB.SetMarketPrice(new Money(150));
                await ctx.SaveChangesAsync();
            }
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            await using (var ctx = Ctx(s.Path))
            {
                var c = await ctx.Caravans.SingleAsync();
                (c.DestinationId != s.C.Id || c.VehicleClassName != "Wagon").Should().BeTrue("the gamble skips the best trip");
                (await ctx.LogEvents.CountAsync(e => e.Type == LogEventType.MerchantDeparted && e.Message.Contains("gamble"))).Should().Be(1);
            }
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Merchant_without_seat_node_is_skipped_with_one_log_event()
    {
        var s = await SeedAsync((ctx, w, n) =>
        {
            ctx.RouteNodes.Remove(n[0]);
            return [RoadGraphFixture.AddRoute(ctx, w, n[1], n[2], 30_000)];
        }, new Options(WithEstimates: false));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            await using var ctx = Ctx(s.Path);
            (await ctx.LogEvents.CountAsync(e => e.Type == LogEventType.MerchantSkippedNoNode)).Should().Be(1);
            (await ctx.Caravans.CountAsync()).Should().Be(0);
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task World_with_routes_but_no_nodes_throws_regenerate_message()
    {
        var s = await SeedAsync((_, _, _) => [], new Options(WithNodes: false));
        try
        {
            await using (var ctx = Ctx(s.Path))
            {
                ctx.Routes.Add(Route.Create(s.WorldId, RouteNodeId.New(), RouteNodeId.New(), RouteCategory.Land,
                    RouteSurface.DirtRoad, 100, 0, 1000, 100, 100, 1, 99, RouteProvenance.Generated).Value);
                await ctx.SaveChangesAsync();
            }
            var act = () => AdvanceAsync(s.Path, s.WorldId, 1440);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("world has no road graph — regenerate the world");
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task World_without_routes_trades_nothing()
    {
        var s = await SeedAsync((_, _, _) => [], new Options(WithNodes: false));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440);
            (await OnlyCaravan(s.Path)).Should().BeNull();
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Upgrade_mid_leg_moves_arrive_earlier_and_ignores_finished_legs()
    {
        var s = await SeedAsync(Chain(30_000));
        try
        {
            await AdvanceAsync(s.Path, s.WorldId, 1440); // dispatch at tick 0; legs 0–1440, 1440–2880
            Tick depart, arriveBefore;
            await using (var ctx = Ctx(s.Path))
            {
                var c = await ctx.Caravans.SingleAsync();
                depart = c.DepartTick;
                arriveBefore = c.ArriveTick;
            }
            int moved;
            await using (var ctx = Ctx(s.Path))
            {
                var world = await ctx.Worlds.SingleAsync();
                var at = new Tick(depart.Value + 2160); // half way along the second leg
                moved = (await RouteUpgradeService.UpgradeAsync(ctx, world, s.Routes[1].Id, RouteSurface.Paved, at,
                    new LogEventEmitter(ctx, world.Id))).Value;
                (await RouteUpgradeService.UpgradeAsync(ctx, world, s.Routes[0].Id, RouteSurface.Paved, new Tick(at.Value + 1),
                    new LogEventEmitter(ctx, world.Id))).Value.Should().Be(0); // first leg already finished
                await ctx.SaveChangesAsync();
            }
            moved.Should().Be(1);
            await using (var ctx = Ctx(s.Path))
            {
                var c = await ctx.Caravans.SingleAsync();
                c.ArriveTick.Value.Should().Be(depart.Value + 2160 + 540); // remaining 15 km at 40 km/day
                c.ArriveTick.Value.Should().BeLessThan(arriveBefore.Value);
                c.Legs[0].ExitTick.Value.Should().Be(depart.Value + 1440);
                c.Legs.Should().HaveCount(3); // second leg split at the upgrade tick
                c.Legs[2].OffsetMetres.Should().Be(15_000);
                (await ctx.RouteUpgrades.CountAsync()).Should().Be(2);
                (await ctx.LogEvents.CountAsync(e => e.Type == LogEventType.RouteUpgraded)).Should().Be(2);
            }
        }
        finally { File.Delete(s.Path); }
    }

    [Test]
    public async Task Upgrade_to_sea_surface_on_land_route_is_rejected()
    {
        var s = await SeedAsync(Chain(30_000));
        try
        {
            await using var ctx = Ctx(s.Path);
            var world = await ctx.Worlds.SingleAsync();
            (await RouteUpgradeService.UpgradeAsync(ctx, world, s.Routes[0].Id, RouteSurface.OpenWater, Tick.Zero,
                new LogEventEmitter(ctx, world.Id))).FirstError.Code.Should().Be("route.surface.category");
        }
        finally { File.Delete(s.Path); }
    }
}
