using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Globe;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Persistence.Tests.Unit;

public class GlobeStoreTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"we_globe_{Guid.NewGuid():N}.db");

    // Creates the world row with the globe parameters and sea level the generation produced, as the
    // seeding step does before calling GlobeStore.WriteAsync.
    private static async Task<World> SeedWorldAsync(string path, WorldGenSettings settings, GeneratedWorld generated)
    {
        var world = World.Create("Globe", 7UL, CalendarDefinition.Default, "1.0.0").Value;
        world.SetGlobe(settings.Grid.Frequency, settings.Grid.PlanetRadiusKm, generated.GeneratorVersion).IsError.Should().BeFalse();
        world.SetSeaLevel(generated.Layers.SeaLevelM);
        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        await ctx.SaveChangesAsync();
        return world;
    }

    private static ulong CompositionHash(CompositionEntry[][] composition)
    {
        var flat = new List<short>();
        foreach (var cell in composition)
        {
            flat.Add((short)cell.Length);
            foreach (var e in cell)
            {
                flat.Add((short)e.Landform);
                flat.Add(e.ShareBp);
            }
        }

        return LayerHash.Of(flat.ToArray());
    }

    [Test]
    public async Task WriteThenLoad_AtM4Seed7_EveryLayerMatches()
    {
        var path = TempDb();
        try
        {
            var settings = new WorldGenSettings { Grid = new() { Frequency = 4 } };
            var generated = WorldGenerator.Generate(7UL, settings);
            var g = generated.Layers;
            var world = await SeedWorldAsync(path, settings, generated);

            await using (var ctx = NewContextOnFile(path))
            {
                var elapsed = await new GlobeStore(ctx).WriteAsync(world.Id, settings, generated);
                elapsed.Should().BePositive();
            }

            WorldLayers l;
            await using (var ctx = NewContextOnFile(path))
                l = (await new GlobeStore(ctx).LoadAsync(world.Id)).Layers;

            l.Grid.Frequency.Should().Be(4);
            l.Grid.CellCount.Should().Be(g.Grid.CellCount);
            l.SeaLevelM.Should().Be(g.SeaLevelM);
            LayerHash.Of(l.PlateOf).Should().Be(LayerHash.Of(g.PlateOf));
            l.Plates.Should().Equal(g.Plates);
            LayerHash.Of(l.ElevationM).Should().Be(LayerHash.Of(g.ElevationM));
            LayerHash.Of(l.BoundaryKind).Should().Be(LayerHash.Of(g.BoundaryKind));
            LayerHash.Of(l.CurrentDir.Select(v => (short)v).ToArray()).Should().Be(LayerHash.Of(g.CurrentDir.Select(v => (short)v).ToArray()));
            LayerHash.Of(l.CurrentAnomalyDeciC).Should().Be(LayerHash.Of(g.CurrentAnomalyDeciC));
            LayerHash.Of(l.WindDir.Select(v => (short)v).ToArray()).Should().Be(LayerHash.Of(g.WindDir.Select(v => (short)v).ToArray()));
            LayerHash.Of(l.WindStrength).Should().Be(LayerHash.Of(g.WindStrength));
            LayerHash.Of(l.TemperatureDeciC).Should().Be(LayerHash.Of(g.TemperatureDeciC));
            LayerHash.Of(l.PrecipitationMm).Should().Be(LayerHash.Of(g.PrecipitationMm));
            LayerHash.Of(l.BiomeOf).Should().Be(LayerHash.Of(g.BiomeOf));
            CompositionHash(l.Composition).Should().Be(CompositionHash(g.Composition));
            LayerHash.Of(l.DownstreamOf).Should().Be(LayerHash.Of(g.DownstreamOf));
            LayerHash.Of(l.FlowAccum).Should().Be(LayerHash.Of(g.FlowAccum));
            LayerHash.Of(l.WaterBodyKind).Should().Be(LayerHash.Of(g.WaterBodyKind));
            l.RiverEdges.Should().Equal(g.RiverEdges);
            g.RiverEdges.Should().NotBeEmpty("the fixture must exercise river rows");
            l.Areas.Should().BeEmpty("GlobeStore does not persist areas (seeding writes them as geographic regions)");
            l.Crust.Should().NotBeNull("the geological record is stored in the geology table");
            l.Crust!.Hash().Should().Be(g.Crust!.Hash());
            l.CollisionStrengthPermille.Should().Equal(g.CollisionStrengthPermille);
            l.BoundaryDistanceCells.Should().Equal(g.BoundaryDistanceCells);

            // Hashes recomputed on the loaded layers: the stages whose layers are stored as generated equal their stage
            // hashes; the elevation, sea level, biome and hydrology layers are the tile summaries, so they are compared
            // with the summary hash of the generated layers (Areas excluded: not loaded).
            var recomputed = new Dictionary<WorldGenStage, ulong>
            {
                [WorldGenStage.Currents] = CurrentStage.Hash(l),
                [WorldGenStage.Winds] = WindStage.Hash(l),
                [WorldGenStage.Climate] = ClimateStage.Hash(l),
            };
            foreach (var (stage, hash) in recomputed)
                hash.Should().Be(generated.Stages.Single(s => s.Stage == stage).Hash, $"stage {stage}");
            TileSummaryStage.GlobeHash(l).Should().Be(TileSummaryStage.GlobeHash(g));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Write_AtM4_NeighbourRowsEqualGridNeighbours_AndRunIsRecorded()
    {
        var path = TempDb();
        try
        {
            var settings = new WorldGenSettings { Grid = new() { Frequency = 4 } };
            var generated = WorldGenerator.Generate(7UL, settings);
            var grid = generated.Layers.Grid;
            var world = await SeedWorldAsync(path, settings, generated);

            await using (var ctx = NewContextOnFile(path))
                await new GlobeStore(ctx).WriteAsync(world.Id, settings, generated);

            await using (var ctx = NewContextOnFile(path))
            {
                var rows = await ctx.GlobeCellNeighbours.AsNoTracking()
                    .Where(r => r.WorldId == world.Id)
                    .OrderBy(r => r.CellId).ThenBy(r => r.Slot)
                    .ToListAsync();
                var expected = new List<(int, int, int)>();
                for (var c = 0; c < grid.CellCount; c++)
                {
                    var n = grid.Neighbours(c);
                    for (var s = 0; s < n.Length; s++)
                        expected.Add((c, s, n[s]));
                }

                rows.Select(r => (r.CellId, (int)r.Slot, r.NeighbourCellId)).Should().Equal(expected);

                var run = await ctx.WorldGenRuns.AsNoTracking().SingleAsync(r => r.WorldId == world.Id);
                run.GeneratorVersion.Should().Be(generated.GeneratorVersion);
                JsonSerializer.Deserialize<WorldGenSettings>(run.SettingsJson).Should().Be(settings);
                using (var doc = JsonDocument.Parse(run.SettingsJson))
                    doc.RootElement.GetProperty("Grid").GetProperty("Frequency").GetInt32().Should().Be(4, "the row stores the nested section form");
                (await new GlobeStore(ctx).LoadSettingsAsync(world.Id)).Should().Be(settings);
                var stages = JsonSerializer.Deserialize<List<WorldGenStageRecord>>(run.StagesJson)!;
                stages.Select(s => (s.Stage, s.Hash, s.DurationTicks))
                    .Should().Equal(generated.Stages.Select(s => (s.Stage.ToString(), s.Hash, s.Duration.Ticks)));
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task LoadSettings_OfAWorldFromAnOlderGenerator_NamesBothVersionsAndAsksToRegenerate()
    {
        var path = TempDb();
        try
        {
            var settings = new WorldGenSettings { Grid = new() { Frequency = 4 } };
            var generated = WorldGenerator.Generate(7UL, settings);
            var world = await SeedWorldAsync(path, settings, generated);
            await using (var ctx = NewContextOnFile(path))
                await new GlobeStore(ctx).WriteAsync(world.Id, settings, generated);

            // A version 2 run stored a setting that version 3 retired (Biomes.MountainReliefM).
            await using (var ctx = NewContextOnFile(path))
            {
                var old = WorldGenerator.GeneratorVersion - 1;
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE worldgen_runs SET GeneratorVersion = {0}, SettingsJson = {1}",
                    old, """{ "Grid": { "Frequency": 4 }, "Biomes": { "MountainReliefM": 1500 } }""");
                var act = () => new GlobeStore(ctx).LoadSettingsAsync(world.Id);
                (await act.Should().ThrowAsync<GlobeGeneratorVersionException>())
                    .WithMessage($"world generated by generator v{old}; this build is v{WorldGenerator.GeneratorVersion} — regenerate the world")
                    .Which.StoredVersion.Should().Be(old);
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-5)]
    [Arguments(10_001)]
    public async Task RegionCellCoverage_OutsideOneTo10000_IsRejectedByTheDatabase(int coverage)
    {
        var path = TempDb();
        try
        {
            await using var ctx = NewContextOnFile(path);
            await ctx.Database.MigrateAsync();
            var world = World.Create("Globe", 7UL, CalendarDefinition.Default, "1.0.0").Value;
            var region = GeographicRegion.Create(world.Id, "Range", WorldEcon.SharedKernel.Globe.GeographicRegionKind.MountainRange).Value;
            ctx.Worlds.Add(world);
            ctx.GeographicRegions.Add(region);
            await ctx.SaveChangesAsync();

            var ok = () => ctx.Database.ExecuteSqlAsync($"INSERT INTO geographic_region_cells (GeographicRegionId, CellId, CoverageBp) VALUES ({region.Id.Value}, 1, 10000)");
            await ok.Should().NotThrowAsync();
            var bad = () => ctx.Database.ExecuteSqlAsync($"INSERT INTO geographic_region_cells (GeographicRegionId, CellId, CoverageBp) VALUES ({region.Id.Value}, 2, {coverage})");
            (await bad.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>()).WithMessage("*CHECK*");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Write_JoinsTheAmbientTransaction_SoARollbackLeavesNoGlobeRows()
    {
        var path = TempDb();
        try
        {
            var settings = new WorldGenSettings { Grid = new() { Frequency = 4 } };
            var generated = WorldGenerator.Generate(7UL, settings);
            var world = await SeedWorldAsync(path, settings, generated);

            await using (var ctx = NewContextOnFile(path))
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();
                await new GlobeStore(ctx).WriteAsync(world.Id, settings, generated);
                (await ctx.GlobeCells.CountAsync()).Should().Be(generated.Layers.Grid.CellCount, "rows are visible inside the transaction");
                (await ctx.TileBlocks.CountAsync()).Should().Be(generated.Layers.Grid.CellCount);
                await tx.RollbackAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                (await ctx.GlobeCells.CountAsync()).Should().Be(0);
                (await ctx.GlobeCellCompositions.CountAsync()).Should().Be(0);
                (await ctx.Set<WorldGenRunRow>().CountAsync()).Should().Be(0);
                (await ctx.TileBlocks.CountAsync()).Should().Be(0);
                (await ctx.Lakes.CountAsync()).Should().Be(0);
                (await ctx.RegionLevels.CountAsync()).Should().Be(0);
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Write_RejectsWorldRowThatDoesNotMatchTheGeneration()
    {
        var path = TempDb();
        try
        {
            var settings = new WorldGenSettings { Grid = new() { Frequency = 4 } };
            var generated = WorldGenerator.Generate(7UL, settings);
            var world = World.Create("Globe", 7UL, CalendarDefinition.Default, "1.0.0").Value; // defaults: m = 64, sea level 0
            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var act = () => new GlobeStore(ctx).WriteAsync(world.Id, settings, generated);
                await act.Should().ThrowAsync<InvalidOperationException>();
                (await ctx.GlobeCells.CountAsync()).Should().Be(0);
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Write_AtM64_Completes_AndPrintsElapsed()
    {
        var path = TempDb();
        try
        {
            // Tile multipliers 1 × 1 keep the 40,962-cell globe tables without generating 23.6 million tiles.
            var settings = new WorldGenSettings { Grid = new() { RegionMultiplier = 1, TileMultiplier = 1 } };
            var generated = WorldGenerator.Generate(7UL, settings);
            var world = await SeedWorldAsync(path, settings, generated);

            TimeSpan elapsed;
            await using (var ctx = NewContextOnFile(path))
                elapsed = await new GlobeStore(ctx).WriteAsync(world.Id, settings, generated);

            Console.WriteLine($"GlobeStore.WriteAsync at m = 64 ({generated.Layers.Grid.CellCount} cells): {elapsed.TotalMilliseconds:F0} ms");

            await using (var ctx = NewContextOnFile(path))
            {
                (await ctx.GlobeCells.CountAsync(r => r.WorldId == world.Id)).Should().Be(40_962);
                (await ctx.GlobeCellNeighbours.CountAsync(r => r.WorldId == world.Id)).Should().Be(40_962 * 6 - 12);
            }
        }
        finally { File.Delete(path); }
    }
}
