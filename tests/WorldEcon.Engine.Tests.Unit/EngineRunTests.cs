using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class EngineRunTests
{
    private const long Day = 1440;

    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static async Task<(string Path, WorldId WorldId)> SeedWorldAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_run_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        await ctx.SaveChangesAsync();

        return (path, world.Id);
    }

    private static void DeleteFile(string path)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    private static async Task<(long Tick, int Snapshots)> ReadFileAsync(string path)
    {
        await using var ctx = NewContextOnFile(path);
        var tick = (await ctx.Worlds.SingleAsync()).CurrentTick.Value;
        var snapshots = await ctx.MoneyLedgerSnapshots.CountAsync();
        return (tick, snapshots);
    }

    [Test]
    public async Task Steps_ThenEnd_RunPhasesAtSameTicks_AsOneAdvance()
    {
        var (pathA, worldA) = await SeedWorldAsync();
        var (pathB, worldB) = await SeedWorldAsync();
        try
        {
            var phaseA = new RecordingPhase("daily", order: 0, cadenceTicks: Day);
            await using (var ctx = NewContextOnFile(pathA))
            {
                await using var run = await new TickEngine([phaseA]).BeginRunAsync(ctx, worldA);
                await run.StepToAsync(new Tick(1000));
                await run.StepToAsync(new Tick(2 * Day));
                await run.StepToAsync(new Tick(3 * Day));
                await run.EndAsync();
            }

            var phaseB = new RecordingPhase("daily", order: 0, cadenceTicks: Day);
            await using (var ctx = NewContextOnFile(pathB))
            {
                var sim = await SimulationContext.LoadAsync(ctx, worldB);
                await new TickEngine([phaseB]).AdvanceAsync(sim, 3 * Day);
            }

            phaseA.RanAtTicks.Should().Equal(Day, 2 * Day, 3 * Day);
            phaseB.RanAtTicks.Should().Equal(Day, 2 * Day, 3 * Day);
            var a = await ReadFileAsync(pathA);
            var b = await ReadFileAsync(pathB);
            a.Tick.Should().Be(3 * Day);
            b.Tick.Should().Be(3 * Day);
            a.Snapshots.Should().Be(b.Snapshots);
        }
        finally
        {
            DeleteFile(pathA);
            DeleteFile(pathB);
        }
    }

    [Test]
    public async Task StepTo_MidDay_RunsNoPhase()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            var phase = new RecordingPhase("daily", order: 0, cadenceTicks: Day);
            await using var run = await new TickEngine([phase]).BeginRunAsync(ctx, worldId);

            var reached = await run.StepToAsync(new Tick(600));

            reached.Value.Should().Be(600);
            phase.Count.Should().Be(0);
            run.LastStepRanPhases.Should().BeFalse();
            run.NextBoundaryTick.Should().Be(Day);
            await run.EndAsync();
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task Dispose_WithoutEnd_RollsBack()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using (var ctx = NewContextOnFile(path))
            {
                var phase = new RecordingPhase("daily", order: 0, cadenceTicks: Day);
                await using (var run = await new TickEngine([phase]).BeginRunAsync(ctx, worldId))
                {
                    await run.StepToAsync(new Tick(2 * Day));
                }

                ctx.ChangeTracker.Entries().Should().BeEmpty();
            }

            (await ReadFileAsync(path)).Tick.Should().Be(0);
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task Cancel_StopsOnCompletedBoundary()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            var phase = new CallbackPhase(Day, tick => { if (tick.Value == 2 * Day) cts.Cancel(); });
            await using (var ctx = NewContextOnFile(path))
            {
                await using var run = await new TickEngine([phase]).BeginRunAsync(ctx, worldId);
                var reached = await run.StepToAsync(new Tick(10 * Day), ct: cts.Token);
                reached.Value.Should().Be(2 * Day);
                await run.EndAsync();
            }

            (await ReadFileAsync(path)).Tick.Should().Be(2 * Day);
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task Progress_ReportedPerBoundary()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            var phase = new RecordingPhase("daily", order: 0, cadenceTicks: Day);
            var progress = new CollectingProgress();
            await using var run = await new TickEngine([phase]).BeginRunAsync(ctx, worldId);

            await run.StepToAsync(new Tick(2 * Day), progress);

            progress.Reported.Should().Equal(Day, 2 * Day);
            await run.EndAsync();
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task SecondWriter_FailsWhileRunOpen()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            await using var run = await new TickEngine([]).BeginRunAsync(ctx, worldId);

            await using var other = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=1");
            await other.OpenAsync();
            await using var cmd = other.CreateCommand();
            cmd.CommandText = "UPDATE worlds SET Name = Name";
            var act = async () => await cmd.ExecuteNonQueryAsync();

            (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(5);
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task JoinsAmbientTransaction()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using (var ctx = NewContextOnFile(path))
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();
                var sim = await SimulationContext.LoadAsync(ctx, worldId);
                await new TickEngine([new RecordingPhase("daily", 0, Day)]).AdvanceAsync(sim, Day);
                await tx.RollbackAsync();
            }

            (await ReadFileAsync(path)).Tick.Should().Be(0);
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task EndTwice_Throws()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            await using var run = await new TickEngine([]).BeginRunAsync(ctx, worldId);
            await run.EndAsync();

            var act = async () => await run.EndAsync();
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task StepTo_BeforeNow_Throws()
    {
        var (path, worldId) = await SeedWorldAsync();
        try
        {
            await using var ctx = NewContextOnFile(path);
            await using var run = await new TickEngine([]).BeginRunAsync(ctx, worldId);
            await run.StepToAsync(new Tick(100));

            var act = async () => await run.StepToAsync(new Tick(50));
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }
        finally
        {
            DeleteFile(path);
        }
    }

    [Test]
    public async Task AddedRows_VisibleAtNextBoundary_SameForStepsAndOneAdvance()
    {
        var (pathA, worldA) = await SeedWorldAsync();
        var (pathB, worldB) = await SeedWorldAsync();
        try
        {
            var seenA = new List<int>();
            await using (var ctx = NewContextOnFile(pathA))
            {
                await using var run = await new TickEngine([new AddContinentPhase(), new CountContinentsPhase(seenA)]).BeginRunAsync(ctx, worldA);
                for (var d = 1; d <= 4; d++) await run.StepToAsync(new Tick(d * Day));
                await run.EndAsync();
            }

            var seenB = new List<int>();
            await using (var ctx = NewContextOnFile(pathB))
            {
                var sim = await SimulationContext.LoadAsync(ctx, worldB);
                await new TickEngine([new AddContinentPhase(), new CountContinentsPhase(seenB)]).AdvanceAsync(sim, 4 * Day);
            }

            // Each day's continent is added by the first phase and saved after the boundary: the next day's query sees it.
            seenA.Should().Equal(0, 1, 2, 3);
            seenB.Should().Equal(0, 1, 2, 3);
        }
        finally
        {
            DeleteFile(pathA);
            DeleteFile(pathB);
        }
    }

    private sealed class AddContinentPhase : ISimulationPhase
    {
        public string Name => "add";
        public int Order => 0;
        public long CadenceTicks => Day;

        public Task ExecuteAsync(SimulationContext ctx, Tick tick)
        {
            ctx.Db.Continents.Add(Continent.Create(ctx.World.Id, $"C{tick.Value}").Value);
            return Task.CompletedTask;
        }
    }

    private sealed class CountContinentsPhase(List<int> seen) : ISimulationPhase
    {
        public string Name => "count";
        public int Order => 1;
        public long CadenceTicks => Day;

        public async Task ExecuteAsync(SimulationContext ctx, Tick tick) => seen.Add(await ctx.Db.Continents.CountAsync());
    }

    /// <summary>Synchronous progress collector (Progress&lt;T&gt; posts to a context, so its order and timing are not fixed).</summary>
    private sealed class CollectingProgress : IProgress<Tick>
    {
        public List<long> Reported { get; } = [];

        public void Report(Tick value) => Reported.Add(value.Value);
    }

    private sealed class CallbackPhase(long cadence, Action<Tick> onRun) : ISimulationPhase
    {
        public string Name => "callback";
        public int Order => 0;
        public long CadenceTicks => cadence;

        public Task ExecuteAsync(SimulationContext ctx, Tick tick)
        {
            onRun(tick);
            return Task.CompletedTask;
        }
    }
}
