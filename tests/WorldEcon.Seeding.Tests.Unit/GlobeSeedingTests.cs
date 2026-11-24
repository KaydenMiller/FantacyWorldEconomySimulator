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
    public async Task Site_on_water_moves_to_nearest_land_tile_and_warns()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c));
        // An ocean cell next to land: the nearest land tile is close by.
        var ocean = Enumerable.Range(0, layers.Grid.CellCount).First(c => layers.IsOcean(c) && layers.Grid.Neighbours(c).ToArray().Any(n => !layers.IsOcean(n))
            && layers.Grid.Neighbours(c).ToArray().All(n => layers.IsOcean(n) || n != land));
        var v = layers.Geometry.Centre(ocean);
        double lat = Math.Asin(v.Y) * 180 / Math.PI, lon = Math.Atan2(v.X, v.Z) * 180 / Math.PI;
        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [Bare("Alderford", cell: land)])])
            with { Nodes = [new SeedNode("Site", "Wreck Point", lat, lon)] };

        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            warnings.Should().Contain(w => w.StartsWith("Site 'Wreck Point' is on water", StringComparison.Ordinal) && w.Contains("nearest land tile"));
            await using var ctx = NewContextOnFile(path);
            var site = await ctx.RouteNodes.SingleAsync(n => n.Kind == Domain.Geography.RouteNodeKind.Site);
            site.Name.Should().Be("Wreck Point"); // the import succeeding shows its tile is land: road generation rejects a node on water
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Different_landmasses_warn_and_new_succeeds_with_air_route()
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
            routes: [new SeedRoute("Alderford", "Brightwater", 1), new SeedRoute("Alderford", "Brightwater", 1, "Air")])
            with { Generation = Settings with { Roads = new() { DefaultMaxTier = 7 } } }; // seed generation.Roads values reach generation

        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            warnings.Should().Contain(w => w.Contains("no land path between Alderford and Brightwater; needs a port or an air route"));
            await using var ctx = NewContextOnFile(path);
            var routes = await ctx.Routes.ToListAsync();
            routes.Where(r => r.Category == Domain.Geography.RouteCategory.Air).Should().ContainSingle().Which.MaxTier.Should().Be(7);
            routes.Should().NotContain(r => r.Category == Domain.Geography.RouteCategory.Land);
            (await ctx.Roads.CountAsync()).Should().Be(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Authored_sea_route_fails()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c));
        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [Bare("Alderford", cell: land), Bare("Brightwater", cell: land)])],
            routes: [new SeedRoute("Alderford", "Brightwater", 1, "Sea")]);
        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*sea routes are generated between ports*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Generated_routes_start_and_end_on_node_tiles_and_roads_list_them_in_order()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).Where(c => Pinnable(layers, c)).ToList();
        var near = land.First(c => c != land[0] && RingDistance(layers.Grid, land[0], c, 4) <= 4);

        var seed = MinimalSeed(worldSeed,
            [new SeedRegion("The Reach", [Bare("Alderford", cell: land[0]), Bare("Brightwater", cell: near)])],
            routes: [new SeedRoute("Alderford", "Brightwater", 2, Surface: "Paved", MaxTier: 1)]);

        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            warnings.Should().BeEmpty();
            await using var ctx = NewContextOnFile(path);
            var nodes = (await ctx.RouteNodes.ToListAsync()).ToDictionary(n => n.Id);
            var routes = await ctx.Routes.ToListAsync();
            routes.Should().NotBeEmpty();
            foreach (var r in routes)
            {
                var tiles = await ctx.RouteTiles.Where(t => t.RouteId == r.Id).OrderBy(t => t.Sequence).Select(t => t.TileId).ToListAsync();
                tiles.Should().HaveCountGreaterThanOrEqualTo(2);
                tiles[0].Should().Be(nodes[r.NodeA].TileId);
                tiles[^1].Should().Be(nodes[r.NodeB].TileId);
                r.LengthMetres.Should().BePositive();
                (r.Surface, r.MaxTier, r.Danger).Should().Be((Domain.Geography.RouteSurface.Paved, 1, 2));
            }
            var road = await ctx.Roads.SingleAsync();
            road.Name.Should().Be("Alderford–Brightwater");
            var first = routes.Single(r => r.Id == road.Routes[0].RouteId);
            nodes[first.Start(road.Routes[0].Direction)].Name.Should().Be("Alderford");
            var last = routes.Single(r => r.Id == road.Routes[^1].RouteId);
            nodes[last.End(road.Routes[^1].Direction)].Name.Should().Be("Brightwater");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Pair_listed_twice_without_names_fails()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c));
        var seed = MinimalSeed(worldSeed,
            [new SeedRegion("The Reach", [Bare("Alderford", cell: land), Bare("Brightwater", cell: land)])],
            routes: [new SeedRoute("Alderford", "Brightwater"), new SeedRoute("Brightwater", "Alderford")]);
        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*listed twice*name*");
            await AssertNothingPersistedAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Unknown_vehicle_class_fails_naming_settlement_and_class()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c));
        var town = Bare("Alderford", cell: land) with { Merchants = [new SeedMerchant(1_000, VehicleClass: "Zeppelin")] };
        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [town])]);
        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Alderford*Zeppelin*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Bad_route_category_fails()
    {
        const ulong worldSeed = 42;
        var layers = WorldGenerator.Generate(worldSeed, Settings).Layers;
        var land = Enumerable.Range(0, layers.Grid.CellCount).First(c => Pinnable(layers, c));
        var seed = MinimalSeed(worldSeed, [new SeedRegion("The Reach", [Bare("Alderford", cell: land), Bare("Brightwater", cell: land)])],
            routes: [new SeedRoute("Alderford", "Brightwater", 1, "ShippingLane")]);
        var path = TempDb();
        try
        {
            var act = async () => await ImportAsync(path, seed);
            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ShippingLane*Land, Sea, Air*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Coastal_settlements_get_voyages_and_estimates_without_a_seed_entry()
    {
        const ulong worldSeed = 42;
        var generated = WorldGenerator.Generate(worldSeed, Settings);
        var tiles = generated.Tiles!;
        var terrain = WorldEcon.WorldGen.Roads.RoadTerrain.From(tiles, new WorldEcon.WorldGen.Roads.RoadSettings());
        // Two coastal land tiles whose water connects (a voyage search between them succeeds).
        var coastal = Enumerable.Range(0, tiles.Count).Where(t => terrain.Coastal(t)
            && (WorldEcon.WorldGen.Tiles.TileWaterKind)tiles.WaterKind[t] == WorldEcon.WorldGen.Tiles.TileWaterKind.None
            && Pinnable(generated.Layers, tiles.GlobeParent[t])).ToList();
        int a = coastal[0], b = -1;
        foreach (var t in coastal.Skip(50))
        {
            var v = WorldEcon.WorldGen.Roads.VoyageGenerator.Generate(terrain,
                [new("a", a, "a", WorldEcon.WorldGen.Roads.PortKind.Coastal), new("b", t, "b", WorldEcon.WorldGen.Roads.PortKind.Coastal)]).Value;
            if (v.Count == 1) { b = t; break; }
        }
        b.Should().BeGreaterThanOrEqualTo(0);
        (double Lat, double Lon) LatLon(int t)
        {
            var v = tiles.Grid.Centre(t);
            return (Math.Asin(Math.Clamp(v.Y, -1, 1)) * 180 / Math.PI, Math.Atan2(v.X, v.Z) * 180 / Math.PI);
        }
        var (la, lo) = LatLon(a);
        var (lb, lob) = LatLon(b);

        var seed = MinimalSeed(worldSeed,
            [new SeedRegion("The Reach", [Bare("Portis", lat: la, lon: lo), Bare("Havenmouth", lat: lb, lon: lob)])]);
        var path = TempDb();
        try
        {
            var (_, warnings) = await ImportAsync(path, seed);
            await using var ctx = NewContextOnFile(path);
            var nodes = await ctx.RouteNodes.ToListAsync();
            nodes.Should().OnlyContain(n => n.PortKind == Domain.Geography.PortKind.Coastal, string.Join(" | ", warnings));
            var voyage = (await ctx.Routes.ToListAsync()).Should().ContainSingle().Subject;
            (voyage.Category, voyage.Surface, voyage.MinTier).Should().Be((Domain.Geography.RouteCategory.Sea, Domain.Geography.RouteSurface.OpenWater, 1));
            var stored = (await ctx.VoyagePaths.SingleAsync(v => v.RouteId == voyage.Id)).Decode();
            stored[0].Should().Be(nodes.Single(n => n.Id == voyage.NodeA).TileId);
            stored[^1].Should().Be(nodes.Single(n => n.Id == voyage.NodeB).TileId);
            (await ctx.Roads.CountAsync()).Should().Be(0, "voyages belong to no road");
            // Each port knows the other by every sea class it offers, plus air (offered everywhere) has no route here.
            var known = await ctx.TravelKnowledge.ToListAsync();
            known.Should().Contain(k => k.VehicleClass == "Longboat" && k.Source == Domain.Economy.KnowledgeSource.Estimated);
            known.Select(k => k.KnowerSettlementId).Distinct().Should().HaveCount(2);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
