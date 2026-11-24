using FluentAssertions;
using WorldEcon.SharedKernel.Globe;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Naming;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class AreaStageTests
{
    // All-ocean layers (sea level 0, elevation -100) with Stage 7 arrays set by hand.
    private static WorldLayers OceanWorld(int m)
    {
        var grid = GlobeGrid.Build(m);
        var n = grid.CellCount;
        return new WorldLayers(grid, GlobeGeometry.Compute(grid))
        {
            ElevationM = Enumerable.Repeat(-100, n).ToArray(),
            SeaLevelM = 0,
            BiomeOf = Enumerable.Repeat((byte)Biome.Ocean, n).ToArray(),
            Composition = Enumerable.Range(0, n).Select(_ => new[] { new CompositionEntry(Landform.Ocean, 10_000) }).ToArray(),
        };
    }

    private static void SetLand(WorldLayers l, int cell, Biome biome, params CompositionEntry[] comp)
    {
        l.ElevationM[cell] = 500;
        l.BiomeOf[cell] = (byte)biome;
        l.Composition[cell] = comp;
    }

    private static bool Connected(WorldLayers l, GeneratedArea a)
    {
        var members = a.Cells.Select(c => c.Cell).ToHashSet();
        var seen = new HashSet<int> { a.Cells[0].Cell };
        var queue = new Queue<int>(seen);
        while (queue.Count > 0)
            foreach (var nb in l.Grid.Neighbours(queue.Dequeue()))
                if (members.Contains(nb) && seen.Add(nb)) queue.Enqueue(nb);
        return seen.Count == members.Count;
    }

    [Test]
    public void Wasatch_fixture_cell_is_in_both_a_mountain_range_and_a_basin_area()
    {
        var l = OceanWorld(4);
        var c0 = 20;
        var ring = l.Grid.Neighbours(c0).ToArray();
        SetLand(l, c0, Biome.Mountain,
            new CompositionEntry(Landform.Mountain, 4000), new CompositionEntry(Landform.Basin, 4000), new CompositionEntry(Landform.Plain, 2000));
        foreach (var nb in ring.Take(2))
            SetLand(l, nb, Biome.Mountain,
                new CompositionEntry(Landform.Mountain, 4000), new CompositionEntry(Landform.Basin, 4000), new CompositionEntry(Landform.Plain, 2000));
        foreach (var nb in ring.Skip(2))
            SetLand(l, nb, Biome.Mountain, new CompositionEntry(Landform.Mountain, 6000), new CompositionEntry(Landform.Plain, 4000));

        AreaStage.Run(l, new WorldGenSettings { Grid = new() { Frequency = 4 } }, 7);

        var mountain = l.Areas.Single(a => a.Kind == GeographicRegionKind.MountainRange);
        var basin = l.Areas.Single(a => a.Kind == GeographicRegionKind.Basin);
        mountain.Cells.Should().Contain(x => x.Cell == c0 && x.CoverageBp == 4000);
        basin.Cells.Should().Contain(x => x.Cell == c0 && x.CoverageBp == 4000);
        basin.Cells.Select(x => x.Cell).Should().BeEquivalentTo(new[] { c0, ring[0], ring[1] });
    }

    [Test]
    public void Landform_membership_requires_a_dominant_landform_and_Plain_only_as_the_largest_entry()
    {
        var l = OceanWorld(4);
        var c0 = 20;
        var ring = l.Grid.Neighbours(c0).ToArray();
        // Three connected rim cells: Mountain largest, DryLakeBed secondary at 3,333 bp or more, Plain secondary.
        foreach (var c in new[] { c0, ring[0], ring[1] })
            SetLand(l, c, Biome.Mountain,
                new CompositionEntry(Landform.Mountain, 5000), new CompositionEntry(Landform.DryLakeBed, 3333), new CompositionEntry(Landform.Plain, 1667));
        // Three connected cells with Hills 6000 and Plain 4000: Plain 4,000 bp is above the threshold but not dominant.
        var far = 120;
        var farRing = l.Grid.Neighbours(far).ToArray();
        foreach (var c in new[] { far, farRing[0], farRing[1] })
            SetLand(l, c, Biome.Hills, new CompositionEntry(Landform.Hills, 6000), new CompositionEntry(Landform.Plain, 4000));
        // Hills 5000 / Plain 5000 tie: the first entry (Hills) is the largest, so Plain is not dominant either.
        SetLand(l, farRing[2], Biome.Hills, new CompositionEntry(Landform.Hills, 5000), new CompositionEntry(Landform.Plain, 5000));
        // DryLakeBed 2500 (above the 2,000 bp threshold, below AreaSettings.DominantShareBp, not largest): not a member.
        SetLand(l, ring[2], Biome.Mountain, new CompositionEntry(Landform.Mountain, 7500), new CompositionEntry(Landform.DryLakeBed, 2500));

        AreaStage.Run(l, new WorldGenSettings { Grid = new() { Frequency = 4 } }, 7);

        l.Areas.Should().NotContain(a => a.Kind == GeographicRegionKind.Plain);
        l.Areas.Single(a => a.Kind == GeographicRegionKind.DryLakeBed).Cells.Select(x => x.Cell)
            .Should().BeEquivalentTo(new[] { c0, ring[0], ring[1] });
        l.Areas.Where(a => a.Kind == GeographicRegionKind.MountainRange).SelectMany(a => a.Cells.Select(x => x.Cell))
            .Should().Contain(new[] { c0, ring[0], ring[1], ring[2] });
        l.Areas.Single(a => a.Kind == GeographicRegionKind.Hills).Cells.Select(x => x.Cell)
            .Should().BeEquivalentTo(new[] { far, farRing[0], farRing[1], farRing[2] });
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public void Areas_are_connected_named_and_every_ocean_cell_is_in_exactly_one_ocean_or_sea(int m)
    {
        var l = OceanWorld(m);
        // One land cell so a single ocean component remains around it.
        SetLand(l, 0, Biome.Grassland, new CompositionEntry(Landform.Plain, 10_000));
        AreaStage.Run(l, new WorldGenSettings { Grid = new() { Frequency = m } }, 99);

        foreach (var a in l.Areas)
        {
            a.Name.Should().NotBeNullOrWhiteSpace();
            Connected(l, a).Should().BeTrue();
        }

        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var count = l.Areas.Count(a => a.Kind is GeographicRegionKind.Ocean or GeographicRegionKind.Sea && a.Cells.Any(x => x.Cell == c));
            count.Should().Be(l.IsOcean(c) ? 1 : 0);
        }

        l.Areas.Select((a, i) => a.Index == i).Should().AllBeEquivalentTo(true);
    }

    [Test]
    public void Small_ocean_is_a_Sea_and_large_is_an_Ocean()
    {
        var l = OceanWorld(4);
        // Land everywhere except a 3-cell pocket.
        var pocket = new[] { 30, l.Grid.Neighbours(30)[0], l.Grid.Neighbours(30)[1] };
        for (var c = 0; c < l.Grid.CellCount; c++)
            if (!pocket.Contains(c))
                SetLand(l, c, Biome.Grassland, new CompositionEntry(Landform.Plain, 10_000));
        AreaStage.Run(l, new WorldGenSettings { Grid = new() { Frequency = 4 }, Areas = new() { SeaMaxCells = 10 } }, 1);
        l.Areas.Should().ContainSingle(a => a.Kind == GeographicRegionKind.Sea).Which.Cells.Should().HaveCount(3);
        l.Areas.Should().NotContain(a => a.Kind == GeographicRegionKind.Ocean);
        l.Areas.Single(a => a.Kind == GeographicRegionKind.Grassland).Cells.Should().OnlyContain(x => x.CoverageBp == 10_000);
    }

    [Test]
    public void Names_are_deterministic_and_non_empty()
    {
        foreach (var kind in Enum.GetValues<GeographicRegionKind>())
        {
            var a = AreaNameGenerator.Name(kind, 12345);
            a.Should().NotBeNullOrWhiteSpace();
            AreaNameGenerator.Name(kind, 12345).Should().Be(a);
        }

        AreaNameGenerator.Name(GeographicRegionKind.MountainRange, 1).Should().EndWith("Mountains");
        AreaNameGenerator.Name(GeographicRegionKind.Lake, 1).Should().StartWith("Lake ");
    }

    [Test]
    public void Run_is_deterministic_and_seed_sensitive()
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = 8 } };
        var a = WorldGenerator.Generate(5, settings);
        var b = WorldGenerator.Generate(5, settings);
        a.Stages.Single(s => s.Stage == WorldGenStage.Areas).Hash.Should().Be(b.Stages.Single(s => s.Stage == WorldGenStage.Areas).Hash);
        a.Layers.Areas.Select(x => x.Name).Should().Equal(b.Layers.Areas.Select(x => x.Name));
        var c = WorldGenerator.Generate(6, settings);
        c.Stages.Single(s => s.Stage == WorldGenStage.Areas).Hash.Should().NotBe(a.Stages.Single(s => s.Stage == WorldGenStage.Areas).Hash);
    }

    [Test]
    [Category("Slow")]
    public void Full_pipeline_at_m64_has_areas_and_Wasatch_membership()
    {
        var world = WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { RegionMultiplier = 3, TileMultiplier = 2 } });
        var l = world.Layers;
        foreach (var s in world.Stages) Console.WriteLine($"Stage {s.Stage}: {s.Duration.TotalMilliseconds:F0} ms");
        var total = world.Stages.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);
        Console.WriteLine($"Total: {total.TotalMilliseconds:F0} ms");
        foreach (var g in l.Areas.GroupBy(a => a.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"Areas {g.Key}: {g.Count()}");

        world.Stages.Should().HaveCount(13);

        var mountainCells = l.Areas.Where(a => a.Kind == GeographicRegionKind.MountainRange).SelectMany(a => a.Cells.Select(c => c.Cell)).ToHashSet();
        var lowCells = l.Areas.Where(a => a.Kind is GeographicRegionKind.Basin or GeographicRegionKind.DryLakeBed or GeographicRegionKind.Lake)
            .SelectMany(a => a.Cells.Select(c => c.Cell)).ToHashSet();
        // Version 4: compositions are tile counts. A Lake or DryLakeBed share that makes a cell a member of a water area
        // (≥ DominantShareBp 3,333 or largest) also gives the cell that water body kind (tiles ≥ 1/3), so a non-floor rim
        // cell cannot qualify; the Wasatch case is a cell in both a MountainRange area and a Basin, DryLakeBed or Lake area.
        var both = mountainCells.Intersect(lowCells).Count();
        var rim = mountainCells.Intersect(lowCells)
            .Count(c => l.WaterBodyKind[c] is not ((byte)WaterBodyKind.Lake or (byte)WaterBodyKind.DryLakeBed));
        Console.WriteLine($"Cells in MountainRange and Basin/DryLakeBed/Lake areas: {both} (non-floor: {rim})");
        both.Should().BeGreaterThanOrEqualTo(1);
        var land = Enumerable.Range(0, l.Grid.CellCount).Count(c => !l.IsOcean(c));
        var largestPlain = l.Areas.Where(a => a.Kind == GeographicRegionKind.Plain).Max(a => a.Cells.Count);
        Console.WriteLine($"Largest Plain area: {largestPlain} of {land} land cells");
        largestPlain.Should().BeLessThan(land / 2);
        l.Areas.Should().OnlyContain(a => a.Name.Length > 0);
        l.Areas.Select(a => a.Name).Should().OnlyHaveUniqueItems();
    }
}
