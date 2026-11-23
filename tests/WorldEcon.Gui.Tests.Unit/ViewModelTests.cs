using System.Globalization;
using FluentAssertions;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Gui.Tests.Unit;

public class ViewModelTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Row(MainViewModel vm, string label) =>
        vm.DetailRows.Single(r => r.Label == label).Value;

    /// <summary>Centres the map on the settlement's globe cell (land, with land neighbours) at <paramref name="level"/>.</summary>
    private static MainViewModel AtLevel(MapLevel level, WorldSession? session = null)
    {
        var vm = TestWorlds.MainWithWorld(session: session);
        var map = vm.Map!;
        map.CentreOn(vm.Session!.Layers.Geometry.Centre(vm.Session.Markers[0].GlobeCell));
        map.ZoomToLevel(level);
        map.Level.Should().Be(level);
        return vm;
    }

    private static double Dot(Vector3d a, Vector3d b) => Vector3d.Dot(a, b);

    // ---- selection and details at each level ----

    [Test]
    public void Selecting_a_globe_cell_fills_the_details_panel()
    {
        var vm = TestWorlds.MainWithWorld();
        var l = vm.Session!.Layers;
        var c = TestWorlds.FirstLandCell(l);

        vm.SelectCell(c);

        vm.Map!.Selected.Should().Be(new MapPick(MapLevel.Globe, c, c, -1));
        vm.DetailsTitle.Should().Be($"Globe cell {c}");
        Row(vm, "Cell id").Should().StartWith(c.ToString(Inv));
        Row(vm, "Biome").Should().Be(((Biome)l.BiomeOf[c]).ToString());
        Row(vm, "Elevation").Should().Contain($"{l.ElevationM[c]:N0} m");
        Row(vm, "Composition").Should().Contain(l.Composition[c][0].Landform.ToString());
        Row(vm, "Settlements").Should().Be("Testburg");
        vm.DetailRows.Select(r => r.Label).Should().Contain(["Lat / lon", "Temperature", "Precipitation", "Wind", "Current", "Water body", "Flow", "Plate", "Areas", "Neighbours"]);
        vm.CanZoomToRegion.Should().BeTrue();
    }

    [Test]
    public void Clicking_at_Globe_View_selects_the_globe_cell_under_the_pointer_and_clicking_space_keeps_the_selection()
    {
        var vm = AtLevel(MapLevel.Globe);
        var map = vm.Map!;
        var c = vm.Session!.Markers[0].GlobeCell;
        var (x, y) = map.View.Project(vm.Session.Layers.Geometry.Centre(c))!.Value;

        vm.Click(x, y).Should().BeTrue();

        map.Selected!.Value.Level.Should().Be(MapLevel.Globe);
        map.Selected.Value.Id.Should().Be(c);
        vm.DetailsTitle.Should().Be($"Globe cell {c}");
        vm.Click(1, 1).Should().BeFalse("the corner is off the globe disc");
        map.Selected.Value.Id.Should().Be(c);
    }

    [Test]
    public void Clicking_at_Region_View_shows_the_region_cell_summary_and_its_parent_globe_cell()
    {
        var vm = AtLevel(MapLevel.Region);
        var s = vm.Session!.Regions;

        vm.Click(vm.Map!.View.CentreX, vm.Map.View.CentreY).Should().BeTrue();

        var pick = vm.Map.Selected!.Value;
        pick.Level.Should().Be(MapLevel.Region);
        var r = pick.Id;
        vm.DetailsTitle.Should().Be($"Region cell {r}");
        Row(vm, "Kind").Should().Be(((Landform)s.Kind[r]).ToString());
        Row(vm, "Biome").Should().Be(((Biome)s.Biome[r]).ToString());
        Row(vm, "Elevation").Should().StartWith($"{s.ElevationM[r].ToString("N0", Inv)} m mean");
        Row(vm, "Temperature").Should().Be($"{(s.TemperatureDeciC[r] / 10.0).ToString("F1", Inv)} °C mean");
        Row(vm, "Precipitation").Should().StartWith(s.PrecipitationMm[r].ToString("N0", Inv));
        Row(vm, "Mountain share").Should().StartWith((s.MountainShareBp[r] / 100.0).ToString("F1", Inv));
        Row(vm, "Flow").Should().StartWith(s.Flow[r].ToString("N0", Inv));
        Row(vm, "Globe cell").Should().Be(s.GlobeParent[r].ToString(Inv));
        pick.GlobeCell.Should().Be(s.GlobeParent[r]);
        vm.DetailRows.Select(x => x.Label).Should().Contain(["Lake", "Downstream", "Tiles", "Lat / lon"]);
        vm.CanZoomToRegion.Should().BeTrue();
    }

    [Test]
    public async Task Clicking_at_Detail_View_shows_the_tile_values_after_its_block_loads()
    {
        var vm = AtLevel(MapLevel.Detail);
        var map = vm.Map!;
        await map.LoadVisibleTilesAsync();
        var marker = vm.Session!.Markers[0];
        var tile = marker.TileId;
        var p = map.Renderer.ProjectInGroup(map.View, vm.Session.World.Tiles!.Centre(tile), marker.GlobeCell, map.Borders)!.Value;

        vm.Click(p.X, p.Y).Should().BeTrue();
        await vm.DetailsLoaded;

        map.Selected!.Value.Should().Be(new MapPick(MapLevel.Detail, tile, marker.GlobeCell, vm.Session.RegionCellOfTile(tile)));
        vm.DetailsTitle.Should().Be($"Tile {tile}");
        var block = TestWorlds.Generated.Tiles!;
        Row(vm, "Elevation").Should().StartWith($"{block.ElevationM[tile].ToString("N0", Inv)} m");
        Row(vm, "Landform").Should().Be(((Landform)block.Landform[tile]).ToString());
        Row(vm, "Biome").Should().Be(((Biome)block.Biome[tile]).ToString());
        Row(vm, "Water").Should().Be(((TileWaterKind)block.WaterKind[tile]).ToString());
        Row(vm, "Temperature").Should().Be($"{(block.TemperatureDeciC[tile] / 10.0).ToString("F1", Inv)} °C");
        Row(vm, "Globe cell").Should().Be(marker.GlobeCell.ToString(Inv));
        Row(vm, "Region cell").Should().Be(vm.Session.RegionCellOfTile(tile).ToString(Inv));
        Row(vm, "Settlements").Should().Be("Testburg");
        vm.DetailRows.Select(x => x.Label).Should().Contain(["Lat / lon", "Lake", "Flow", "Precipitation", "Area"]);
        vm.CanZoomToRegion.Should().BeFalse("only globe and region cells zoom to Region View");
    }

    [Test]
    public async Task Tile_details_show_loading_until_the_block_arrives()
    {
        var gated = new GatedTileSource(TestWorlds.Loaded.Tiles);
        var vm = TestWorlds.MainWithWorld(session: TestWorlds.Session(gated));
        var marker = vm.Session!.Markers[0];

        vm.Select(new MapPick(MapLevel.Detail, marker.TileId, marker.GlobeCell, vm.Session.RegionCellOfTile(marker.TileId)));

        vm.DetailsTitle.Should().Be($"Tile {marker.TileId}");
        vm.DetailRows.Should().Equal(new DetailRow("Status", MainViewModel.LoadingText));
        vm.DetailsLoaded.IsCompleted.Should().BeFalse();
        gated.Release();
        await vm.DetailsLoaded;
        Row(vm, "Settlements").Should().Be("Testburg");
        gated.Loads.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task A_later_selection_wins_over_a_tile_whose_block_is_still_loading()
    {
        var gated = new GatedTileSource(TestWorlds.Loaded.Tiles);
        var vm = TestWorlds.MainWithWorld(session: TestWorlds.Session(gated));
        var marker = vm.Session!.Markers[0];
        vm.Select(new MapPick(MapLevel.Detail, marker.TileId, marker.GlobeCell, vm.Session.RegionCellOfTile(marker.TileId)));
        var pending = vm.DetailsLoaded;

        vm.SelectCell(marker.GlobeCell);
        gated.Release();
        await pending;

        vm.DetailsTitle.Should().Be($"Globe cell {marker.GlobeCell}");
        vm.DetailRows.Should().Contain(r => r.Label == "Cell id");
    }

    [Test]
    public async Task A_missing_tile_block_shows_a_message_instead_of_values()
    {
        var vm = TestWorlds.MainWithWorld(session: TestWorlds.Session(new EmptySource()));
        var marker = vm.Session!.Markers[0];

        vm.Select(new MapPick(MapLevel.Detail, marker.TileId, marker.GlobeCell, 0));
        await vm.DetailsLoaded;

        Row(vm, "Status").Should().Contain("no tile block is stored").And.Contain("regenerate");
    }

    private sealed class EmptySource : ITileBlockSource
    {
        public Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct) => Task.FromResult<TileBlock?>(null);
    }

    // ---- zoom to region, back to globe ----

    [Test]
    public void Double_click_on_a_globe_cell_zooms_to_Region_View_centred_on_it()
    {
        var vm = AtLevel(MapLevel.Globe);
        var map = vm.Map!;
        var c = vm.Session!.Markers[0].GlobeCell;
        var centre = vm.Session.Layers.Geometry.Centre(c);
        var (x, y) = map.View.Project(centre)!.Value;

        vm.Activate(x, y).Should().BeTrue();

        map.Level.Should().Be(MapLevel.Region);
        Dot(map.View.Centre, centre).Should().BeGreaterThan(1 - 1e-9);
        map.Selected!.Value.Id.Should().Be(c);
        vm.LevelName.Should().Be("Region");
        vm.CanGoBack.Should().BeTrue();
    }

    [Test]
    public void Enter_on_a_selected_region_cell_centres_Region_View_on_it()
    {
        var vm = AtLevel(MapLevel.Region);
        var map = vm.Map!;
        vm.Click(map.View.CentreX + 120, map.View.CentreY + 70).Should().BeTrue();
        var r = map.Selected!.Value.Id;

        vm.ZoomToRegion().Should().BeTrue();

        map.Level.Should().Be(MapLevel.Region);
        Dot(map.View.Centre, vm.Session!.Regions.Geometry.Centre(r)).Should().BeGreaterThan(1 - 1e-9);
        map.View.ScalePxPerRad.Should().BeApproximately(vm.Session.World.ScaleForRegionPx(MapViewModel.RegionViewCellPx), 1e-6);
    }

    [Test]
    public void Zoom_to_region_without_a_selection_does_nothing()
    {
        var vm = TestWorlds.MainWithWorld();
        var before = vm.Map!.View;
        vm.ZoomToRegion().Should().BeFalse();
        vm.Map.View.Should().BeSameAs(before);
    }

    [Test]
    public void Back_to_globe_returns_to_Globe_View_keeping_the_centre_and_selection()
    {
        var vm = AtLevel(MapLevel.Detail);
        var map = vm.Map!;
        map.Pan(40, -25);
        vm.Click(map.View.CentreX, map.View.CentreY);
        var selected = map.Selected;
        var centre = map.View.Centre;

        vm.BackToGlobe().Should().BeTrue();

        map.Level.Should().Be(MapLevel.Globe);
        map.View.ScalePxPerRad.Should().Be(map.View.MinScale);
        Dot(map.View.Centre, centre).Should().BeGreaterThan(1 - 1e-12);
        map.Selected.Should().Be(selected);
        vm.CanGoBack.Should().BeFalse();
        vm.BackToGlobe().Should().BeFalse("already at Globe View");
    }

    // ---- wheel, pan, hover ----

    [Test]
    public void Wheel_zoom_passes_the_thresholds_Globe_Region_Detail_and_back()
    {
        var vm = AtLevel(MapLevel.Globe);
        var map = vm.Map!;
        var names = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LevelName)) names.Add(vm.LevelName);
        };
        var levels = new List<MapLevel> { map.Level };
        for (var i = 0; i < 80 && map.Level != MapLevel.Detail; i++)
        {
            map.ZoomBy(1, 300, 260);
            if (map.Level != levels[^1]) levels.Add(map.Level);
        }

        levels.Should().Equal(MapLevel.Globe, MapLevel.Region, MapLevel.Detail);
        names.Should().Equal("Region", "Detail");
        map.View.Flatness.Should().Be(1, "Detail View is flat");

        for (var i = 0; i < 200 && map.Level != MapLevel.Globe; i++) map.ZoomBy(-1, 300, 260);
        map.Level.Should().Be(MapLevel.Globe);
        map.View.Flatness.Should().Be(0, "Globe View is a sphere");
        for (var i = 0; i < 50; i++) map.ZoomBy(-1, 300, 260);
        map.View.ScalePxPerRad.Should().Be(map.View.MinScale, "zoom out stops at the whole globe");
    }

    [Test]
    public void Wheel_zoom_keeps_the_point_under_the_cursor()
    {
        var vm = AtLevel(MapLevel.Region);
        var map = vm.Map!;
        var before = map.View.Unproject(250, 180)!.Value;
        map.ZoomBy(2, 250, 180);
        var after = map.View.Project(before)!.Value;
        after.X.Should().BeApproximately(250, 0.5);
        after.Y.Should().BeApproximately(180, 0.5);
    }

    [Test]
    public void Dragging_pans_the_map_with_the_pointer()
    {
        var vm = AtLevel(MapLevel.Region);
        var map = vm.Map!;
        var p = map.View.Unproject(400, 300)!.Value;
        map.Pan(25, -10);
        var q = map.View.Project(p)!.Value;
        q.X.Should().BeApproximately(425, 0.1);
        q.Y.Should().BeApproximately(290, 0.1);
    }

    [Test]
    public void Hover_tracks_the_item_under_the_pointer()
    {
        var vm = TestWorlds.MainWithWorld();
        var map = vm.Map!;
        map.HoverAt(400, 300);
        map.Hover.Should().Be(map.PickAt(400, 300));
        map.Hover.Should().NotBeNull();
        map.HoverAt(1, 1);
        map.Hover.Should().BeNull();
    }

    [Test]
    public void Redraw_requests_are_raised_on_visual_changes_only()
    {
        var vm = TestWorlds.MainWithWorld();
        var count = 0;
        vm.Map!.Invalidated += (_, _) => count++;
        vm.Map.HoverAt(400, 300);
        var afterHover = count;
        vm.Map.HoverAt(400, 300);
        count.Should().Be(afterHover, "the same hover item does not redraw");
        vm.Map.SetViewport(800, 600);
        count.Should().Be(afterHover, "the same viewport does not redraw");
        vm.Map.Pan(5, 0);
        count.Should().Be(afterHover + 1);
        vm.Map.Rivers = true;
        count.Should().Be(afterHover + 1, "setting a toggle to its value does not redraw");
    }

    [Test]
    public void A_whole_globe_view_stays_whole_when_the_viewport_changes()
    {
        var vm = TestWorlds.MainWithWorld();
        vm.Map!.SetViewport(1200, 900);
        vm.Map.View.ScalePxPerRad.Should().Be(0.45 * 900);
    }

    // ---- layers ----

    [Test]
    public void Layer_picker_and_toggles_change_the_draw_options()
    {
        var vm = TestWorlds.MainWithWorld();
        var map = vm.Map!;
        map.LayerNames.Should().Equal(Enum.GetNames<GlobeLayer>());
        map.LayerNames.Should().Contain(nameof(GlobeLayer.Landform));
        var o = map.DrawOptions;
        new[] { o.Borders, o.Rivers, o.Lakes, o.MountainSymbols, o.Settlements, o.Labels, o.Arrows }.Should().AllBeEquivalentTo(true);
        o.Equator.Should().BeTrue("the equator is on by default");
        o.Graticule.Should().BeFalse("latitude and longitude lines are off by default");
        o.RiverArrows.Should().BeFalse("river arrows are off by default");
        o.RiverEnds.Should().BeFalse("river ends are off by default");

        var redraws = 0;
        map.Invalidated += (_, _) => redraws++;
        map.LayerIndex = (int)GlobeLayer.Landform;
        map.Borders = false;
        map.Rivers = false;
        map.Lakes = false;
        map.MountainSymbols = false;
        map.Settlements = false;
        map.Labels = false;
        map.Arrows = false;
        map.Equator = false;
        map.Graticule = true;
        map.RiverArrows = true;
        map.RiverEnds = true;

        redraws.Should().Be(12);
        o = map.DrawOptions;
        o.Layer.Should().Be(GlobeLayer.Landform);
        new[] { o.Borders, o.Rivers, o.Lakes, o.MountainSymbols, o.Settlements, o.Labels, o.Arrows }.Should().AllBeEquivalentTo(false);
        o.Equator.Should().BeFalse();
        o.Graticule.Should().BeTrue();
        o.RiverArrows.Should().BeTrue();
        o.RiverEnds.Should().BeTrue();
        map.LayerIndex = 99;
        map.Layer.Should().Be(GlobeLayer.Landform, "an out-of-range index is ignored");
    }

    [Test]
    public void Markers_come_from_the_settlements_with_their_tiles()
    {
        var vm = TestWorlds.MainWithWorld();
        var s = vm.Session!.Data.Settlements[0];
        vm.Map!.DrawOptions.Markers.Should().Equal(new MapMarker(s.Cell, s.TileId!.Value, "Testburg"));
        vm.Session.GlobeCellOfTile(s.TileId.Value).Should().Be(s.Cell);
    }

    // ---- status bar ----

    [Test]
    public void Status_bar_names_world_seed_counts_generator_and_view()
    {
        var vm = TestWorlds.MainWithWorld();
        var tiles = TestWorlds.Loaded.TileGrid.Count;
        var size = TileMetrics.SizeKm(TestWorlds.Settings.Grid.PlanetRadiusKm, tiles);
        vm.StatusText.Should().Contain("Testland").And.Contain("seed 42").And.Contain("2,562 globe cells")
            .And.Contain($"{tiles.ToString("N0", Inv)} tiles of {size.ToString("F1", Inv)} km")
            .And.Contain($"generator v{WorldGenerator.GeneratorVersion}").And.EndWith("Globe View");
        vm.Map!.ZoomToLevel(MapLevel.Region);
        vm.StatusText.Should().EndWith("Region View");
        vm.Map.ZoomToLevel(MapLevel.Detail);
        vm.StatusText.Should().EndWith("Detail View");
        vm.LevelName.Should().Be("Detail");
    }

    [Test]
    public void Enter_at_Detail_View_on_a_region_cell_fallback_pick_does_not_zoom_out()
    {
        var vm = AtLevel(MapLevel.Detail);
        var region = vm.Session!.World.RegionTiles!.Nearest(vm.Map!.View.Centre);
        vm.Select(new MapPick(MapLevel.Region, region, vm.Session.Regions.GlobeParent[region], region));
        vm.CanZoomToRegion.Should().BeFalse();
        vm.ZoomToRegion().Should().BeFalse();
        vm.Map.Level.Should().Be(MapLevel.Detail);
    }

    private sealed class FailingSource : ITileBlockSource
    {
        public Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct) => throw new IOException("database is locked");
    }

    [Test]
    public async Task Status_bar_names_failed_tile_blocks_with_the_error()
    {
        MainViewModel.TileBlockStatus(null).Should().BeEmpty();
        var w = TestWorlds.Session().World;
        using var cache = new TileBlockCache(new FailingSource(), w.Tiles!, w.GlobeTiles);
        MainViewModel.TileBlockStatus(cache).Should().BeEmpty("nothing failed yet");
        await cache.EnsureLoadedAsync(0);
        await cache.EnsureLoadedAsync(1);
        MainViewModel.TileBlockStatus(cache).Should().Be(" · 2 tile block(s) could not be loaded, drawn as region cells, retrying: database is locked");
    }

    // ---- areas ----

    [Test]
    public void Selecting_an_area_highlights_its_cells_and_centres_the_globe_on_it()
    {
        var vm = AtLevel(MapLevel.Detail);
        var l = vm.Session!.Layers;
        var area = l.Areas.Where(a => a.Kind != WorldEcon.SharedKernel.Globe.GeographicRegionKind.Ocean).OrderByDescending(a => a.Cells.Count).First();
        var item = vm.Areas.Items.Single(i => i.Index == area.Index);

        vm.Areas.SelectedArea = item;

        vm.Map!.Highlighted.Should().BeEquivalentTo(area.Cells.Select(x => x.Cell));
        vm.Map.DrawOptions.HighlightedGlobeCells.Should().BeSameAs(vm.Map.Highlighted);
        vm.Map.Level.Should().Be(MapLevel.Globe);
        var centre = vm.Session.World.GlobeTiles.Nearest(vm.Map.View.Centre);
        area.Cells.Select(x => x.Cell).Should().Contain(centre, "the view centre is a member cell");
        vm.DetailsTitle.Should().Be($"{area.Kind} '{area.Name}'");

        vm.Areas.SelectedArea = null;
        vm.Map.Highlighted.Should().BeNull();
    }

    [Test]
    public void Areas_filter_by_kind_and_text()
    {
        var vm = TestWorlds.MainWithWorld();
        var areas = vm.Areas;
        areas.Filtered.Should().HaveCount(vm.Session!.Layers.Areas.Count);
        var kind = vm.Session.Layers.Areas[0].Kind;
        areas.KindIndex = areas.KindNames.IndexOf(kind.ToString());
        areas.Filtered.Should().OnlyContain(a => a.Kind == kind).And.NotBeEmpty();
        var name = areas.Filtered[0].Name;
        var part = name[..3];
        areas.SearchText = part.ToUpperInvariant();
        areas.Filtered.Should().OnlyContain(a => a.Name.Contains(part, StringComparison.OrdinalIgnoreCase)).And.Contain(a => a.Name == name);
        areas.KindIndex = 0;
        areas.SearchText = "";
        areas.Filtered.Should().HaveCount(vm.Session.Layers.Areas.Count);
    }

    [Test]
    public void Replacing_the_world_disposes_the_previous_renderer()
    {
        var vm = TestWorlds.MainWithWorld();
        var first = vm.Session!;
        vm.UseSession(TestWorlds.Session());
        // The old session is disposed on a worker thread (UseSession); wait for it before drawing with it.
        SpinWait.SpinUntil(() => first.IsClosed, TimeSpan.FromSeconds(10)).Should().BeTrue();
        using var bmp = new SkiaSharp.SKBitmap(10, 10);
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        var act = () => first.Renderer.Draw(canvas, vm.Map!.View, vm.Map.DrawOptions);
        act.Should().Throw<ObjectDisposedException>();
    }
}
