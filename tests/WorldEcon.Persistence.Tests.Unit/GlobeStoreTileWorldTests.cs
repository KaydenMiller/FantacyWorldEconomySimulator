using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Globe;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tests.Unit.Tiles;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Persistence.Tests.Unit;

/// <summary>
/// <see cref="GlobeStore.WriteAsync"/> / <see cref="GlobeStore.LoadAsync"/> with tiles on a generated world at Frequency 16
/// × 2 × 3 (tile frequency 96, 92,162 tiles, 2,562 globe cells, 10,242 region cells).
/// </summary>
public class GlobeStoreTileWorldTests
{
    private const ulong Seed = 42;
    private static readonly WorldGenSettings Settings = new() { Grid = new() { Frequency = 16, RegionMultiplier = 2, TileMultiplier = 3 } };
    private static readonly Lazy<GeneratedWorld> Generated = new(() => WorldGenerator.Generate(Seed, Settings));

    private static WorldDbContext NewContext(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"we_tileworld_{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static async Task<World> SeedWorldAsync(string path, GeneratedWorld generated)
    {
        var world = World.Create("Tiles", Seed, CalendarDefinition.Default, "1.0.0").Value;
        world.SetGlobe(Settings.Grid.Frequency, Settings.Grid.PlanetRadiusKm, generated.GeneratorVersion).IsError.Should().BeFalse();
        world.SetSeaLevel(generated.Layers.SeaLevelM);
        await using var ctx = NewContext(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        await ctx.SaveChangesAsync();
        return world;
    }

    private static async Task<World> WrittenWorldAsync(string path, List<GlobeWriteProgress>? progress = null)
    {
        var generated = Generated.Value;
        var world = await SeedWorldAsync(path, generated);
        await using var ctx = NewContext(path);
        await new GlobeStore(ctx).WriteAsync(world.Id, Settings, generated, progress is null ? null : new ListProgress(progress));
        return world;
    }

    private sealed class ListProgress(List<GlobeWriteProgress> into) : IProgress<GlobeWriteProgress>
    {
        public void Report(GlobeWriteProgress value) => into.Add(value);
    }

    [Test]
    public async Task WriteThenLoad_EveryTileFieldOfEveryBlock_RegionSummaryAndLakes_EqualTheGeneration()
    {
        var path = TempDb();
        try
        {
            var generated = Generated.Value;
            var tiles = generated.Tiles!;
            generated.Lakes.Should().Contain(l => l.Kind == TileWaterKind.Lake && l.ExitTile >= 0, "the fixture must exercise full lakes with exit tiles");
            generated.Lakes.Should().Contain(l => l.Inflow > 0 && l.Evaporation > 0);
            var progress = new List<GlobeWriteProgress>();
            var world = await WrittenWorldAsync(path, progress);

            progress.Select(p => p.Step).Distinct().Should().Equal(GlobeWriteStep.GlobeTables, GlobeWriteStep.TileBlocks, GlobeWriteStep.Lakes, GlobeWriteStep.RegionLevel);
            progress.Where(p => p.Step == GlobeWriteStep.TileBlocks).Select(p => p.Fraction).Should().BeInAscendingOrder().And.EndWith(1.0);

            await using var ctx = NewContext(path);
            var store = new GlobeStore(ctx);
            var stored = await store.LoadAsync(world.Id);

            stored.Settings.Should().Be(Settings);
            stored.GeneratorVersion.Should().Be(WorldGenerator.GeneratorVersion);
            stored.TileGrid.Frequency.Should().Be(96);
            stored.TileGrid.Count.Should().Be(tiles.Count);
            stored.Layers.Grid.CellCount.Should().Be(2_562);
            TileSummaryStage.GlobeHash(stored.Layers).Should().Be(TileSummaryStage.GlobeHash(generated.Layers));
            stored.Regions.Count.Should().Be(10_242);
            TileSummaryStage.RegionHash(stored.Regions).Should().Be(TileSummaryStage.RegionHash(generated.Regions!));
            stored.Lakes.Should().Equal(generated.Lakes, "every TileLake field, including Inflow, Evaporation and ExitTile, round-trips");
            stored.Imagery.Should().NotBeNull("worlds written by this build carry the map imagery");
            stored.Imagery!.Biome.Should().Equal(generated.Imagery!.Biome);
            stored.Imagery.Water.Should().Equal(generated.Imagery.Water);
            stored.Imagery.ElevationM.Should().Equal(generated.Imagery.ElevationM);
            stored.RiverCourses.Should().NotBeNull();
            stored.RiverCourses!.From.Should().Equal(generated.RiverCourses!.From);
            stored.RiverCourses.To.Should().Equal(generated.RiverCourses.To);
            stored.RiverCourses.ToWater.Should().Equal(generated.RiverCourses.ToWater);

            (await ctx.TileBlocks.CountAsync()).Should().Be(2_562);
            (await ctx.TileBlocks.SumAsync(r => r.TileCount)).Should().Be(tiles.Count);
            var expected = new InMemoryTileBlockSource(tiles);
            var fromFile = new SqliteTileBlockSource(path);
            for (var c = 0; c < 2_562; c++)
            {
                var want = expected.Get(c)!;
                SyntheticTileBlocks.AssertEqual(want, (await store.LoadTileBlockAsync(c))!);
                SyntheticTileBlocks.AssertEqual(want, (await fromFile.LoadAsync(c, CancellationToken.None))!);
            }

            (await fromFile.LoadAsync(2_562, CancellationToken.None)).Should().BeNull();
            var rowids = await ctx.Database.SqlQueryRaw<int>("SELECT CellId AS Value FROM tile_blocks ORDER BY rowid").ToListAsync();
            rowids.Should().Equal(Enumerable.Range(0, 2_562), "blocks are inserted in cell order");
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task SqliteTileBlockSource_ConcurrentLoadsFromManyThreads_EqualTheGeneration()
    {
        var path = TempDb();
        try
        {
            await WrittenWorldAsync(path);
            var expected = new InMemoryTileBlockSource(Generated.Value.Tiles!);
            var source = new SqliteTileBlockSource(path);
            var cells = Enumerable.Range(0, 2_562).Concat(Enumerable.Range(0, 2_562).Reverse()).ToArray();
            var loaded = new TileBlock?[cells.Length];
            await Parallel.ForAsync(0, cells.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                async (i, ct) => loaded[i] = await source.LoadAsync(cells[i], ct));
            for (var i = 0; i < cells.Length; i++)
                SyntheticTileBlocks.AssertEqual(expected.Get(cells[i])!, loaded[i]!);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Load_OfAWorldFromAnOlderGenerator_ThrowsTheRegenerateError_ForLoadAndLoadLayers()
    {
        var path = TempDb();
        try
        {
            var world = await WrittenWorldAsync(path);
            await using var ctx = NewContext(path);
            var old = WorldGenerator.GeneratorVersion - 1;
            await ctx.Database.ExecuteSqlRawAsync("UPDATE worldgen_runs SET GeneratorVersion = {0}", old);
            var message = $"world generated by generator v{old}; this build is v{WorldGenerator.GeneratorVersion} — regenerate the world";

            var load = () => new GlobeStore(ctx).LoadAsync(world.Id);
            (await load.Should().ThrowAsync<GlobeGeneratorVersionException>()).WithMessage(message);
            var layers = () => new GlobeStore(ctx).LoadLayersAsync(world.Id);
            (await layers.Should().ThrowAsync<GlobeGeneratorVersionException>()).WithMessage(message);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Load_OfAWorldFromANewerGenerator_ThrowsAndAsksForANewerBuild()
    {
        var path = TempDb();
        try
        {
            var world = await WrittenWorldAsync(path);
            await using var ctx = NewContext(path);
            var newer = WorldGenerator.GeneratorVersion + 1;
            await ctx.Database.ExecuteSqlRawAsync("UPDATE worldgen_runs SET GeneratorVersion = {0}", newer);
            var load = () => new GlobeStore(ctx).LoadAsync(world.Id);
            (await load.Should().ThrowAsync<GlobeGeneratorVersionException>())
                .WithMessage($"world generated by generator v{newer}; this build is v{WorldGenerator.GeneratorVersion} — open it with a newer build of this program");
        }
        finally { Cleanup(path); }
    }

    [Test]
    [Arguments("UPDATE lakes SET Kind = 0 WHERE Id = (SELECT MIN(Id) FROM lakes)", "*kind 0*")]
    [Arguments("DELETE FROM lakes WHERE Id = {named}", "*names lake*")]
    public async Task Load_WithAnInvalidLakeKind_OrARegionCellNamingAMissingLake_ThrowsInvalidData(string sql, string message)
    {
        var path = TempDb();
        try
        {
            var world = await WrittenWorldAsync(path);
            // {named}: a lake id that a region cell names.
            var named = Generated.Value.Regions!.LakeId.Max();
            await using var ctx = NewContext(path);
#pragma warning disable EF1002 // test-only SQL built from constants and an integer
            await ctx.Database.ExecuteSqlRawAsync(sql.Replace("{named}", named.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
#pragma warning restore EF1002
            var load = () => new GlobeStore(ctx).LoadAsync(world.Id);
            (await load.Should().ThrowAsync<InvalidDataException>()).WithMessage(message);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task SqliteTileBlockSource_NullData_ThrowsInvalidData()
    {
        var path = TempDb();
        try
        {
            await using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE tile_blocks (CellId INTEGER PRIMARY KEY, TileCount INTEGER NOT NULL, Data BLOB); INSERT INTO tile_blocks VALUES (3, 10, NULL);";
                await cmd.ExecuteNonQueryAsync();
            }

            var source = new SqliteTileBlockSource(path);
            var load = () => source.LoadAsync(3, CancellationToken.None);
            (await load.Should().ThrowAsync<InvalidDataException>()).WithMessage("*row 3 has no data*");
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Load_WithoutARegionLevelRow_AsksToRegenerate()
    {
        var path = TempDb();
        try
        {
            var world = await WrittenWorldAsync(path);
            await using var ctx = NewContext(path);
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM region_level");
            var load = () => new GlobeStore(ctx).LoadAsync(world.Id);
            (await load.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no region level*regenerate the world*");
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Load_WithoutMapImageryRows_LoadsWithNoImagery()
    {
        var path = TempDb();
        try
        {
            var world = await WrittenWorldAsync(path);
            await using var ctx = NewContext(path);
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM map_imagery");
            var stored = await new GlobeStore(ctx).LoadAsync(world.Id);
            stored.Imagery.Should().BeNull("a world written before the map imagery existed draws with flat cell colours");
            stored.RiverCourses.Should().BeNull();
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Load_OfAFileWithoutTheMapImageryTable_LoadsWithNoImagery()
    {
        var path = TempDb();
        try
        {
            var world = await WrittenWorldAsync(path);
            await using var ctx = NewContext(path);
            // A file written before the AddMapImagery migration (opening does not migrate).
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE map_imagery");
            var stored = await new GlobeStore(ctx).LoadAsync(world.Id);
            stored.Imagery.Should().BeNull();
            stored.RiverCourses.Should().BeNull();
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Write_OfAGlobeOnlyGeneration_IsRejected_AndWritesNothing()
    {
        var path = TempDb();
        try
        {
            var globeOnly = WorldGenerator.GenerateGlobe(Seed, Settings);
            var world = await SeedWorldAsync(path, globeOnly);
            await using var ctx = NewContext(path);
            var act = () => new GlobeStore(ctx).WriteAsync(world.Id, Settings, globeOnly);
            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no tile layers*");
            (await ctx.GlobeCells.CountAsync()).Should().Be(0);
            (await ctx.TileBlocks.CountAsync()).Should().Be(0);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Write_ProgressHandlerThrowingDuringTileBlocks_RollsBackEveryTable()
    {
        var path = TempDb();
        try
        {
            var generated = Generated.Value;
            var world = await SeedWorldAsync(path, generated);
            await using (var ctx = NewContext(path))
            {
                var act = () => new GlobeStore(ctx).WriteAsync(world.Id, Settings, generated, new ThrowAt(GlobeWriteStep.TileBlocks));
                (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("injected");
            }

            await using (var ctx = NewContext(path))
            {
                (await ctx.GlobeCells.CountAsync()).Should().Be(0);
                (await ctx.WorldGenRuns.CountAsync()).Should().Be(0);
                (await ctx.TileBlocks.CountAsync()).Should().Be(0);
                (await ctx.Lakes.CountAsync()).Should().Be(0);
                (await ctx.RegionLevels.CountAsync()).Should().Be(0);
            }
        }
        finally { Cleanup(path); }
    }

    private sealed class ThrowAt(GlobeWriteStep step) : IProgress<GlobeWriteProgress>
    {
        public void Report(GlobeWriteProgress value)
        {
            if (value.Step == step && value.Fraction > 0) throw new InvalidOperationException("injected");
        }
    }
}
