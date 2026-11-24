using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class TravelTimeTests
{
    [Test] public void Flat_dirt_road_30km_is_one_day() => TravelTime.Ticks(30_000, 30_000, 100, 100, 100, 1440).Should().Be(1440);
    [Test] public void Uphill_factor_80_is_slower() => TravelTime.Ticks(30_000, 30_000, 100, 80, 100, 1440).Should().Be(1800);
    [Test] public void Difficulty_200_doubles() => TravelTime.Ticks(30_000, 30_000, 100, 100, 200, 1440).Should().Be(2880);
    [Test] public void Rounds_up_and_is_at_least_one() => TravelTime.Ticks(1, 40_000, 150, 150, 100, 1440).Should().Be(1);

    [Test]
    public void Zero_direction_factor_throws()
    {
        var act = () => TravelTime.Ticks(1, 1, 100, 0, 100, 1440);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class RouteNodeAndVehicleTests
{
    [Test]
    public void Settlement_kind_requires_settlement_id()
        => RouteNode.Create(WorldId.New(), RouteNodeKind.Settlement, 1, null, "x").FirstError.Code
            .Should().Be("routenode.settlement.required");

    [Test]
    public void Junction_with_settlement_id_is_error()
        => RouteNode.Create(WorldId.New(), RouteNodeKind.Junction, 1, SettlementId.New(), null).FirstError.Code
            .Should().Be("routenode.settlement.forbidden");

    [Test]
    public void Vehicle_defaults_are_five_in_order()
    {
        var d = VehicleClass.Defaults(WorldId.New());
        d.Select(v => v.Name).Should().Equal("HandCart", "Wagon", "Longboat", "MerchantVessel", "Airship");
        d.Select(v => v.Tier).Should().Equal(1, 2, 1, 2, 1);
        d.Select(v => v.SpeedFactor).Should().Equal(80, 100, 100, 120, 150);
        d.Select(v => v.Category).Should().Equal(RouteCategory.Land, RouteCategory.Land, RouteCategory.Sea, RouteCategory.Sea, RouteCategory.Air);
    }
}

public class RouteTests
{
    private static readonly RouteNodeId A = RouteNodeId.New(), B = RouteNodeId.New();

    private static ErrorOr.ErrorOr<Route> Make(RouteNodeId? b = null, RouteCategory cat = RouteCategory.Land,
        RouteSurface surface = RouteSurface.DirtRoad, int diff = 100, int danger = 0, long len = 1000,
        int ab = 100, int ba = 100, int min = 1, int max = 99)
        => Route.Create(WorldId.New(), A, b ?? B, cat, surface, diff, danger, len, ab, ba, min, max, RouteProvenance.Generated);

    [Test]
    public void Create_rejects_each_error_code()
    {
        Make(b: A).FirstError.Code.Should().Be("route.selfloop");
        Make(len: 0).FirstError.Code.Should().Be("route.length.nonpositive");
        Make(diff: 0).FirstError.Code.Should().Be("route.difficulty.nonpositive");
        Make(danger: -1).FirstError.Code.Should().Be("route.danger.negative");
        Make(ab: 1001).FirstError.Code.Should().Be("route.factor.range");
        Make(ab: 0, ba: 0).FirstError.Code.Should().Be("route.factor.bothZero");
        Make(min: 0).FirstError.Code.Should().Be("route.tier.min");
        Make(min: 3, max: 2).FirstError.Code.Should().Be("route.tier.order");
        Make(cat: RouteCategory.Sea, surface: RouteSurface.Paved).FirstError.Code.Should().Be("route.surface.category");
    }

    [Test]
    public void Sea_and_air_routes_take_their_own_surfaces()
    {
        Make(cat: RouteCategory.Sea, surface: RouteSurface.OpenWater).IsError.Should().BeFalse();
        Make(cat: RouteCategory.Air, surface: RouteSurface.Sky).IsError.Should().BeFalse();
    }

    [Test]
    public void ChangeSurface_returns_old_and_rejects_wrong_category()
    {
        var r = Make().Value;
        r.ChangeSurface(RouteSurface.Paved).Value.Should().Be(RouteSurface.DirtRoad);
        r.Surface.Should().Be(RouteSurface.Paved);
        r.ChangeSurface(RouteSurface.Sky).IsError.Should().BeTrue();
    }

    [Test]
    public void Road_create_keeps_order_and_directions()
    {
        var r1 = RouteId.New(); var r2 = RouteId.New();
        var road = Road.Create(WorldId.New(), "North Way", RouteCategory.Land, RouteProvenance.Generated,
            [(r1, TravelDirection.AtoB), (r2, TravelDirection.BtoA)]).Value;
        road.Routes.Should().Equal(new RoadRouteEntry(0, r1, TravelDirection.AtoB), new RoadRouteEntry(1, r2, TravelDirection.BtoA));
        Road.Create(WorldId.New(), " ", RouteCategory.Land, RouteProvenance.Generated, [(r1, TravelDirection.AtoB)])
            .FirstError.Code.Should().Be("road.name.empty");
    }

    [Test]
    public void EdgesFor_filters_category_tier_and_zero_factor()
    {
        var world = World.Create("w", 1, CalendarDefinition.Default, "v1").Value;
        var vehicles = VehicleClass.Defaults(world.Id).ToDictionary(v => v.Name);
        var steep = Route.Create(world.Id, A, B, RouteCategory.Land, RouteSurface.DirtRoad, 100, 0, 30_000, 100, 0, 1, 1, RouteProvenance.Generated).Value;
        var sea = Route.Create(world.Id, A, B, RouteCategory.Sea, RouteSurface.OpenWater, 100, 0, 100_000, 100, 100, 1, 99, RouteProvenance.Generated).Value;
        var air = Route.Create(world.Id, A, B, RouteCategory.Air, RouteSurface.Sky, 100, 0, 150_000, 100, 100, 1, 99, RouteProvenance.Generated).Value;
        Route[] all = [steep, sea, air];

        TravelGraph.EdgesFor(all, vehicles["Wagon"], world, 1440).Should().BeEmpty();
        var cart = TravelGraph.EdgesFor(all, vehicles["HandCart"], world, 1440).ToList();
        cart.Should().ContainSingle().Which.Direction.Should().Be(TravelDirection.AtoB);
        TravelGraph.EdgesFor(all, vehicles["Longboat"], world, 1440).Should().HaveCount(2).And.OnlyContain(e => e.Route == sea.Id);
        TravelGraph.EdgesFor(all, vehicles["MerchantVessel"], world, 1440).Should().HaveCount(2);
        var airship = TravelGraph.EdgesFor(all, vehicles["Airship"], world, 1440).ToList();
        airship.Should().HaveCount(2).And.OnlyContain(e => e.Route == air.Id);
        airship[0].Ticks.Should().Be(960); // 150 km at 150 km/day × 150 % = 2/3 day
    }

    [Test]
    public void EdgesFor_uses_surface_speed_and_direction()
    {
        var world = World.Create("w", 1, CalendarDefinition.Default, "v1").Value;
        var wagon = VehicleClass.Defaults(world.Id)[1];
        var r = Route.Create(world.Id, A, B, RouteCategory.Land, RouteSurface.DirtRoad, 100, 0, 30_000, 80, 120, 1, 99, RouteProvenance.Generated).Value;
        var edges = TravelGraph.EdgesFor([r], wagon, world, 1440).ToList();
        edges.Single(e => e.Direction == TravelDirection.AtoB).Ticks.Should().Be(1800);
        edges.Single(e => e.Direction == TravelDirection.BtoA).Ticks.Should().Be(1200);
        r.ChangeSurface(RouteSurface.Paved);
        TravelGraph.EdgesFor([r], wagon, world, 1440).First().Ticks.Should().Be(1350);
    }
}

public class TravelGraphTests
{
    private static readonly RouteNodeId A = RouteNodeId.New(), B = RouteNodeId.New(), C = RouteNodeId.New();

    private static TravelEdge E(RouteNodeId f, RouteNodeId t, long ticks, RouteId? r = null)
        => new(r ?? RouteId.New(), f, t, TravelDirection.AtoB, ticks, ticks * 10);

    [Test]
    public void One_way_edge_is_only_travelled_forward()
    {
        var g = new TravelGraph([E(A, B, 10)]);
        g.FindPath(A, B).Should().NotBeNull();
        g.FindPath(B, A).Should().BeNull();
    }

    [Test]
    public void Uphill_and_downhill_ticks_differ_by_direction()
    {
        var g = new TravelGraph([E(A, B, 1800), E(B, A, 1200)]);
        g.FindPath(A, B)!.TotalTicks.Should().Be(1800);
        g.FindPath(B, A)!.TotalTicks.Should().Be(1200);
    }

    [Test]
    public void Cycle_terminates_and_finds_shortest()
    {
        var g = new TravelGraph([E(A, B, 10), E(B, C, 10), E(C, A, 10), E(A, C, 50)]);
        var p = g.FindPath(A, C)!;
        p.TotalTicks.Should().Be(20);
        p.Nodes.Should().Equal(A, B, C);
        p.TotalLengthMetres.Should().Be(200);
    }

    [Test]
    public void Parallel_routes_choose_fewer_ticks()
    {
        var r1 = RouteId.New();
        var g = new TravelGraph([E(A, B, 1200), E(A, B, 900, r1)]);
        g.FindPath(A, B)!.Edges.Single().Route.Should().Be(r1);
    }

    [Test]
    public void Parallel_routes_with_equal_ticks_choose_lower_route_guid()
    {
        var r1 = new RouteId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var r2 = new RouteId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var g = new TravelGraph([E(A, B, 900, r2), E(A, B, 900, r1)]);
        g.FindPath(A, B)!.Edges.Single().Route.Should().Be(r1);
    }

    [Test]
    public void FindReachable_includes_exact_limit_excludes_origin()
    {
        var g = new TravelGraph([E(A, B, 10), E(B, C, 10)]);
        g.FindReachable(A, 10).Select(r => r.Node).Should().Equal(B);
        g.FindReachable(A, 20).Select(r => r.Node).Should().Equal(B, C);
    }
}

public class LegScheduleTests
{
    private static CaravanLeg Leg(int seq, long enter, long exit, long metres = 10_000)
        => new(seq, RouteId.New(), TravelDirection.AtoB, new Tick(enter), new Tick(exit), metres);

    [Test]
    public void Build_is_contiguous_and_ends_at_total_ticks()
    {
        var a = RouteNodeId.New(); var b = RouteNodeId.New(); var c = RouteNodeId.New();
        var path = new TravelGraph([new TravelEdge(RouteId.New(), a, b, TravelDirection.AtoB, 100, 1_000),
            new TravelEdge(RouteId.New(), b, c, TravelDirection.BtoA, 50, 500)]).FindPath(a, c)!;
        var legs = LegSchedule.Build(path, new Tick(1000));
        legs.Select(l => (l.EnterTick.Value, l.ExitTick.Value)).Should().Equal((1000L, 1100L), (1100L, 1150L));
        legs[1].Direction.Should().Be(TravelDirection.BtoA);
        LegSchedule.Validate(legs).IsError.Should().BeFalse();
    }

    [Test]
    public void PositionAt_before_depart_is_origin_and_at_arrive_is_destination()
    {
        IReadOnlyList<CaravanLeg> legs = [Leg(0, 100, 200)];
        LegSchedule.PositionAt(legs, new Tick(99)).Kind.Should().Be(LegPositionKind.AtOrigin);
        LegSchedule.PositionAt(legs, new Tick(200)).Kind.Should().Be(LegPositionKind.AtDestination);
    }

    [Test]
    public void PositionAt_mid_leg_interpolates_metres()
    {
        var p = LegSchedule.PositionAt([Leg(0, 0, 100, 1), Leg(1, 100, 200)], new Tick(150));
        p.Kind.Should().Be(LegPositionKind.OnRoute);
        p.MetresAlong.Should().Be(5_000);
        p.Sequence.Should().Be(1);
    }

    [Test]
    public void TryOnRoute_matches_PositionAt()
    {
        IReadOnlyList<CaravanLeg> legs = [Leg(0, 0, 100, 1), Leg(1, 100, 200)];
        LegSchedule.TryOnRoute(legs, 150, out var index, out var metres).Should().BeTrue();
        (index, metres).Should().Be((1, 5_000L));
        LegSchedule.TryOnRoute(legs, -1, out _, out _).Should().BeFalse();
        LegSchedule.TryOnRoute(legs, 200, out _, out _).Should().BeFalse();
        LegSchedule.TryOnRoute([], 5, out _, out _).Should().BeFalse();
    }

    [Test]
    public void Rebase_mid_leg_splits_leg_and_keeps_position()
    {
        IReadOnlyList<CaravanLeg> legs = [Leg(0, 0, 1000)];
        // New speed: the whole 10 km takes 500 ticks, so the remaining 5 km take 250.
        var r = LegSchedule.Rebase(legs, new Tick(500), (_, m) => m * 500 / 10_000);
        r.Select(l => (l.Sequence, l.EnterTick.Value, l.ExitTick.Value, l.LengthMetres, l.OffsetMetres))
            .Should().Equal((0, 0L, 500L, 5_000L, 0L), (1, 500L, 750L, 5_000L, 5_000L));
        LegSchedule.PositionAt(r, new Tick(500)).MetresAlong.Should().Be(LegSchedule.PositionAt(legs, new Tick(500)).MetresAlong);
        LegSchedule.PositionAt(r, new Tick(625)).MetresAlong.Should().Be(7_500);
        LegSchedule.Validate(r).IsError.Should().BeFalse();
    }

    [Test]
    public void Rebase_at_leg_boundary_keeps_finished_leg()
    {
        IReadOnlyList<CaravanLeg> legs = [Leg(0, 0, 100), Leg(1, 100, 200)];
        var r = LegSchedule.Rebase(legs, new Tick(100), (_, _) => 50);
        r[0].Should().Be(legs[0]);
        (r[1].EnterTick.Value, r[1].ExitTick.Value).Should().Be((100L, 150L));
    }

    [Test]
    public void Validate_rejects_gap()
        => LegSchedule.Validate([Leg(0, 0, 100), Leg(1, 101, 200)]).FirstError.Code.Should().Be("legs.gap");

    [Test]
    public void Caravan_create_derives_ticks_and_replace_legs_updates_arrive()
    {
        IReadOnlyList<CaravanLeg> legs = [Leg(0, 10, 100), Leg(1, 100, 200)];
        var c = Caravan.Create(WorldId.New(), MerchantId.New(), SettlementId.New(), SettlementId.New(), GoodId.New(),
            1, Money.Zero, "Wagon", legs).Value;
        (c.DepartTick.Value, c.ArriveTick.Value).Should().Be((10L, 200L));
        c.ReplaceLegs(LegSchedule.Rebase(legs, new Tick(150), (_, m) => m * 50 / 10_000)).IsError.Should().BeFalse();
        c.ArriveTick.Should().Be(new Tick(175));
        Caravan.Create(WorldId.New(), MerchantId.New(), SettlementId.New(), SettlementId.New(), GoodId.New(),
            1, Money.Zero, "Wagon", [Leg(0, 0, 100), Leg(1, 101, 200)]).FirstError.Code.Should().Be("legs.gap");
    }
}
