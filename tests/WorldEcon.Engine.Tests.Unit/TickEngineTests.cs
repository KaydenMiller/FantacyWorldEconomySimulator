using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class TickEngineTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    /// <summary>Creates a fresh SQLite DB with a single World row and returns (path, worldId).</summary>
    private static async Task<(string Path, WorldId WorldId)> SeedWorldAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_engine_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        await ctx.SaveChangesAsync();

        return (path, world.Id);
    }

    [Test]
    public async Task Advance_RunsDailyPhase_OncePerDayBoundary()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            var sim = await SimulationContext.LoadAsync(ctx, worldId);
            var phase = new RecordingPhase("daily", order: 0, cadenceTicks: 1440);
            var engine = new TickEngine([phase]);

            await engine.AdvanceAsync(sim, 2880);

            phase.Count.Should().Be(2);
            phase.RanAtTicks.Should().Equal(1440, 2880);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Advance_RunsHourlyPhase_OncePerHourBoundary()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            var sim = await SimulationContext.LoadAsync(ctx, worldId);
            var phase = new RecordingPhase("hourly", order: 0, cadenceTicks: 60);
            var engine = new TickEngine([phase]);

            await engine.AdvanceAsync(sim, 120);

            phase.Count.Should().Be(2);
            phase.RanAtTicks.Should().Equal(60, 120);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Advance_PersistsCurrentTick()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using (var ctx = NewContextOnFile(path))
            {
                var sim = await SimulationContext.LoadAsync(ctx, worldId);
                var engine = new TickEngine([]);
                await engine.AdvanceAsync(sim, 100);
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var reloaded = await ctx.Worlds.FirstAsync(w => w.Id == worldId);
                reloaded.CurrentTick.Value.Should().Be(100);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Advance_RunsPhasesInOrder()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            var sim = await SimulationContext.LoadAsync(ctx, worldId);

            var log = new List<string>();
            var phase1 = new RecordingPhase("second", order: 1, cadenceTicks: 1, sharedOrderLog: log);
            var phase0 = new RecordingPhase("first", order: 0, cadenceTicks: 1, sharedOrderLog: log);

            // Pass out of order to prove the engine sorts by Order.
            var engine = new TickEngine([phase1, phase0]);

            await engine.AdvanceAsync(sim, 1);

            log.Should().Equal("first", "second");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Advance_NegativeTicks_Throws()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            var sim = await SimulationContext.LoadAsync(ctx, worldId);
            var engine = new TickEngine([]);

            var act = async () => await engine.AdvanceAsync(sim, -1);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task LoadAsync_MissingWorld_Throws()
    {
        var (path, _) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            var act = async () => await SimulationContext.LoadAsync(ctx, WorldId.New());
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
