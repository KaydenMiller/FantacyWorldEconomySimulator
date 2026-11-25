using ErrorOr;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Application.Queries;
using WorldEcon.Application.Simulation;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Seeding;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>The time bar's view model: availability, the date label, jumps, To:, Play and failures.</summary>
public class TimeControlsViewModelTests
{
    private static async Task<T> WithFileWorld<T>(Func<string, MainViewModel, Task<T>> body)
    {
        var db = Path.Combine(Path.GetTempPath(), $"gui_time_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(db, TestWorlds.Settings);
            SqliteConnection.ClearAllPools();
            var vm = new MainViewModel();
            (await vm.OpenAsync(db)).Should().BeTrue(vm.StatusText);
            await vm.CaravansLoaded;
            try
            {
                return await body(db, vm);
            }
            finally
            {
                vm.Session?.Dispose();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                File.Delete(f);
        }
    }

    private static (MainViewModel Vm, WorldSession Session, FakeRun Run) Scripted(long tick = 10_000)
    {
        var session = new WorldSession(TestWorlds.Loaded with { TimeControlsAvailable = true }, path: "stub.db");
        var vm = TestWorlds.MainWithWorld(session: session);
        vm.CaravanQuery = _ => Task.FromResult<ErrorOr<CaravanPositions>>(new CaravanPositions(tick, []));
        session.CurrentTick = tick;
        var run = new FakeRun(tick);
        vm.ReplaceTime(new TimeControlsViewModel(vm, session, _ => Task.FromResult<ErrorOr<ISimulationRun>>(run), null));
        return (vm, session, run);
    }

    [Test]
    public void InMemoryWorld_Unavailable_WithReason()
    {
        var vm = TestWorlds.MainWithWorld();
        vm.Time!.IsAvailable.Should().BeFalse();
        vm.Time.UnavailableReason.Should().Be("Time controls need a world opened from a file.");
    }

    [Test]
    public void FileNotWal_Unavailable_WithReason()
    {
        var vm = TestWorlds.MainWithWorld(session: new WorldSession(TestWorlds.Loaded, path: "stub.db"));
        vm.Time!.IsAvailable.Should().BeFalse();
        vm.Time.UnavailableReason.Should().Be("Time controls need the file in WAL mode; close other programs using it and reopen.");
    }

    [Test]
    public async Task JumpOneDay_UpdatesDateAndCaravansAndCommits()
    {
        await WithFileWorld(async (db, vm) =>
        {
            var time = vm.Time!;
            time.IsAvailable.Should().BeTrue(time.UnavailableReason);
            var start = time.DisplayTick;

            await time.JumpAsync("1d");
            await vm.CaravansLoaded;

            time.DisplayTick.Should().Be(start + 1440);
            time.DateText.Should().Contain($"(tick {start + 1440:N0})");
            vm.Caravans!.Tick.Should().Be(start + 1440);
            vm.Session!.ActiveRun.Should().BeNull();
            (await CaravanPositionsQuery.RunAsync(db)).Value.CurrentTick.Should().Be(start + 1440);
            return 0;
        });
    }

    [Test]
    public async Task DuringPlay_FileActionsDisabled()
    {
        await WithFileWorld(async (db, vm) =>
        {
            var time = vm.Time!;
            await time.TogglePlayAsync();
            time.IsPlaying.Should().BeTrue();
            vm.CanUseFileActions.Should().BeFalse();
            time.Tick(TimeSpan.FromSeconds(1)); // 1 h/s

            await time.TogglePlayAsync();

            time.IsPlaying.Should().BeFalse();
            vm.CanUseFileActions.Should().BeTrue();
            (await CaravanPositionsQuery.RunAsync(db)).Value.CurrentTick.Should().Be(time.DisplayTick);
            return 0;
        });
    }

    [Test]
    public void ToFields_ClampDay()
    {
        var (vm, _, _) = Scripted();
        var time = vm.Time!;
        time.ToDay = 45;
        time.ToDay.Should().Be(time.MonthLength);
        time.ToDay = 0;
        time.ToDay.Should().Be(1);
    }

    [Test]
    public void ToFields_ClampDayOnMonthChange()
    {
        var (vm, _, _) = Scripted();
        var time = vm.Time!;
        time.ToMonthIndex = 0;
        time.ToDay = time.MonthLength;
        var shortest = Enumerable.Range(0, time.MonthNames.Count).MinBy(i => { time.ToMonthIndex = i; return time.MonthLength; });

        time.ToMonthIndex = shortest;

        time.ToDay.Should().BeLessThanOrEqualTo(time.MonthLength);
        time.ToTick.Should().NotBeNull();
    }

    [Test]
    public void Go_DisabledForPastDate_AndBadTime()
    {
        var (vm, _, _) = Scripted();
        var time = vm.Time!;
        time.CanGo.Should().BeFalse("prefilled with the current time");
        time.ToYear += 1;
        time.CanGo.Should().BeTrue();
        time.ToTime = "25:00";
        time.CanGo.Should().BeFalse();
        time.ToTime = "12:5x";
        time.CanGo.Should().BeFalse();
        time.ToTime = "12:30";
        time.CanGo.Should().BeTrue();
        time.ToYear -= 2;
        time.CanGo.Should().BeFalse();
    }

    [Test]
    public async Task Go_AdvancesToTheDate()
    {
        var (vm, _, run) = Scripted();
        var time = vm.Time!;
        time.ToYear += 1;
        var target = time.ToTick!.Value;

        await time.GoAsync();

        run.StepTargets.Should().Equal(target);
        run.EndCalls.Should().Be(1);
        time.DisplayTick.Should().Be(target);
    }

    [Test]
    public async Task FailedRun_ResetsClockAndShowsError()
    {
        var (vm, session, run) = Scripted();
        run.FailNextStep = new InvalidOperationException("boom");
        var time = vm.Time!;

        await time.JumpAsync("1w");

        time.DisplayTick.Should().Be(10_000);
        session.ActiveRun.Should().BeNull();
        vm.ErrorText.Should().Be("Time run failed: boom Nothing was saved.");
        vm.CanUseFileActions.Should().BeTrue();
    }

    [Test]
    public async Task ActiveRun_SetWhileJumping_ClearedAfter()
    {
        var (vm, session, run) = Scripted();
        run.HoldSteps = true;
        var time = vm.Time!;

        var jump = time.JumpAsync("1d");
        await run.StepStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.ActiveRun.Should().BeSameAs(run);
        vm.IsTimeRunning.Should().BeTrue();
        time.ShowsJumpControls.Should().BeFalse();

        run.ReleaseStep();
        await jump.WaitAsync(TimeSpan.FromSeconds(10));
        session.ActiveRun.Should().BeNull();
        time.ShowsJumpControls.Should().BeTrue();
    }
}
