using FluentAssertions;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.TestFixtures;
using WorldEcon.WorldGen.Grid;
using TileChords = WorldEcon.WorldGen.Roads.TileChords;

namespace WorldEcon.Application.Tests.Unit;

public class SettlementDetailsQueryTests
{
    [Test]
    public async Task Header_Filled()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var h = (await SettlementDetailsQuery.RunAsync(w.Path, w.Brindle.Id.Value)).Value.Header;
        h.Should().Be(new SettlementHeader(w.Brindle.Id.Value, "Brindle", SettlementType.Village, 900, SettlementState.Active,
            PortKind.River, w.TileB, EconomyTestWorld.Now, h.ReadDate));
        var d = new WorldEcon.Simulation.Time.CalendarSystem(w.World.Calendar).ToDate(new WorldEcon.SharedKernel.Tick(EconomyTestWorld.Now));
        h.ReadDate.Should().Be($"Y{d.Year} M{d.Month} D{d.Day} {d.Hour:D2}:{d.Minute:D2}");
    }

    [Test]
    public async Task Market_AggregatesShops_OrderedByGood()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Market.Should().Equal(
            new MarketLine("Cloth", GoodCategory.Material, 10, 200, 300),
            new MarketLine("Grain", GoodCategory.Food, 120, 40, 80));
    }

    [Test]
    public async Task Shops_StockAndMargin()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var shops = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Shops;
        shops.Select(s => s.Name).Should().Equal("Granary", "Weaver");
        var weaver = shops[1];
        weaver.MarkupBp.Should().Be(5000);
        weaver.Till.Should().Be(7_000);
        weaver.Kind.Should().Be(ShopKind.Retail);
        weaver.Stock.Should().Equal(
            new ShopStockLine("Cloth", 10, 200, 300, 100, 50.0),
            new ShopStockLine("Grain", 20, 40, 80, 40, 100.0));
    }

    [Test]
    public async Task Merchants_LastTripsAndCaravansOut()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var m = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Merchants.Single();
        m.Should().BeEquivalentTo(new
        {
            Id = w.Merchant.Id.Value, Capital = 25_000L, Reach = 400L, VehicleClass = "Wagon", RiskAversion = 35,
            WeightCapacityGrams = 500_000L, VolumeCapacityCm3 = 2_000_000L,
        });
        m.LastTrips.Should().Equal(
            new TripInfo("Brindle", "Ashford", "Wagon", 3_000, 100_000, 9_000),
            new TripInfo("Ashford", "Brindle", "Wagon", 2_880, 100_000, 8_000));
        m.CaravansOut.Should().BeEquivalentTo([w.Outbound.Id.Value, w.Inbound.Id.Value]);
        (await SettlementDetailsQuery.RunAsync(w.Path, w.Brindle.Id.Value)).Value.Merchants.Should().BeEmpty();
    }

    [Test]
    public async Task TravelTimes_KnowerOnly_Ordered()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var rows = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.TravelTimes;
        rows.Select(r => (r.From, r.To, r.VehicleClass, r.Ticks, r.Source, r.UpdatedTick)).Should().Equal(
            ("Ashford", "Brindle", "HandCart", 4_320L, KnowledgeSource.Estimated, 0L),
            ("Ashford", "Brindle", "Wagon", 2_880L, KnowledgeSource.Reported, 9_000L),
            ("Brindle", "Ashford", "Wagon", 3_000L, KnowledgeSource.Estimated, 0L));
        rows[0].FromId.Should().Be(w.Ashford.Id.Value);
        rows[0].LengthMetres.Should().Be(100_000);
    }

    [Test]
    public async Task Entrances_EdgeAndOtherEnd()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var e = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Entrances.Single();
        var edge = TileChords.EdgeIndex(new TileGrid(2), w.TileA, w.TileJ);
        e.Should().Be(new EntranceLine(w.RouteAJ.Id.Value, "King's Way", RouteCategory.Land, RouteSurface.DirtRoad,
            $"Junction @ tile {w.TileJ}", 50_000, 0, edge));
    }

    [Test]
    public async Task Caravans_InboundOutboundReturning()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var c = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Caravans;
        c.Should().Equal(
            new CaravanHere(w.Inbound.Id.Value, CaravanHereKind.Inbound, "Cloth", 5, "Brindle", "HandCart", 11_500),
            new CaravanHere(w.Outbound.Id.Value, CaravanHereKind.Outbound, "Grain", 40, "Brindle", "Wagon", 13_000),
            new CaravanHere(w.Returning.Id.Value, CaravanHereKind.Returning, "Grain", 12, "Brindle", "Wagon", 11_000));
        var atBrindle = (await SettlementDetailsQuery.RunAsync(w.Path, w.Brindle.Id.Value)).Value.Caravans;
        atBrindle.Select(x => (x.Id, x.Kind)).Should().Equal(
            (w.Outbound.Id.Value, CaravanHereKind.Inbound), (w.Inbound.Id.Value, CaravanHereKind.Outbound));
    }

    [Test]
    public async Task EmptySettlement_AllSectionsEmpty()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var d = (await SettlementDetailsQuery.RunAsync(w.Path, w.Corran.Id.Value)).Value;
        d.Header.Name.Should().Be("Corran");
        d.Header.Port.Should().Be(PortKind.None);
        d.Header.TileId.Should().BeNull();
        d.Market.Should().BeEmpty();
        d.Shops.Should().BeEmpty();
        d.Merchants.Should().BeEmpty();
        d.TravelTimes.Should().BeEmpty();
        d.Entrances.Should().BeEmpty();
        d.Caravans.Should().BeEmpty();
    }

    [Test]
    public async Task Unknown_NotFound()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        (await SettlementDetailsQuery.RunAsync(w.Path, Guid.NewGuid())).FirstError.Code.Should().Be("settlement.notFound");
    }

    [Test]
    public async Task List_OrderedByName()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        (await SettlementDetailsQuery.ListAsync(w.Path)).Value.Select(x => x.Name).Should().Equal("Ashford", "Brindle", "Corran");
    }
}
