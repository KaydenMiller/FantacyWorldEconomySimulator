using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Globe;
using WorldEcon.SharedKernel.Globe;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.Seeding.Tests.Unit;

/// <summary>
/// Globe generation wired into <see cref="SeedImporter"/>: generate, persist, pin, name, warn. Every test uses
/// grid frequency 16 (2,562 cells) for speed; the generator is deterministic, so a test can regenerate the same
/// globe in memory to choose cells (ocean, inland, separate landmasses) before writing the seed.
/// </summary>
public class GlobeSeedingTests
{
    private const int Frequency = 16;
    private static readonly WorldGenSettings Settings = new() { Grid = new() { Frequency = Frequency } };

    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"globeseed_{Guid.NewGuid():N}.db");

    private static string SampleSeedPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "samples", "aerthos.seed.json")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            throw new FileNotFoundException("Could not locate samples/aerthos.seed.json from the test assembly.");
        return Path.Combine(dir, "samples", "aerthos.seed.json");
    }

    private static SeedSettlement Bare(string name, int? cell = null, double? lat = null, double? lon = null)
        => new(name, "Town", 0, 0, 1_000, Shops: [], Market: [], Endowments: [], Production: [], Merchants: [],
            Cell: cell, Lat: lat, Lon: lon);

    private static SeedWorld MinimalSeed(ulong seed, IReadOnlyList<SeedRegion> regions, IReadOnlyList<SeedRoute>? routes = null,
        IReadOnlyList<SeedNaturalArea>? naturalAreas = null)
        => new(Name: "Test", Seed: seed, RulesetVersion: "1.0.0", Goods: [], Recipes: [],
            Continents: [new SeedContinent("Mundus", [new SeedCountry("Highmark", regions)])],
            Routes: routes ?? [],
            Generation: new WorldGenSettings { Grid = new() { Frequency = Frequency } },
            NaturalAreas: naturalAreas);

    private static async Task<(Domain.Geography.WorldId Id, IReadOnlyList<string> Warnings)> ImportAsync(string path, SeedWorld seed)
    {
        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        return await new SeedImporter(ctx).ImportAsync(seed);
    }

    // A failed import must leave nothing behind: no world row, globe rows, natural areas or settlements.
    private static async Task AssertNothingPersistedAsync(string path)
    {
        await using var ctx = NewContextOnFile(path);
        (await ctx.Worlds.CountAsync()).Should().Be(0);
        (await ctx.GlobeCells.CountAsync()).Should().Be(0);
        (await ctx.Set<WorldGenRunRow>().CountAsync()).Should().Be(0);
        (await ctx.GeographicRegions.CountAsync()).Should().Be(0);
        (await ctx.GeographicRegionCells.CountAsync()).Should().Be(0);
        (await ctx.Settlements.CountAsync()).Should().Be(0);
        (await ctx.TileBlocks.CountAsync()).Should().Be(0);
        (await ctx.Lakes.CountAsync()).Should().Be(0);
        (await ctx.RegionLevels.CountAsync()).Should().Be(0);
    }

    private static int RingDistance(GlobeGrid grid, int from, int to, int maxDepth)
    {
        var depth = new Dictionary<int, int> { [from] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (c == to) return depth[c];
            if (depth[c] == maxDepth) continue;
            foreach (var nb in grid.Neighbours(c))
                if (depth.TryAdd(nb, depth[c] + 1)) queue.Enqueue(nb);
        }
        return int.MaxValue;
    }

    // Land components in id order, computed independently of GlobeSeeding.
    private static int[] LandComponents(WorldLayers l)
    {
        var comp = new int[l.Grid.CellCount];
        Array.Fill(comp, -1);
        var next = 0;
        for (var start = 0; start < comp.Length; start++)
        {
            if (l.IsOcean(start) || comp[start] >= 0) continue;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            comp[start] = next;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var nb in l.Grid.Neighbours(c))
                {
                    if (l.IsOcean(nb) || comp[nb] >= 0) continue;
                    comp[nb] = next;
                    queue.Enqueue(nb);
                }
            }
            next++;
        }
        return comp;
    }

    private static bool Pinnable(WorldLayers l, int c) => !l.IsOcean(c) && l.BiomeOf[c] != (byte)Biome.Lake;

    private static bool HasOceanNeighbour(WorldLayers l, int c)
    {
        foreach (var nb in l.Grid.Neighbours(c))
            if (l.IsOcean(nb)) return true;
        return false;
    }

    [Test]
    public async Task Import_Sample_PersistsGlobe_AndPinsEverySettlementOnLand()
    {
        var path = TempDb();
        try
        {
            var seed = await new JsonSeedSource(SampleSeedPath()).LoadAsync();
            seed = seed with { Generation = new WorldGenSettings { Grid = new() { Frequency = Frequency } } };

            var (worldId, _) = await ImportAsync(path, seed);

            await using var ctx = NewContextOnFile(path);
            (await ctx.GlobeCells.CountAsync(c => c.WorldId == worldId)).Should().Be(2_562);

            var world = await ctx.Worlds.SingleAsync();
            world.GridFrequency.Should().Be(Frequency);
            world.WorldGenVersion.Should().Be(WorldGenerator.GeneratorVersion);

            var layers = await new GlobeStore(ctx).LoadLayersAsync(worldId);
            var settlements = await ctx.Settlements.ToListAsync();
            settlements.Should().HaveCount(6);
            foreach (var s in settlements)
            {
                s.CellId.Should().NotBeNull($"{s.Name} must be pinned");
                layers.IsOcean(s.CellId!.Value.Value).Should().BeFalse($"{s.Name} must be on land");
            }

            // Each settlement is pinned to a land tile inside its globe cell (TileId from PinStage).
            var g = Settings.Grid;
            var tileGrid = new TileGrid(g.Frequency * g.RegionMultiplier * g.TileMultiplier);
            var globeTiles = new TileGrid(g.Frequency);
            var store = new GlobeStore(ctx);
            foreach (var s in settlements)
            {
                s.TileId.Should().NotBeNull($"{s.Name} must be pinned to a tile");
                var tile = s.TileId!.Value;
                GridHierarchy.Parent(tileGrid, globeTiles, tile).Should().Be(s.CellId!.Value.Value, $"{s.Name}'s tile lies in its cell");
                var block = (await store.LoadTileBlockAsync(s.CellId!.Value.Value))!;
                var water = (WorldGen.Tiles.TileWaterKind)block.Water[Array.BinarySearch(block.TileIds, tile)];
                water.Should().NotBe(WorldGen.Tiles.TileWaterKind.Ocean).And.NotBe(WorldGen.Tiles.TileWaterKind.Lake);
            }

            settlements.Select(s => s.TileId).Distinct().Should().HaveCount(6);

            var hammerfell = settlements.Single(s => s.Name == "Hammerfell").CellId!.Value.Value;
            var greenfield = settlements.Single(s => s.Name == "Greenfield").CellId!.Value.Value;
            RingDistance(layers.Grid, hammerfell, greenfield, 3).Should().BeLessThanOrEqualTo(3);

            // Auto-placed settlements never share a cell, so settlements of different political regions never do either.
            settlements.Select(s => s.CellId).Distinct().Should().HaveCount(6);
            settlements.GroupBy(s => s.CellId).Should().OnlyContain(g => g.Select(s => s.PoliticalRegionId).Distinct().Count() == 1);

            // Every generated area is stored with its member cells.
            var generated = WorldGenerator.Generate(seed.Seed, Settings).Layers.Areas;
            (await ctx.GeographicRegions.CountAsync(r => r.WorldId == worldId)).Should().Be(generated.Count);
            (await ctx.GeographicRegionCells.CountAsync()).Should().Be(generated.Sum(a => a.Cells.Count));
            var first = generated[0];
            var stored = await ctx.GeographicRegions.SingleAsync(r => r.Name == first.Name && r.Kind == first.Kind);
            stored.NameAuthored.Should().BeFalse();
            var storedCells = await ctx.GeographicRegionCells.Where(c => c.GeographicRegionId == stored.Id).ToListAsync();
            storedCells.Select(c => (c.Cell.Value, c.CoverageBp)).Should().BeEquivalentTo(first.Cells.Select(c => (c.Cell, c.CoverageBp)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_ReportsEveryGenerationStageThenEveryWriteStep_InOrder_WithRisingFractions()
    {
        var path = TempDb();
        try
        {
            var seed = MinimalSeed(42, [new SeedRegion("The Reach", [Bare("Hammerfell")])]);
            var reports = new List<SeedingProgress>();
            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                await new SeedImporter(ctx).ImportWithReportAsync(seed, new ListProgress(reports));
            }

            var steps = reports.Select(r => r.Step).Distinct().ToList();
            var generation = new[]
            {
                WorldGenStage.Plates, WorldGenStage.Elevation, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds,
                WorldGenStage.Climate, WorldGenStage.TileGrid, WorldGenStage.TileElevation, WorldGenStage.TileSeaLevel,
                WorldGenStage.TileClimate, WorldGenStage.TileHydrology, WorldGenStage.TileLandform, WorldGenStage.Summary, WorldGenStage.Areas,
            }.Select(s => s.ToString());
            steps.Should().Equal(generation.Concat([GlobeSeeding.StepGlobeTables, GlobeSeeding.StepTileBlocks, GlobeSeeding.StepLakes,
                GlobeSeeding.StepRegionLevel, GlobeSeeding.StepAreasAndPins]));
            reports.Select(r => r.Fraction).Should().BeInAscendingOrder().And.OnlyContain(f => f >= 0 && f < 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ListProgress(List<SeedingProgress> into) : IProgress<SeedingProgress>
    {
        public void Report(SeedingProgress value) => into.Add(value);
    }

    [Test]
    public async Task Import_Sample_AdvancesOneMonth()
    {
        var path = TempDb();
        try
        {
            var seed = await new JsonSeedSource(SampleSeedPath()).LoadAsync();
            seed = seed with { Generation = new WorldGenSettings { Grid = new() { Frequency = Frequency } } };
            var (worldId, _) = await ImportAsync(path, seed);

            await using var ctx = NewContextOnFile(path);
            var sim = await SimulationContext.LoadAsync(ctx, worldId);
            var engine = new TickEngine(StandardPhases.All());
            await engine.AdvanceAsync(sim, 43_200);
            sim.World.CurrentTick.Value.Should().Be(43_200);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_Throws_NamingSettlement_WhenLatLonIsOnOcean()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var ocean = Enumerable.Range(0, layers.Grid.CellCount).First(layers.IsOcean);
        var (lat, lon) = layers.Geometry.LatLon(ocean);

        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [Bare("Hammerfell", lat: lat, lon: lon)])]);

        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Hammerfell*ocean*");
            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_PinsExplicitCell()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).Last(c => Pinnable(layers, c));

        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [Bare("Hammerfell", cell: land)])]);

        var path = TempDb();
        try
        {
            await ImportAsync(path, seed);
            await using var ctx = NewContextOnFile(path);
            (await ctx.Settlements.SingleAsync()).CellId.Should().Be(new GlobeCellId(land));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_RenamesArea_FromNaturalAreasEntry()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var area = layers.Areas[0];
        var cell = area.Cells[0].Cell;
        var (lat, lon) = layers.Geometry.LatLon(cell);

        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [Bare("Hammerfell")])],
            naturalAreas: [new SeedNaturalArea("The Authored Expanse", area.Kind.ToString().ToLowerInvariant(), lat, lon)]);

        var path = TempDb();
        try
        {
            var (worldId, _) = await ImportAsync(path, seed);
            await using var ctx = NewContextOnFile(path);
            var named = await ctx.GeographicRegions.SingleAsync(r => r.WorldId == worldId && r.Name == "The Authored Expanse");
            named.Kind.Should().Be(area.Kind);
            named.NameAuthored.Should().BeTrue();
            (await ctx.GeographicRegionCells.AnyAsync(c => c.GeographicRegionId == named.Id && c.Cell == new GlobeCellId(cell)))
                .Should().BeTrue();
            (await ctx.GeographicRegions.CountAsync(r => r.NameAuthored)).Should().Be(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_Throws_WhenNaturalAreaPointHasNoAreaOfThatKind()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        // An ocean cell lies in no Desert area.
        var ocean = Enumerable.Range(0, layers.Grid.CellCount).First(layers.IsOcean);
        var (lat, lon) = layers.Geometry.LatLon(ocean);

        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [Bare("Hammerfell")])],
            naturalAreas: [new SeedNaturalArea("Sands of Nowhere", "Desert", lat, lon)]);

        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Sands of Nowhere*");
            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_Throws_WhenNaturalAreaKindIsUnknown()
    {
        var seed = MinimalSeed(42, [new SeedRegion("The Reach", [Bare("Hammerfell")])],
            naturalAreas: [new SeedNaturalArea("Somewhere", "Volcano", 0, 0)]);

        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Volcano*");
            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task Import_Throws_WhenNaturalAreaNameIsBlank_AndPersistsNothing(string? name)
    {
        var seed = MinimalSeed(42, [new SeedRegion("The Reach", [Bare("Hammerfell")])],
            naturalAreas: [new SeedNaturalArea(name!, "Ocean", 0, 0)]);

        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Seed invalid: naturalArea name blank*");
            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_Throws_WhenAGenerationSettingIsOutOfRange_AndPersistsNothing()
    {
        var seed = MinimalSeed(42, [new SeedRegion("The Reach", [Bare("Hammerfell")])]) with
        {
            Generation = new WorldGenSettings { Grid = new() { Frequency = Frequency, PlanetRadiusKm = 0 }, Areas = new() { MinAreaCells = 0 } },
        };

        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Seed invalid: generation*PlanetRadiusKm*MinAreaCells*");
            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_PersistsNothing_WhenAWriteFailsAfterTheGlobeTables()
    {
        var seed = MinimalSeed(42, [new SeedRegion("The Reach", [Bare("Hammerfell")])]);
        var path = TempDb();
        try
        {
            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                // Settlement pins are the last write of globe seeding, after the globe tables and natural areas.
                await ctx.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER fail_pin BEFORE UPDATE OF CellId ON settlements BEGIN SELECT RAISE(ABORT, 'injected pin failure'); END;");
                var act = async () => await new SeedImporter(ctx).ImportAsync(seed);
                (await act.Should().ThrowAsync<DbUpdateException>())
                    .WithInnerException<Microsoft.Data.Sqlite.SqliteException>().WithMessage("*injected pin failure*");
            }

            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task RunAsync_WithoutACallerTransaction_IsAtomicOnItsOwn()
    {
        var path = TempDb();
        try
        {
            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                await ctx.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER fail_pin BEFORE UPDATE OF CellId ON settlements BEGIN SELECT RAISE(ABORT, 'injected pin failure'); END;");
                var world = Domain.Geography.World.Create("W", 42UL, SharedKernel.Calendar.CalendarDefinition.Default, "1.0.0").Value;
                ctx.Worlds.Add(world);
                var continent = Domain.Geography.Continent.Create(world.Id, "C").Value;
                var country = Domain.Geography.Country.Create(world.Id, continent.Id, "K").Value;
                var region = Domain.Geography.PoliticalRegion.Create(world.Id, "R", country.Id).Value;
                var town = Domain.Geography.Settlement.Create(world.Id, region.Id, "Town", Domain.Geography.SettlementType.Town, 0, 0, 100).Value;
                ctx.AddRange(continent, country, region, town);
                var act = async () => await GlobeSeeding.RunAsync(ctx, world, Settings, [(town, new PinRequest("Town", "R", null, null, null))], []);
                (await act.Should().ThrowAsync<DbUpdateException>())
                    .WithInnerException<Microsoft.Data.Sqlite.SqliteException>().WithMessage("*injected pin failure*");
            }

            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_Warns_WhenGroupSeparationIsRelaxed()
    {
        var seed = MinimalSeed(42, [new SeedRegion("The Reach", [Bare("Hammerfell")]), new SeedRegion("Ironhills", [Bare("Karak")])]) with
        {
            Generation = new WorldGenSettings { Grid = new() { Frequency = Frequency }, Pinning = new() { MinGroupSeparationRings = 1000 } },
        };

        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            warnings.Should().ContainSingle().Which.Should().Contain("Karak").And.Contain("separation relaxed").And.Contain("requested 1000");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_WarnsOnce_WhenLandRouteCrossesLandmasses()
    {
        // Find a seed whose frequency-16 globe has at least two landmasses with pinnable cells.
        ulong worldSeed = 0;
        int a = -1, b = -1;
        for (ulong s = 1; s <= 50 && a < 0; s++)
        {
            var l = WorldGenerator.Generate(s, Settings).Layers;
            var comp = LandComponents(l);
            var firstOf = new Dictionary<int, int>();
            for (var c = 0; c < comp.Length; c++)
                if (comp[c] >= 0 && Pinnable(l, c)) firstOf.TryAdd(comp[c], c);
            if (firstOf.Count < 2) continue;
            worldSeed = s;
            a = firstOf.Values.Min();
            b = firstOf.Values.Where(c => comp[c] != comp[a]).Min();
        }
        a.Should().BeGreaterThanOrEqualTo(0, "some seed in 1–50 must have two landmasses");

        var seed = MinimalSeed(worldSeed,
            [new SeedRegion("The Reach", [Bare("Alderford", cell: a), Bare("Brightwater", cell: b)])],
            routes: [new SeedRoute("Alderford", "Brightwater", 100, "Plains", 1, "Land")]);

        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            warnings.Should().ContainSingle();
            warnings[0].Should().Contain("Alderford").And.Contain("Brightwater");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_WarnsOnce_WhenShippingLaneEndIsInland()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var inland = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c) && !HasOceanNeighbour(layers, c));
        var coastal = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c) && HasOceanNeighbour(layers, c));

        var seed = MinimalSeed(worldSeed,
            [new SeedRegion("The Reach", [Bare("Inlandia", cell: inland), Bare("Harbourton", cell: coastal)])],
            routes:
            [
                new SeedRoute("Harbourton", "Inlandia", 100, "Sea", 1, "ShippingLane"),
                new SeedRoute("Inlandia", "Harbourton", 100, "Sea", 1, "ShippingLane"),
            ]);

        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            warnings.Should().ContainSingle();
            warnings[0].Should().Contain("Inlandia");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Import_GivesNoWarnings_WhenRoutesMatchTerrain()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c));

        var seed = MinimalSeed(worldSeed,
            [new SeedRegion("The Reach", [Bare("Alderford", cell: land), Bare("Brightwater", cell: land)])],
            routes: [new SeedRoute("Alderford", "Brightwater", 100, "Plains", 1, "Land")]);

        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            warnings.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
