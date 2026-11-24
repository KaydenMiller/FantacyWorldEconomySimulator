using ErrorOr;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.Seeding;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>The city panel view model: loading, sections, filters, search, refresh, late results, and the City tab.</summary>
public class CityPanelTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();
    private static readonly Guid RouteAB = Guid.NewGuid(), CaravanIn = Guid.NewGuid(), CaravanOut = Guid.NewGuid(), CaravanHome = Guid.NewGuid();

    internal static SettlementDetails Sample(Guid id, string name = "Ashford") => new(
        new SettlementHeader(id, name, SettlementType.Town, 12_400, SettlementState.Active, PortKind.Coastal, 1834221, 10_000, "Y1 M1 D7 22:40"),
        [new MarketLine("Cloth", GoodCategory.Material, 10, 200, 300), new MarketLine("Grain", GoodCategory.Food, 120, 40, 80)],
        [new ShopInfo(Guid.NewGuid(), "Granary", ShopKind.Retail, 2000, 5_000, [new ShopStockLine("Grain", 100, 50, 80, 30, 60)])],
        [new MerchantInfo(Guid.NewGuid(), 25_000, 400, "Wagon", 35, 500_000, 2_000_000,
            [new TripInfo("Brindle", name, "Wagon", 3_000, 100_000, 9_000)], [CaravanOut])],
        [
            new TravelTimeLine(id, name, B, "Brindle", "HandCart", 4_320, 100_000, KnowledgeSource.Estimated, 0),
            new TravelTimeLine(id, name, B, "Brindle", "Wagon", 2_880, 100_000, KnowledgeSource.Reported, 9_000),
            new TravelTimeLine(B, "Brindle", id, name, "Wagon", 3_000, 100_000, KnowledgeSource.Estimated, 0),
        ],
        [new EntranceLine(RouteAB, "King's Way", RouteCategory.Land, RouteSurface.DirtRoad, "Junction @ tile 12", 50_000, 3, 2)],
        [
            new CaravanHere(CaravanIn, CaravanHereKind.Inbound, "Cloth", 5, "Brindle", "HandCart", 11_500),
            new CaravanHere(CaravanOut, CaravanHereKind.Outbound, "Grain", 40, "Brindle", "Wagon", 13_000),
            new CaravanHere(CaravanHome, CaravanHereKind.Returning, "Grain", 12, "Brindle", "Wagon", 11_000),
        ]);

    /// <summary>A panel over the in-memory test world whose query and settlement list are stubs.</summary>
    private static (CityPanelViewModel Panel, List<MapObject> Selected, List<Guid> Highlighted, Func<int> Calls) Stub(
        Func<Guid, Task<ErrorOr<SettlementDetails>>>? query = null, WorldSession? session = null)
    {
        var selected = new List<MapObject>();
        var highlighted = new List<Guid>();
        var calls = 0;
        var panel = new CityPanelViewModel(session ?? TestWorlds.Session(), selected.Add, highlighted.Add, null,
            (id, _) => { calls++; return query?.Invoke(id) ?? Task.FromResult<ErrorOr<SettlementDetails>>(Sample(id)); },
            () => Task.FromResult<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>>(new List<(Guid, string)> { (A, "Ashford"), (B, "Brindle"), (C, "Corran") }));
        return (panel, selected, highlighted, () => calls);
    }

    [Test]
    public async Task Show_FillsSections_HeaderAndAsOf()
    {
        var (p, _, _, _) = Stub();
        await p.ShowAsync(A);
        p.SettlementId.Should().Be(A);
        p.HeaderText.Should().Be("Ashford");
        p.SubHeaderText.Should().Be("Town · pop 12,400 · port coastal · tile 1834221");
        p.AsOfText.Should().Be("as of Y1 M1 D7 22:40");
        p.MarketTitle.Should().Be("Market (2 goods)");
        p.MarketLines.Should().HaveCount(2);
        p.MarketLines[0].Text.Should().StartWith("Cloth");
        p.ShopsTitle.Should().Be("Shops (1)");
        p.MerchantsTitle.Should().Be("Merchants (1)");
        p.EntrancesTitle.Should().Be("Route entrances (1)");
        p.CaravansTitle.Should().Be("Caravans here (3)");
        (p.MarketExpanded, p.TravelExpanded, p.ShopsExpanded, p.MerchantsExpanded, p.EntrancesExpanded, p.CaravansExpanded)
            .Should().Be((true, true, false, false, false, false));
    }

    [Test]
    public async Task Show_Loading_ThenLoaded()
    {
        var gate = new TaskCompletionSource<ErrorOr<SettlementDetails>>();
        var (p, _, _, _) = Stub(_ => gate.Task);
        var showing = p.ShowAsync(A);
        p.IsLoading.Should().BeTrue();
        p.AsOfText.Should().Be("loading…");
        gate.SetResult(Sample(A));
        await showing;
        p.IsLoading.Should().BeFalse();
        p.AsOfText.Should().StartWith("as of");
    }

    [Test]
    public async Task FromHereOnly_And_VehicleFilter_FilterTravelRows()
    {
        var (p, _, _, _) = Stub();
        await p.ShowAsync(A);
        p.FromHereOnly.Should().BeTrue();
        p.VisibleTravelTimes.Should().HaveCount(2).And.OnlyContain(t => t.FromId == A);
        p.TravelTitle.Should().Be("Travel times (2 of 3)");
        p.FromHereOnly = false;
        p.VisibleTravelTimes.Should().HaveCount(3);
        p.VehicleClasses.Should().Equal("all", "HandCart", "Wagon");
        p.VehicleFilter = "Wagon";
        p.VisibleTravelTimes.Should().HaveCount(2).And.OnlyContain(t => t.VehicleClass == "Wagon");
        p.TravelLines.Should().HaveCount(2);
        p.VehicleFilter = "all";
        p.VisibleTravelTimes.Should().HaveCount(3);
    }

    [Test]
    public async Task Search_FiltersByName_CaseInsensitive_ChoosingShowsIt()
    {
        var (p, _, _, _) = Stub();
        await p.ShowAsync(A);
        p.SearchText = "BRIN";
        p.SearchResults.Select(r => r.Name).Should().Equal("Brindle");
        await p.ChooseAsync(p.SearchResults[0]);
        p.SettlementId.Should().Be(B);
        p.SearchText.Should().BeEmpty();
    }

    [Test]
    public async Task Rows_OpenActions_SelectOnMapAndHighlight()
    {
        var (p, selected, highlighted, _) = Stub();
        await p.ShowAsync(A);
        p.EntranceLines.Single().Open!();
        highlighted.Should().Equal(RouteAB);
        p.CaravanLines.Single(l => l.Text.Contains("Cloth")).Open!();
        selected.Should().Equal(new MapObject(MapObjectKind.Caravan, CaravanIn));
        p.CaravanLines.Where(l => l.Text.StartsWith("Returning", StringComparison.Ordinal) || l.Text.Contains("11,000") || l.Text.Contains("× 12"))
            .Should().OnlyContain(l => l.Open == null, "returning caravans are not on the map");
        p.MerchantLines.Should().Contain(l => l.Open != null, "caravans out are listed under their merchant");
    }

    [Test]
    public async Task DataChanged_Requeries_Detach_Stops()
    {
        var session = TestWorlds.Session();
        var (p, _, _, calls) = Stub(session: session);
        await p.ShowAsync(A);
        calls().Should().Be(1);
        session.RaiseDataChanged();
        await p.Loaded;
        calls().Should().Be(2);
        p.Detach();
        session.RaiseDataChanged();
        calls().Should().Be(2);
    }

    [Test]
    public async Task RefreshAll_RaisesDataChangedOnce_AndRequeries()
    {
        var session = TestWorlds.Session();
        var (p, _, _, calls) = Stub(session: session);
        await p.ShowAsync(A);
        var raised = 0;
        session.DataChanged += (_, _) => raised++;
        p.RefreshAll();
        await p.Loaded;
        raised.Should().Be(1);
        calls().Should().Be(2);
    }

    [Test]
    public async Task LateResult_Dropped_WhenAnotherCityShown()
    {
        var gates = new Dictionary<Guid, TaskCompletionSource<ErrorOr<SettlementDetails>>> { [A] = new(), [B] = new() };
        var (p, _, _, _) = Stub(id => gates[id].Task);
        var first = p.ShowAsync(A);
        var second = p.ShowAsync(B);
        gates[B].SetResult(Sample(B, "Brindle"));
        await second;
        gates[A].SetResult(Sample(A, "Ashford"));
        await first;
        p.HeaderText.Should().Be("Brindle");
        p.SettlementId.Should().Be(B);
    }

    [Test]
    public async Task UnknownSettlement_ShowsError()
    {
        var (p, _, _, _) = Stub(_ => Task.FromResult<ErrorOr<SettlementDetails>>(Error.NotFound("settlement.notFound", "No settlement with id x.")));
        await p.ShowAsync(A);
        p.Error.Should().Be("No settlement with id x.");
        p.Details.Should().BeNull();
    }

    [Test]
    public async Task InMemoryWorld_ShowsNoDatabaseText()
    {
        var p = new CityPanelViewModel(TestWorlds.Session(), _ => { }, _ => { });
        await p.ShowAsync(A);
        p.Error.Should().Be(CityPanelViewModel.NoDatabaseText);
    }

    [Test]
    public async Task RealQuery_OnDemoWorld_MatchesQuery()
    {
        var db = Path.Combine(Path.GetTempPath(), $"gui_city_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(db, TestWorlds.Settings);
            SqliteConnection.ClearAllPools();
            var vm = new MainViewModel();
            (await vm.OpenAsync(db)).Should().BeTrue(vm.StatusText);
            var town = vm.Session!.Data.Settlements.Single(s => s.Name == "Hammerfell");
            vm.OpenSettlement(town.Id);
            await vm.City!.Loaded;
            var expected = (await SettlementDetailsQuery.RunAsync(db, town.Id)).Value;
            vm.City.Details.Should().BeEquivalentTo(expected);
            vm.City.MarketLines.Should().HaveCount(expected.Market.Count);
            vm.SideTabIndex.Should().Be(MainViewModel.CityTabIndex);
            vm.Session.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Test]
    public void SelectingCell_SwitchesFromCityToDetails_KeepsCity()
    {
        var vm = TestWorlds.MainWithWorld();
        var id = vm.Session!.Data.Settlements[0].Id;
        vm.OpenSettlement(id);
        vm.SideTabIndex.Should().Be(MainViewModel.CityTabIndex);
        vm.SelectCell(0);
        vm.SideTabIndex.Should().Be(MainViewModel.DetailsTabIndex);
        vm.City!.SettlementId.Should().Be(id);
        vm.SideTabIndex = MainViewModel.LayersTabIndex;
        vm.SelectCell(1);
        vm.SideTabIndex.Should().Be(MainViewModel.LayersTabIndex, "only the City tab gives way to Details");
    }
}
