using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Application.Queries;
using WorldEcon.Application.Simulation;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.TestFixtures;

namespace WorldEcon.Application.Tests.Unit;

public class SimulationRunnerTests
{
    private const long Now = EconomyTestWorld.Now;

    private static async Task<SimulationRunner> OpenAsync(string path, IEnumerable<ISimulationPhase>? phases = null)
    {
        (await WorldFileJournal.TryEnableWalAsync(path)).Should().BeTrue();
        var opened = phases is null ? await SimulationRunner.OpenAsync(path) : await SimulationRunner.OpenAsync(path, phases);
        opened.IsError.Should().BeFalse(opened.IsError ? opened.FirstError.Description : "");
        return opened.Value;
    }

    [Test]
    public async Task Open_DeleteModeFile_ReturnsNotWal()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using (var conn = new SqliteConnection($"Data Source={w.Path};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            await cmd.ExecuteScalarAsync();
        }

        var opened = await SimulationRunner.OpenAsync(w.Path);

        opened.IsError.Should().BeTrue();
        opened.FirstError.Code.Should().Be("Runner.NotWal");
    }

    [Test]
    public async Task Open_MissingFile_ReturnsNoWorld()
    {
        var opened = await SimulationRunner.OpenAsync(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.db"));
        opened.FirstError.Code.Should().Be("Runner.NoWorld");
    }

    [Test]
    public async Task Step_TargetNotAfterNow_ReturnsValidation()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using var runner = await OpenAsync(w.Path);

        var step = await runner.StepToAsync(Now);

        step.IsError.Should().BeTrue();
        step.FirstError.Code.Should().Be("Runner.TargetNotAfterNow");
        step.FirstError.Type.Should().Be(ErrorOr.ErrorType.Validation);
    }

    [Test]
    public async Task DuringRun_OtherConnectionSeesOldTick_RunnerSeesNew()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using var runner = await OpenAsync(w.Path);

        var step = await runner.StepToAsync(Now + 1440);

        step.Value.Tick.Should().Be(Now + 1440);
        runner.CurrentTick.Should().Be(Now + 1440);
        (await CaravanPositionsQuery.RunAsync(w.Path)).Value.CurrentTick.Should().Be(Now);
        (await runner.CaravanPositionsAsync()).Value.CurrentTick.Should().Be(Now + 1440);
        (await runner.SettlementDetailsAsync(w.Ashford.Id.Value)).Value.Header.ReadTick.Should().Be(Now + 1440);
        (await runner.SettlementListAsync()).Value.Select(s => s.Name).Should().Contain(["Ashford", "Brindle", "Corran"]);
    }

    [Test]
    public async Task End_Commits()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using (var runner = await OpenAsync(w.Path))
        {
            await runner.StepToAsync(Now + 1440);
            await runner.EndAsync();
        }

        (await CaravanPositionsQuery.RunAsync(w.Path)).Value.CurrentTick.Should().Be(Now + 1440);
    }

    [Test]
    public async Task Dispose_WithoutEnd_RollsBack()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using (var runner = await OpenAsync(w.Path))
        {
            await runner.StepToAsync(Now + 1440);
        }

        (await CaravanPositionsQuery.RunAsync(w.Path)).Value.CurrentTick.Should().Be(Now);
    }

    [Test]
    public async Task OverlappingSteps_Throw()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var phase = new GatedPhase();
        await using var runner = await OpenAsync(w.Path, [phase]);

        var first = runner.StepToAsync(runner.NextBoundaryTick);
        await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = async () => await runner.StepToAsync(runner.NextBoundaryTick + 10);

        await second.Should().ThrowAsync<InvalidOperationException>();
        phase.Release.SetResult();
        (await first).IsError.Should().BeFalse();
    }

    [Test]
    public async Task Step_ReportsPhasesRan()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using var runner = await OpenAsync(w.Path);

        var boundary = runner.NextBoundaryTick;
        boundary.Should().BeGreaterThan(Now);
        (await runner.StepToAsync(boundary)).Value.PhasesRan.Should().BeTrue();
        (await runner.StepToAsync(boundary + 10)).Value.PhasesRan.Should().BeFalse();
    }

    [Test]
    public async Task Step_ReportsProgressPerBoundary()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using var runner = await OpenAsync(w.Path, [new GatedPhase(open: true)]);
        var reported = new List<long>();

        await runner.StepToAsync(runner.NextBoundaryTick + 1440, new SyncProgress(reported.Add));

        reported.Should().HaveCount(2);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>A daily phase that waits for <see cref="Release"/> (or runs straight through when open).</summary>
    private sealed class GatedPhase(bool open = false) : ISimulationPhase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "gated";
        public int Order => 0;
        public long CadenceTicks => Tick.DefaultMinutesPerDay;

        public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
        {
            if (open) return;
            Entered.TrySetResult();
            await Release.Task;
        }
    }
}
