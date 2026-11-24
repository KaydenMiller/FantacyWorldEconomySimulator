using FluentAssertions;
using WorldEcon.SharedKernel.Globe;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class PinStageTests
{
    // Synthetic world: every cell is temperate grassland land (elevation 100, 150 deci-C, 800 mm) unless changed.
    private static WorldLayers LandWorld(int m, bool ocean = false)
    {
        var grid = GlobeGrid.Build(m);
        var n = grid.CellCount;
        return new WorldLayers(grid, GlobeGeometry.Compute(grid))
        {
            ElevationM = Enumerable.Repeat(ocean ? -100 : 100, n).ToArray(),
            SeaLevelM = 0,
            TemperatureDeciC = Enumerable.Repeat((short)150, n).ToArray(),
            PrecipitationMm = Enumerable.Repeat(800, n).ToArray(),
            BiomeOf = Enumerable.Repeat((byte)(ocean ? Biome.Ocean : Biome.Grassland), n).ToArray(),
            WaterBodyKind = new byte[n],
        };
    }

    private static int Rings(WorldLayers l, int from, int to)
    {
        var dist = new Dictionary<int, int> { [from] = 0 };
        var q = new Queue<int>([from]);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (c == to) return dist[c];
            foreach (var nb in l.Grid.Neighbours(c))
                if (dist.TryAdd(nb, dist[c] + 1)) q.Enqueue(nb);
        }
        return int.MaxValue;
    }

    [Test]
    public void Explicit_lat_lon_maps_to_NearestCell()
    {
        var l = LandWorld(8);
        var r = PinStage.Run(l, [new PinRequest("a", "g", null, 12.5, -40.0)], 1);
        r.IsError.Should().BeFalse();
        r.Value.Single().Should().Be(new PinResult("a", l.Geometry.NearestCell(12.5, -40.0), false));
    }

    [Test]
    public void Explicit_cell_id_is_used()
    {
        var l = LandWorld(4);
        var r = PinStage.Run(l, [new PinRequest("a", "g", 17, null, null)], 1);
        r.Value.Single().Should().Be(new PinResult("a", 17, false));
    }

    [Test]
    public void Explicit_ocean_cell_is_a_pin_ocean_error_naming_the_key()
    {
        var l = LandWorld(4);
        l.ElevationM[17] = -50;
        var r = PinStage.Run(l, [new PinRequest("Port", "g", 17, null, null)], 1);
        r.IsError.Should().BeTrue();
        r.FirstError.Code.Should().Be("pin.ocean");
        r.FirstError.Description.Should().Be("Port is pinned to ocean cell 17");
    }

    [Test]
    public void Explicit_lake_cell_is_a_pin_ocean_error()
    {
        var l = LandWorld(4);
        l.BiomeOf[9] = (byte)Biome.Lake;
        var r = PinStage.Run(l, [new PinRequest("Dock", "g", 9, null, null)], 1);
        r.FirstError.Code.Should().Be("pin.ocean");
        r.FirstError.Description.Should().Be("Dock is pinned to lake cell 9");
    }

    [Test]
    public void Out_of_range_cell_id_is_an_error()
    {
        var l = LandWorld(4);
        var r = PinStage.Run(l, [new PinRequest("a", "g", 99999, null, null)], 1);
        r.FirstError.Code.Should().Be("pin.cell");
    }

    [Test]
    public void Request_without_location_data_for_cell_or_latlon_half_is_auto_placed_on_land()
    {
        var l = LandWorld(8);
        // Only half a coordinate pair counts as no location.
        var r = PinStage.Run(l, [new PinRequest("a", "g", null, 10, null)], 1);
        r.Value.Single().AutoPlaced.Should().BeTrue();
    }

    [Test]
    public void Auto_placed_cell_is_land_and_not_ice_lake_or_dry_lake_bed()
    {
        var l = LandWorld(8);
        // Make every cell but a few invalid; the best cells are otherwise excluded types.
        for (int c = 0; c < l.Grid.CellCount; c++)
        {
            if (c % 3 == 0) l.ElevationM[c] = -10;
            else if (c % 3 == 1) l.BiomeOf[c] = (byte)Biome.Ice;
            else if (c % 7 == 0) l.WaterBodyKind[c] = (byte)WaterBodyKind.DryLakeBed;
            else if (c % 5 == 0) l.WaterBodyKind[c] = (byte)WaterBodyKind.Lake;
        }
        var reqs = Enumerable.Range(0, 20).Select(i => new PinRequest($"k{i}", $"g{i % 4}", null, null, null)).ToList();
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var r = PinStage.Run(l, reqs, seed);
            r.IsError.Should().BeFalse();
            foreach (var p in r.Value)
            {
                p.AutoPlaced.Should().BeTrue();
                l.IsOcean(p.Cell).Should().BeFalse();
                l.BiomeOf[p.Cell].Should().NotBe((byte)Biome.Ice);
                l.WaterBodyKind[p.Cell].Should().NotBe((byte)WaterBodyKind.DryLakeBed);
                l.WaterBodyKind[p.Cell].Should().NotBe((byte)WaterBodyKind.Lake);
            }
        }
    }

    [Test]
    public void First_of_a_group_takes_the_highest_suitability_cell()
    {
        var l = LandWorld(8);
        // Make one cell uniquely best: in range on both axes plus a river; all others lose the temperature point.
        for (int c = 0; c < l.Grid.CellCount; c++) l.TemperatureDeciC[c] = 400;
        l.TemperatureDeciC[123] = 150;
        var r = PinStage.Run(l, [new PinRequest("a", "g", null, null, null)], 3);
        r.Value.Single().Cell.Should().Be(123);
    }

    [Test]
    public void Group_members_are_placed_within_three_rings_of_the_first()
    {
        var l = LandWorld(16);
        var reqs = Enumerable.Range(0, 12).Select(i => new PinRequest($"k{i}", "g", null, null, null)).ToList();
        var r = PinStage.Run(l, reqs, 7);
        r.IsError.Should().BeFalse();
        var first = r.Value[0].Cell;
        foreach (var p in r.Value) Rings(l, first, p.Cell).Should().BeLessThanOrEqualTo(3);
    }

    [Test]
    public void Later_members_take_the_best_scoring_cell_within_three_rings()
    {
        var l = LandWorld(16);
        for (int c = 0; c < l.Grid.CellCount; c++) l.TemperatureDeciC[c] = 400; // 4 points each (precipitation only)
        const int anchor = 300; // explicit anchor of the group, 4 points
        var ring1 = l.Grid.Neighbours(anchor).ToArray();
        var target = l.Grid.Neighbours(ring1[0]).ToArray().First(c => c != anchor && !ring1.Contains(c));
        l.WaterBodyKind[target] = (byte)WaterBodyKind.River; // 6 points: best in range
        var far = Enumerable.Range(0, l.Grid.CellCount).First(c => Rings(l, anchor, c) > 6);
        l.TemperatureDeciC[far] = 150; // 8 points but out of range: the global best
        var r = PinStage.Run(l, [new PinRequest("a", "g", anchor, null, null), new PinRequest("b", "g", null, null, null)], 5);
        r.Value[0].Cell.Should().Be(anchor);
        r.Value[1].Cell.Should().Be(target);
        PinStage.Run(l, [new PinRequest("c", "other", null, null, null)], 5).Value[0].Cell.Should().Be(far);
    }

    [Test]
    public void Two_requests_pinned_to_one_cell_both_succeed()
    {
        var l = LandWorld(4);
        var r = PinStage.Run(l, [new PinRequest("a", "g1", 30, null, null), new PinRequest("b", "g2", 30, null, null)], 1);
        r.IsError.Should().BeFalse();
        r.Value.Select(p => p.Cell).Should().Equal(30, 30);
    }

    [Test]
    public void All_ocean_world_gives_pin_noland_naming_the_first_unpinned_key()
    {
        var l = LandWorld(4, ocean: true);
        var r = PinStage.Run(l, [new PinRequest("Capital", "g", null, null, null), new PinRequest("Town", "g", null, null, null)], 1);
        r.FirstError.Code.Should().Be("pin.noland");
        r.FirstError.Description.Should().Be("No land cell available for Capital");
    }

    [Test]
    public void Different_seeds_can_place_a_group_first_cell_differently_and_same_seed_is_stable()
    {
        var l = LandWorld(8);
        var req = new[] { new PinRequest("a", "g", null, null, null) };
        PinStage.Run(l, req, 11).Value.Should().Equal(PinStage.Run(l, req, 11).Value);
        var cells = Enumerable.Range(1, 12).Select(s => PinStage.Run(l, req, (ulong)s).Value[0].Cell).Distinct().Count();
        cells.Should().BeGreaterThan(1);
    }

    [Test]
    public void Auto_placement_prefers_river_and_coast_adjacency()
    {
        var l = LandWorld(8);
        var coast = 200;
        foreach (var nb in l.Grid.Neighbours(coast)) l.ElevationM[nb] = -10;
        l.ElevationM[coast] = 100;
        var r = PinStage.Run(l, [new PinRequest("a", "g", null, null, null)], 2);
        // The only cell with an ocean neighbour or the cells beside the ocean ring: best cell must have coast or river bonus.
        l.Grid.Neighbours(r.Value[0].Cell).ToArray().Any(nb => l.IsOcean(nb)).Should().BeTrue();
    }

    private static WorldLayers Generated() => WorldGenerator.Generate(7, new WorldGenSettings { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 2 } }).Layers;

    [Test]
    [Skip("Generator version 6 terrain: the fixture world has no basin area; needs a fixture with one")]
    public void Authored_area_name_at_a_point_inside_a_basin_area_renames_it()
    {
        var l = Generated();
        var area = l.Areas.First(a => a.Kind == GeographicRegionKind.Basin || a.Kind == GeographicRegionKind.Plain);
        var (lat, lon) = l.Geometry.LatLon(area.Cells[0].Cell);
        var r = PinStage.NameAreas(l, [new AreaNameRequest("Great Test", area.Kind, lat, lon)]);
        r.IsError.Should().BeFalse();
        r.Value.Single().Should().Be((area.Index, "Great Test"));
    }

    [Test]
    public void Authored_area_name_where_no_area_of_that_kind_exists_is_an_error()
    {
        var l = Generated();
        var ocean = Enumerable.Range(0, l.Grid.CellCount).First(c => l.IsOcean(c));
        var (lat, lon) = l.Geometry.LatLon(ocean);
        var r = PinStage.NameAreas(l, [new AreaNameRequest("Nowhere", GeographicRegionKind.MountainRange, lat, lon)]);
        r.IsError.Should().BeTrue();
        r.FirstError.Code.Should().Be("area.none");
        r.FirstError.Description.Should().Contain("Nowhere");
    }

    [Test]
    public void Pinning_a_generated_world_is_deterministic_and_never_alters_terrain()
    {
        var l = Generated();
        var before = l.ElevationM.ToArray();
        var reqs = new[] { new PinRequest("a", "g", null, null, null), new PinRequest("b", "g", null, null, null), new PinRequest("c", "h", null, null, null) };
        var r1 = PinStage.Run(l, reqs, 99);
        var r2 = PinStage.Run(l, reqs, 99);
        r1.IsError.Should().BeFalse();
        r1.Value.Should().Equal(r2.Value);
        l.ElevationM.Should().Equal(before);
        r1.Value.All(p => !l.IsOcean(p.Cell)).Should().BeTrue();
    }

    [Test]
    public void Three_groups_of_two_on_a_real_world_get_distinct_cells_separated_anchors_and_close_members()
    {
        var l = WorldGenerator.Generate(7, new WorldGenSettings { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 2 } }).Layers;
        var reqs = new List<PinRequest>();
        foreach (var g in new[] { "A", "B", "C" })
            for (int i = 0; i < 2; i++) reqs.Add(new PinRequest($"{g}{i}", g, null, null, null));
        var r = PinStage.Run(l, reqs, 5);
        r.IsError.Should().BeFalse();
        r.Value.Select(p => p.Cell).Distinct().Should().HaveCount(6);
        var anchors = new[] { r.Value[0].Cell, r.Value[2].Cell, r.Value[4].Cell };
        int minPair = int.MaxValue;
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++) minPair = Math.Min(minPair, Rings(l, anchors[i], anchors[j]));
        minPair.Should().BeGreaterThanOrEqualTo(6);
        for (int g = 0; g < 3; g++) Rings(l, anchors[g], r.Value[2 * g + 1].Cell).Should().BeLessThanOrEqualTo(3);
    }

    [Test]
    public void Separation_relaxes_to_the_maximum_feasible_value()
    {
        var l = LandWorld(4); // 162 cells; ring diameter is small, so 6 is infeasible for two groups
        var r = PinStage.Run(l, [new PinRequest("a", "A", null, null, null), new PinRequest("b", "B", null, null, null)], 1, new PinSettings { MinGroupSeparationRings = 20 });
        r.IsError.Should().BeFalse();
        var got = Rings(l, r.Value[0].Cell, r.Value[1].Cell);
        var diameter = Enumerable.Range(0, l.Grid.CellCount).Max(c => Rings(l, r.Value[0].Cell, c));
        got.Should().Be(diameter);
        r.Value[0].Warning.Should().BeNull("the first anchor has no other anchor to be near");
        r.Value[1].Warning.Should().Be($"b (group B): anchor placed {got} rings from another group's anchor; requested 20 (separation relaxed).");
    }

    [Test]
    public void Feasible_separation_gives_no_warning()
    {
        var l = LandWorld(8);
        var r = PinStage.Run(l, [new PinRequest("a", "A", null, null, null), new PinRequest("b", "B", null, null, null), new PinRequest("c", "A", null, null, null)], 1, new PinSettings { MinGroupSeparationRings = 3 });
        r.Value.Should().OnlyContain(p => p.Warning == null);
    }

    [Test]
    public void Two_authored_names_for_one_area_are_an_area_duplicate_error()
    {
        var l = Generated();
        var area = l.Areas.First(a => a.Kind == GeographicRegionKind.Ocean && a.Cells.Count >= 2);
        var (lat1, lon1) = l.Geometry.LatLon(area.Cells[0].Cell);
        var (lat2, lon2) = l.Geometry.LatLon(area.Cells[^1].Cell);
        var r = PinStage.NameAreas(l, [new AreaNameRequest("First", area.Kind, lat1, lon1), new AreaNameRequest("Second", area.Kind, lat2, lon2)]);
        r.IsError.Should().BeTrue();
        r.FirstError.Code.Should().Be("area.duplicate");
        r.FirstError.Description.Should().Contain("Second");
    }

    [Test]
    public void Blank_authored_area_name_is_an_area_name_error()
    {
        var l = Generated();
        var area = l.Areas.First(a => a.Kind == GeographicRegionKind.Ocean);
        var (lat, lon) = l.Geometry.LatLon(area.Cells[0].Cell);
        foreach (var blank in new[] { "", "   " })
        {
            var r = PinStage.NameAreas(l, [new AreaNameRequest(blank, area.Kind, lat, lon)]);
            r.IsError.Should().BeTrue();
            r.FirstError.Code.Should().Be("area.name");
        }
    }

    [Test]
    public void Auto_placement_never_reuses_an_occupied_cell_and_reports_noland_when_full()
    {
        var l = LandWorld(1); // 12 cells
        var reqs = Enumerable.Range(0, 13).Select(i => new PinRequest($"k{i}", "g", null, null, null)).ToList();
        var r = PinStage.Run(l, reqs, 1);
        r.FirstError.Code.Should().Be("pin.noland");
        r.FirstError.Description.Should().Be("No land cell available for k12");
        PinStage.Run(l, reqs.Take(12).ToList(), 1).Value.Select(p => p.Cell).Distinct().Should().HaveCount(12);
    }
}
