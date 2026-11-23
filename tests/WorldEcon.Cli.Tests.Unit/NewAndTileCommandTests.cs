using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Persistence;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Cli.Tests.Unit;

/// <summary>
/// <c>new --preset</c> with a generation file, <c>globe</c> tile statistics and <c>globe tile</c> end to end. The world is the
/// small-5km preset (radius 3,000 km) with the grid overridden to Frequency 16 × 2 × 2 (tile frequency 64, 40,962 tiles)
/// for test speed.
/// </summary>
[NotInParallel]
public class NewAndTileCommandTests
{
    private const string GridOverride = """{ "Grid": { "Frequency": 16, "RegionMultiplier": 2, "TileMultiplier": 2 } }""";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var context = TestContext.Current!;
        var outStart = context.Output.GetStandardOutput().Length;
        var errStart = context.Output.GetErrorOutput().Length;
        var exit = await CommandRunner.RunAsync(args);
        return (exit, context.Output.GetStandardOutput()[outStart..], context.Output.GetErrorOutput()[errStart..]);
    }

    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), $"cli_{Guid.NewGuid():N}{extension}");

    private static WorldDbContext Open(string path) =>
        new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task New_with_preset_and_file_prints_stages_and_stats_then_globe_and_globe_tile_show_the_tiles()
    {
        var db = TempPath(".db");
        var file = TempPath(".json");
        await File.WriteAllTextAsync(file, GridOverride);
        try
        {
            var (exit, output, err) = await RunAsync("new", db, "--preset", "small-5km", "--generation", file);
            exit.Should().Be(0, err);
            // Per-stage progress lines, then the statistics.
            output.Should().Contain("preset small-5km").And.Contain("radius 3,000 km, frequency 16 x 2 x 2")
                .And.MatchRegex(@"\[\s*\d+ %\] TileSeaLevel\s+\d+\.\d\d s")
                .And.MatchRegex(@"\[\s*\d+ %\] Saving tile blocks\s+\d+\.\d\d s")
                .And.Contain("Globe cells: 2,562").And.Contain("Region cells: 10,242")
                .And.Contain("Tiles:       40,962, tile size").And.Contain("Lakes:").And.Contain("Total time:")
                .And.Contain("Database:").And.Contain("Peak memory:");
            var expectedSize = WorldGen.Tiles.TileMetrics.SizeKm(3_000, 40_962).ToString("F2", Inv);
            output.Should().Contain($"tile size {expectedSize} km");

            var (sExit, settingsJson, _) = await RunAsync("globe", "settings", db);
            sExit.Should().Be(0);
            WorldGenSettingsJson.Deserialize(settingsJson).Should().Be(WorldGenSettingsJson.Deserialize(GridOverride, WorldGenPresets.Get("small-5km")),
                "the file overrides the preset; the preset radius 3,000 km stays");

            var (statsExit, stats, statsErr) = await RunAsync("globe", db);
            statsExit.Should().Be(0, statsErr);
            stats.Should().Contain("Regions:      10,242 region cells, frequency 32 (x 2)")
                .And.Contain($"Tiles:        40,962 tiles, frequency 64 (x 2), tile size {expectedSize} km; stored 40,962 tiles in 2,562 blocks")
                .And.Contain("Tile lakes:");

            // A pinned settlement: its tile, then the same tile by --at its centre.
            string name;
            int cell, tile;
            await using (var ctx = Open(db))
            {
                var s = await ctx.Settlements.AsNoTracking().OrderBy(x => x.Name).FirstAsync();
                (name, cell, tile) = (s.Name, s.CellId!.Value.Value, s.TileId!.Value);
            }

            SqliteConnection.ClearAllPools();
            var (tExit, details, tErr) = await RunAsync("globe", "tile", db, tile.ToString(Inv));
            tExit.Should().Be(0, tErr);
            details.Should().Contain($"Tile {tile}").And.Contain($"Globe cell:   {cell}").And.Contain("Region cell:")
                .And.Contain("Elevation:").And.Contain("Landform:").And.Contain("Biome:").And.Contain("Water:").And.Contain("Lake:")
                .And.Contain("Flow:").And.Contain("Temperature:").And.Contain("Precipitation:").And.Contain($"Settlements:  {name}");

            var v = new TileGrid(64).Centre(tile);
            var lat = (Math.Asin(v.Y) * 180 / Math.PI).ToString("R", Inv);
            var lon = (Math.Atan2(v.X, v.Z) * 180 / Math.PI).ToString("R", Inv);
            var (aExit, at, aErr) = await RunAsync("globe", "tile", db, "--at", $"{lat},{lon}");
            aExit.Should().Be(0, aErr);
            at.Should().StartWith($"Tile {tile}").And.Contain($"Settlements:  {name}");

            var (badExit, _, badErr) = await RunAsync("globe", "tile", db, "40962");
            badExit.Should().Be(1);
            badErr.Should().Contain("Tile id must be an integer in 0..40961");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(db);
            File.Delete(file);
        }
    }

    [Test]
    public async Task New_rejects_an_unknown_preset_before_creating_the_database()
    {
        var db = TempPath(".db");
        var (exit, _, err) = await RunAsync("new", db, "--preset", "moon");
        exit.Should().Be(1);
        err.Should().Contain("Unknown preset 'moon'").And.Contain("earth-5km, earth-10km, small-5km");
        File.Exists(db).Should().BeFalse();
    }

    [Test]
    public async Task New_on_an_existing_file_fails_before_generating_and_leaves_the_file()
    {
        var db = TempPath(".db");
        await File.WriteAllTextAsync(db, "keep");
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (exit, output, err) = await RunAsync("new", db);
            exit.Should().Be(1);
            err.Should().Contain("already exists");
            output.Should().NotContain("Generating");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "nothing is generated");
            (await File.ReadAllTextAsync(db)).Should().Be("keep");
        }
        finally { File.Delete(db); }
    }
}
