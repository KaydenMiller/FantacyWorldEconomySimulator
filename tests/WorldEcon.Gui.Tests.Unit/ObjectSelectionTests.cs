using System.Numerics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Geography;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.Seeding;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>Clicking caravans, settlements and nodes; the Routes group toggles; Details rows for each.</summary>
public class ObjectSelectionTests
{
    /// <summary>A main view model over the graph world (two routes, a junction, a site), at Detail View centred on <paramref name="centre"/>.</summary>
    private static (MainViewModel Vm, WorldSession Session) NewMain(WorldEcon.Application.Globe.LoadedGlobe loaded, Vector3d centre)
    {
        var session = new WorldSession(loaded, path: null);
        var vm = TestWorlds.MainWithWorld(session: session);
        vm.Map!.CentreOn(centre);
        vm.Map.ZoomToLevel(MapLevel.Detail);
        return (vm, session);
    }

    /// <summary>Draws the overlay once (object picks use the positions of the last draw).</summary>
    private static void DrawOnce(MainViewModel vm)
    {
        var view = vm.Map!.View;
        using var bmp = new SKBitmap((int)view.Width, (int)view.Height);
        using var canvas = new SKCanvas(bmp);
        vm.Map.Renderer.DrawOverlay(canvas, view, vm.Map.DrawOptions);
    }

    private static (float X, float Y) Screen(MainViewModel vm, Vector3d p)
    {
        vm.Map!.View.ProjectFast(new Vector3((float)p.X, (float)p.Y, (float)p.Z), out var x, out var y).Should().BeTrue();
        return (x, y);
    }

    private static CaravanOnMap Caravan(Guid route, long enter, long exit, string good = "Grain") => new(Guid.NewGuid(), Guid.NewGuid(), "Testburg",
        Guid.NewGuid(), "Testburg", Guid.NewGuid(), "Old Quarry", Guid.NewGuid(), good, 12, 45, "Wagon", enter, exit, 2 * exit - enter,
        [new CaravanLegInfo(0, route, TravelDirection.AtoB, enter, exit, 15_000, 0)]);

