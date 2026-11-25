using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.Seeding;
using WorldEcon.SharedKernel;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Cli.Tests.Unit;

/// <summary>
/// A CLI <c>advance</c> killed part-way leaves the file at its tick from before the advance: the whole advance is one
/// transaction, and a killed process never commits it.
/// </summary>
[NotInParallel]
public class AdvanceKillTests
{
    private static string SampleSeedPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "samples", "aerthos.seed.json")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            throw new FileNotFoundException("Could not locate samples/aerthos.seed.json from the test assembly.");
        return Path.Combine(dir, "samples", "aerthos.seed.json");
    }

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    [Test]
    [Category("Slow")]
    public async Task KilledAdvance_LeavesPreRunTick()
    {
        var db = Path.Combine(Path.GetTempPath(), $"cli_kill_{Guid.NewGuid():N}.db");
        try
        {
            var seed = await new JsonSeedSource(SampleSeedPath()).LoadAsync();
            seed = seed with { Generation = new WorldGenSettings { Grid = new() { Frequency = 16 } } };
            long t0;
            await using (var ctx = Open(db))
            {
                await ctx.Database.MigrateAsync();
                await new SeedImporter(ctx).ImportAsync(seed);
                var world = await ctx.Worlds.FirstAsync();
                var sim = await SimulationContext.LoadAsync(ctx, world.Id);
                await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, 7 * Tick.DefaultMinutesPerDay); // caravans exist
                t0 = sim.World.CurrentTick.Value;
            }

            SqliteConnection.ClearAllPools(); // closes the last connection: the WAL is checkpointed and removed

            var cliDll = Path.Combine(AppContext.BaseDirectory, "WorldEcon.Cli.dll");
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            using var child = Process.Start(new ProcessStartInfo(dotnet)
            {
                ArgumentList = { cliDll, "advance", db, "10y" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            // Wait until the advance has written into the WAL (steps saved inside its open transaction), then a little more.
            var sw = Stopwatch.StartNew();
            var wal = db + "-wal";
            while (sw.Elapsed < TimeSpan.FromSeconds(30) && !child.HasExited && !(File.Exists(wal) && new FileInfo(wal).Length > 64 * 1024))
                await Task.Delay(50);
            await Task.Delay(500);

            child.HasExited.Should().BeFalse("advance finished before kill; raise the duration");
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();

            await using (var ctx = Open(db))
                (await ctx.Worlds.FirstAsync()).CurrentTick.Value.Should().Be(t0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { db, db + "-wal", db + "-shm", db + "-journal" })
                File.Delete(f);
        }
    }
}
