using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Persistence.Globe;
using WorldEcon.WorldGen.Tests.Unit.Tiles;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Persistence.Tests.Unit;

public class TileBlockStoreTests
{
    private static WorldDbContext NewContext(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"we_tiles_{Guid.NewGuid():N}.db");

    private static async Task<WorldDbContext> OpenMigratedAsync(string path)
    {
        var ctx = NewContext(path);
        await ctx.Database.MigrateAsync();
        return ctx;
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Test]
    public async Task WriteThenLoad_FewHundredBlocks_EveryFieldEqual()
    {
        var path = TempDb();
        try
        {
            var blocks = new List<TileBlock>();
            for (var cell = 0; cell < 300; cell++)
                blocks.Add(cell switch
                {
                    0 => new TileBlock(0, [], [], [], [], [], [], [], [], [], []),
                    1 => SyntheticTileBlocks.Extremes(1, 1, 1),
                    2 => SyntheticTileBlocks.Extremes(2, 576, 2),
                    < 12 => SyntheticTileBlocks.Realistic(cell, 480),
                    _ => SyntheticTileBlocks.Realistic(cell, 576),
                });

            await using (var ctx = await OpenMigratedAsync(path))
                await new GlobeStore(ctx).WriteTileBlocksAsync(blocks);

            await using (var ctx = NewContext(path))
            {
                var store = new GlobeStore(ctx);
                foreach (var expected in blocks)
                {
                    var actual = await store.LoadTileBlockAsync(expected.GlobeCell);
                    actual.Should().NotBeNull();
                    SyntheticTileBlocks.AssertEqual(expected, actual!);
                }

                (await store.LoadTileBlockAsync(300)).Should().BeNull();
                (await ((ITileBlockSource)store).LoadAsync(5, CancellationToken.None))!.GlobeCell.Should().Be(5);
                (await ctx.TileBlocks.CountAsync()).Should().Be(300);
                (await ctx.TileBlocks.Where(r => r.CellId == 40).Select(r => r.TileCount).SingleAsync()).Should().Be(576);
            }
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Write_MoreThanOneBatch_KeepsOrderAndAllRows()
    {
        var path = TempDb();
        try
        {
            var n = GlobeStore.EncodeBatchSize * 2 + 7;
            await using (var ctx = await OpenMigratedAsync(path))
                await new GlobeStore(ctx).WriteTileBlocksAsync(Enumerable.Range(0, n).Select(c => SyntheticTileBlocks.Realistic(c, 20)));
            await using (var ctx = NewContext(path))
            {
                (await ctx.TileBlocks.AsNoTracking().OrderBy(r => r.CellId).Select(r => r.CellId).ToListAsync()).Should().Equal(Enumerable.Range(0, n));
                // rowid order equals cell order, so a table scan returns cells ascending.
                var rowOrder = await ctx.Database.SqlQueryRaw<int>("SELECT CellId AS Value FROM tile_blocks ORDER BY rowid").ToListAsync();
                rowOrder.Should().Equal(Enumerable.Range(0, n));
            }
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Write_JoinsAmbientTransaction_RollbackDiscardsRows()
    {
        var path = TempDb();
        try
        {
            await using var ctx = await OpenMigratedAsync(path);
            await using (var tx = await ctx.Database.BeginTransactionAsync())
            {
                await new GlobeStore(ctx).WriteTileBlocksAsync([SyntheticTileBlocks.Realistic(1, 50)]);
                await new GlobeStore(ctx).WriteRegionLevelAsync([1, 2, 3]);
                await tx.RollbackAsync();
            }

            (await ctx.TileBlocks.CountAsync()).Should().Be(0);
            (await new GlobeStore(ctx).LoadRegionLevelAsync()).Should().BeNull();
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Load_CorruptRow_ThrowsInvalidData()
    {
        var path = TempDb();
        try
        {
            await using var ctx = await OpenMigratedAsync(path);
            var store = new GlobeStore(ctx);
            await store.WriteTileBlocksAsync([SyntheticTileBlocks.Realistic(4, 50)]);
            // Row says cell 9, payload says cell 4.
            await ctx.Database.ExecuteSqlRawAsync("UPDATE tile_blocks SET CellId = 9");
            var act = async () => await store.LoadTileBlockAsync(9);
            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task LoadingOneBlock_DoesNotReadOtherRows()
    {
        var path = TempDb();
        try
        {
            await using var ctx = await OpenMigratedAsync(path);
            var store = new GlobeStore(ctx);
            await store.WriteTileBlocksAsync(Enumerable.Range(0, 20).Select(c => SyntheticTileBlocks.Realistic(c, 50)));
            // Damage every other row's payload: loading cell 3 must still succeed because only its row is read.
            await ctx.Database.ExecuteSqlRawAsync("UPDATE tile_blocks SET Data = x'00' WHERE CellId <> 3");
            (await store.LoadTileBlockAsync(3))!.GlobeCell.Should().Be(3);
            var act = async () => await store.LoadTileBlockAsync(4);
            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task Lakes_RoundTrip()
    {
        var path = TempDb();
        try
        {
            var lakes = new List<TileLake>
            {
                new(0, TileWaterKind.Lake, 123, 5000, 1234.5678901234, 777, 12),
                new(1, TileWaterKind.DryLakeBed, -40, 1, 0.1 + 0.2, -1, int.MaxValue),
                new(2, TileWaterKind.Lake, short.MinValue, int.MaxValue, double.Epsilon, 0, 0),
            };
            await using (var ctx = await OpenMigratedAsync(path))
                await new GlobeStore(ctx).WriteLakesAsync(lakes);
            await using (var ctx = NewContext(path))
                (await new GlobeStore(ctx).LoadLakesAsync()).Should().Equal(lakes);
        }
        finally { Cleanup(path); }
    }

    [Test]
    public async Task RegionLevel_RoundTrip_AndReplace()
    {
        var path = TempDb();
        try
        {
            var data = new byte[100_000];
            new Random(5).NextBytes(data);
            await using var ctx = await OpenMigratedAsync(path);
            var store = new GlobeStore(ctx);
            (await store.LoadRegionLevelAsync()).Should().BeNull();
            await store.WriteRegionLevelAsync(data);
            (await store.LoadRegionLevelAsync()).Should().Equal(data);
            await store.WriteRegionLevelAsync([9]);
            (await store.LoadRegionLevelAsync()).Should().Equal(new byte[] { 9 });
            (await ctx.RegionLevels.CountAsync()).Should().Be(1);
        }
        finally { Cleanup(path); }
    }

    [Test]
    [Category("Slow")]
    public async Task Measure_DefaultWorldSize_40962Blocks_EncodeWriteTimeAndDbSize()
    {
        var path = TempDb();
        try
        {
            const int cells = 40_962;
            await using (var ctx = await OpenMigratedAsync(path))
            {
                var sw = Stopwatch.StartNew();
                // Generation of the synthetic block is inside the timing; it is part of the lazy enumerable the writer consumes.
                long genTicks = 0;
                IEnumerable<TileBlock> Source()
                {
                    for (var c = 0; c < cells; c++)
                    {
                        var t = Stopwatch.GetTimestamp();
                        var b = SyntheticTileBlocks.Realistic(c, c < 12 ? 480 : 576);
                        genTicks += Stopwatch.GetTimestamp() - t;
                        yield return b;
                    }
                }

                await new GlobeStore(ctx).WriteTileBlocksAsync(Source());
                sw.Stop();
                var gen = TimeSpan.FromSeconds(genTicks / (double)Stopwatch.Frequency);
                Console.WriteLine($"MEASURE write {cells} blocks: total {sw.Elapsed.TotalSeconds:F1} s (synthetic generation {gen.TotalSeconds:F1} s, so encode+insert about {(sw.Elapsed - gen).TotalSeconds:F1} s)");
            }

            SqliteConnection.ClearAllPools();
            var size = new FileInfo(path).Length;
            Console.WriteLine($"MEASURE db size {size / 1_000_000.0:F1} MB ({size / (double)cells:F0} B/cell)");

            await using (var ctx = NewContext(path))
            {
                var store = new GlobeStore(ctx);
                var sw = Stopwatch.StartNew();
                for (var c = 0; c < 2000; c += 4)
                    await store.LoadTileBlockAsync(c);
                Console.WriteLine($"MEASURE single block load: {sw.Elapsed.TotalMilliseconds / 500:F3} ms avg over 500 loads");
                (await ctx.TileBlocks.CountAsync()).Should().Be(cells);
            }

            size.Should().BeLessThan(400L * 1_000_000);
        }
        finally { Cleanup(path); }
    }
}