    [Test]
    public void Click_OnCaravan_SelectsCaravan_ShowsRows_HighlightsRoute()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        var (vm, s) = NewMain(loaded, loaded.TileGrid.Centre(loaded.Routes[0].Tiles[1]));
        var c = Caravan(r1, 0, 1_000);
        vm.Caravans!.Load(new CaravanPositions(500, [c]));
        DrawOnce(vm);
        var (x, y) = Screen(vm, vm.Caravans.OnRoute[0].Position);
        vm.Click(x, y).Should().BeTrue();
        vm.Map!.SelectedObject.Should().Be(new MapObject(MapObjectKind.Caravan, c.Id));
        vm.Map.HighlightedRoute.Should().Be(r1);
        vm.DetailsTitle.Should().Be("Caravan: Grain × 12");
        var rows = vm.DetailRows.ToDictionary(r => r.Label, r => r.Value);
        rows.Keys.Should().Equal("Owner", "Seat", "From → To", "Goods", "Vehicle", "Route", "Leg", "Progress", "Departs", "Arrives", "Returns", "Cost basis");
        rows["From → To"].Should().Be("Testburg → Old Quarry");
        rows["Goods"].Should().Be("Grain × 12");
        rows["Route"].Should().Be("North Road · Land · DirtRoad");
        rows["Leg"].Should().Be("1 of 1");
        rows["Progress"].Should().Be("7.5 of 15.0 km");
        s.Dispose();
    }

    [Test]
    public void Click_OnMergedMarker_ListsCaravans_AsLinks()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        var (vm, s) = NewMain(loaded, loaded.TileGrid.Centre(loaded.Routes[0].Tiles[1]));
        var a = Caravan(r1, 0, 1_000, "Grain");
        var b = Caravan(r1, 0, 1_000, "Cloth");
        vm.Caravans!.Load(new CaravanPositions(500, [a, b]));
        DrawOnce(vm);
        var (x, y) = Screen(vm, vm.Caravans.OnRoute[0].Position);
        vm.Click(x, y).Should().BeTrue();
        vm.DetailsTitle.Should().Be("2 caravans");
        vm.DetailRows.Should().HaveCount(2).And.OnlyContain(r => r.Open != null);
        vm.DetailRows.Single(r => r.Value.StartsWith("Cloth", StringComparison.Ordinal)).Open!();
        vm.Map!.SelectedObject.Should().Be(new MapObject(MapObjectKind.Caravan, b.Id));
        s.Dispose();
    }

    [Test]
    public void Click_OnSettlement_RaisesSettlementRequested()
    {
        var loaded = TestWorlds.Loaded;
        var town = loaded.Settlements[0];
        var (vm, s) = NewMain(loaded, loaded.TileGrid.Centre(town.TileId!.Value));
        Guid? requested = null;
        vm.SettlementRequested += (_, id) => requested = id;
        DrawOnce(vm);
        var (x, y) = Screen(vm, loaded.TileGrid.Centre(town.TileId.Value));
        vm.Click(x, y).Should().BeTrue();
        requested.Should().Be(town.Id);
        vm.Map!.SelectedObject.Should().Be(new MapObject(MapObjectKind.Settlement, town.Id));
        s.Dispose();
    }

    [Test]
    public void Click_OnSite_ShowsRoutesAndCaravans()
    {
        var (loaded, _, site, _, r2) = WorldSessionGraphTests.Graph();
        var siteTile = loaded.Nodes[1].TileId;
        var (vm, s) = NewMain(loaded, loaded.TileGrid.Centre(siteTile));
        vm.Caravans!.Load(new CaravanPositions(500, [Caravan(r2, 0, 1_000)]));
        DrawOnce(vm);
        var (x, y) = Screen(vm, loaded.TileGrid.Centre(siteTile));
        vm.Click(x, y).Should().BeTrue();
        vm.Map!.SelectedObject.Should().Be(new MapObject(MapObjectKind.Node, site));
        vm.DetailsTitle.Should().Be("Site: Old Quarry");
        vm.DetailRows.Select(r => r.Label).Should().Contain(["Kind", "Name", "Tile", "Lat / lon", "Route", "Caravans on these routes"]);
        vm.DetailRows.Single(r => r.Label == "Caravans on these routes").Value.Should().Be("1");
        s.Dispose();
    }

    [Test]
    public void Click_Empty_FallsBackToCell_AndClearsObject()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        var (vm, s) = NewMain(loaded, loaded.TileGrid.Centre(loaded.Routes[0].Tiles[1]));
        vm.Caravans!.Load(new CaravanPositions(500, [Caravan(r1, 0, 1_000)]));
        DrawOnce(vm);
        var (x, y) = Screen(vm, vm.Caravans.OnRoute[0].Position);
        vm.Click(x, y);
        vm.Click(5, 5).Should().BeTrue();
        vm.Map!.SelectedObject.Should().BeNull();
        vm.Map.HighlightedRoute.Should().BeNull();
        vm.DetailsTitle.Should().MatchRegex("^(Tile|Region cell|Globe cell) "); // tile blocks are not loaded in this test
        s.Dispose();
    }

    [Test]
    public void Toggles_Defaults_AndReachDrawOptions()
    {
        var vm = TestWorlds.MainWithWorld();
        (vm.Map!.Caravans, vm.Map.Junctions, vm.Map.Sites).Should().Be((true, false, true));
        vm.Map.Junctions = true;
        vm.Map.Caravans = false;
        (vm.Map.DrawOptions.Junctions, vm.Map.DrawOptions.Caravans).Should().Be((true, false));
    }

    [Test]
    public void Markers_CarrySettlementIds()
    {
        var vm = TestWorlds.MainWithWorld();
        vm.Map!.DrawOptions.Markers!.Single().Id.Should().Be(vm.Session!.Data.Settlements[0].Id);
    }

    [Test]
    public async Task InMemoryWorld_NoCaravanQuery()
    {
        var vm = TestWorlds.MainWithWorld();
        await vm.CaravansLoaded;
        vm.Caravans!.All.Should().BeEmpty();
        vm.ErrorText.Should().BeEmpty();
    }

    [Test]
    public async Task OpenedFile_LoadsCaravans_WithoutError()
    {
        var db = Path.Combine(Path.GetTempPath(), $"gui_caravans_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(db, TestWorlds.Settings);
            SqliteConnection.ClearAllPools();
            var vm = new MainViewModel();
            (await vm.OpenAsync(db)).Should().BeTrue(vm.StatusText);
            // Opening finishes after the caravans are read: nothing reads the file once OpenAsync has returned.
            vm.CaravansLoaded.IsCompleted.Should().BeTrue();
            vm.ErrorText.Should().BeEmpty();
            var expected = (await CaravanPositionsQuery.RunAsync(db)).Value;
            vm.Caravans!.All.Should().HaveCount(expected.Caravans.Count);
            vm.Session!.CurrentTick.Should().Be(expected.CurrentTick);
            vm.Session.NodeMarkers.Should().Contain(n => n.Label == "Greystone Quarry");
            vm.Session.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Test]
    public void TileDetails_HasNodeRow()
    {
        var (loaded, _, _, _, _) = WorldSessionGraphTests.Graph();
        using var s = new WorldSession(loaded, path: null);
        var j = loaded.Nodes[0];
        DetailFormatter.NodeText(s, j.TileId).Should().Be($"Junction @ tile {j.TileId}");
        DetailFormatter.NodeText(s, loaded.Nodes[1].TileId).Should().Be("Site Old Quarry");
        DetailFormatter.NodeText(s, -5).Should().Be("none");
    }

    [Test]
    public void DrawOptions_CaravanMarkers_AreASnapshot_NotChangedByLaterTicks()
    {
        // The render thread draws from DrawOptions captured earlier; a later SetTick or Load must not change what it reads.
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        var (vm, s) = NewMain(loaded, loaded.TileGrid.Centre(loaded.Routes[0].Tiles[1]));
        vm.Caravans!.Load(new CaravanPositions(500, [Caravan(r1, 0, 1_000)]));
        var captured = vm.Map!.DrawOptions.CaravanMarkers!;
        var before = captured.ToArray();
        vm.Caravans.SetTick(900);
        captured.Should().Equal(before);
        vm.Caravans.Load(new CaravanPositions(500, []));
        captured.Should().Equal(before);
        vm.Map.DrawOptions.CaravanMarkers.Should().BeEmpty();
        s.Dispose();
    }

    [Test]
    public async Task LateRename_DoesNotOverwriteDetails_OfANewSelection()
    {
        var db = Path.Combine(Path.GetTempPath(), $"gui_late_rename_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(db, TestWorlds.Settings);
            SqliteConnection.ClearAllPools();
            var vm = new MainViewModel();
            (await vm.OpenAsync(db)).Should().BeTrue(vm.StatusText);
            var site = vm.Session!.Data.Nodes.Single(n => n.Kind == RouteNodeKind.Site);
            vm.SelectObject(new MapObject(MapObjectKind.Node, site.Id));
            var editor = vm.NodeEditor!;
            editor.Text = "Old Quarry";
            var saving = editor.SaveAsync();
            vm.SelectCell(0); // the user moves on before the save finishes
            await saving;
            vm.DetailsTitle.Should().Be("Globe cell 0");
            vm.Session.Node(site.Id)!.Name.Should().Be("Old Quarry", "the rename itself still applies");
            vm.Session.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }
}
