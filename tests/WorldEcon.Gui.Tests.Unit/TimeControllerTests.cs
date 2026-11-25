using ErrorOr;
using FluentAssertions;
using WorldEcon.Application.Simulation;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>The play-loop rules: pacing, holding at boundaries, Pause, Max throttling, jumps and failures.</summary>
public class TimeControllerTests
{
    private const long Start = 10_000; // not on a day boundary (next boundary 10,080)
    private static readonly TimeSpan Tenth = TimeSpan.FromMilliseconds(100);

    private static (TimeController Time, FakeRun Run) New(long start = Start, Func<long>? nowMs = null, long boundaryEvery = 1440)
    {
        var run = new FakeRun(start, boundaryEvery);
        var time = new TimeController(start, _ => Task.FromResult<ErrorOr<ISimulationRun>>(run), nowMs);
        return (time, run);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue();
    }

    [Test]
    public async Task Update_AdvancesBySpeed_CarriesFraction()
    {
        var (time, _) = New();
        time.Speed = PlaySpeed.OneMinute;
        await time.PlayAsync();

        for (var i = 0; i < 15; i++) time.Update(Tenth);
        time.DisplayTick.Should().Be(Start + 1);
        for (var i = 0; i < 5; i++) time.Update(Tenth);
        time.DisplayTick.Should().Be(Start + 2);
    }

    [Test]
    public async Task Update_HoldsAtBoundary_UntilStepCompletes()
    {
        var (time, run) = New(start: 1440 - 60);
        run.HoldSteps = true;
        time.Speed = PlaySpeed.OneDay;
        await time.PlayAsync();

        time.Update(TimeSpan.FromSeconds(1));
        time.DisplayTick.Should().Be(1440);
        await run.StepStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        run.StepTargets.Should().Equal(1440);

        time.Update(TimeSpan.FromSeconds(1));
        time.DisplayTick.Should().Be(1440);
        run.StepTargets.Should().HaveCount(1);

        run.ReleaseStep();
        await WaitUntil(() => run.CurrentTick == 1440);
        await Task.Delay(20);
        time.Update(TimeSpan.FromSeconds(0.5));
        time.DisplayTick.Should().Be(1440 + 720);
    }

    [Test]
    public async Task PhasesRan_RaisesDataStepped()
    {
        var (time, run) = New(start: 1440 - 60);
        var data = 0;
        time.DataStepped += (_, _) => data++;
        time.Speed = PlaySpeed.OneDay;
        await time.PlayAsync();

        time.Update(TimeSpan.FromSeconds(1));
        await WaitUntil(() => data == 1);
    }

    [Test]
    public async Task SpeedChange_AppliesNextUpdate()
    {
        var (time, _) = New();
        time.Speed = PlaySpeed.OneMinute;
        await time.PlayAsync();
        time.Update(TimeSpan.FromSeconds(1));
        time.DisplayTick.Should().Be(Start + 1);

        time.Speed = PlaySpeed.OneHour;
        time.Update(TimeSpan.FromSeconds(1));
        time.DisplayTick.Should().Be(Start + 61);
    }

    [Test]
    public async Task Pause_StepsToDisplayTick_ThenEnds()
    {
        var (time, run) = New();
        time.Speed = PlaySpeed.OneHour;
        await time.PlayAsync();
        time.Update(TimeSpan.FromSeconds(1));

        await time.PauseAsync();

        run.StepTargets.Should().Equal(Start + 60);
        run.EndCalls.Should().Be(1);
        time.IsRunActive.Should().BeFalse();
        time.FileTick.Should().Be(Start + 60);
        time.DisplayTick.Should().Be(Start + 60);
    }

    [Test]
    public async Task Pause_AtRunStartTick_EndsWithoutStep()
    {
        var (time, run) = New();
        await time.PlayAsync();

        await time.PauseAsync();

        run.StepTargets.Should().BeEmpty();
        run.EndCalls.Should().Be(1);
        time.IsRunActive.Should().BeFalse();
    }

    [Test]
    public async Task Pause_DuringStep_WaitsThenEndsOnce()
    {
        var (time, run) = New(start: 1440 - 60);
        run.HoldSteps = true;
        time.Speed = PlaySpeed.OneDay;
        await time.PlayAsync();
        time.Update(TimeSpan.FromSeconds(1));
        await run.StepStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var pause = time.PauseAsync();
        pause.IsCompleted.Should().BeFalse();
        run.ReleaseStep();
        await pause.WaitAsync(TimeSpan.FromSeconds(10));

        run.EndCalls.Should().Be(1);
        run.StepTargets.Should().Equal(1440);
        time.FileTick.Should().Be(1440);
    }

    [Test]
    public async Task Max_ThrottlesDataStepped()
    {
        long now = 0;
        var (time, run) = New(nowMs: () => now += 50 / 2); // each step reads the clock about twice
        var data = 0;
        time.DataStepped += (_, _) => data++;
        time.Speed = PlaySpeed.Max;
        await time.PlayAsync();

        await WaitUntil(() => run.StepTargets.Count >= 20);
        await time.PauseAsync();

        var steps = run.StepTargets.Count;
        data.Should().BeGreaterThan(0);
        data.Should().BeLessThan(steps, "Max re-reads at most once per 250 ms");
        data.Should().BeLessThanOrEqualTo((int)(now / TimeController.DataThrottleMs) + 2);
        run.EndCalls.Should().Be(1);
    }

    [Test]
    public async Task Jump_WhilePlaying_IsConflict()
    {
        var (time, _) = New();
        await time.PlayAsync();

        var jump = await time.JumpAsync(1440);

        jump.FirstError.Type.Should().Be(ErrorType.Conflict);
    }

    [Test]
    public async Task AdvanceTo_NotAfterNow_IsValidation()
    {
        var (time, run) = New();

        var result = await time.AdvanceToAsync(Start);

        result.FirstError.Type.Should().Be(ErrorType.Validation);
        run.StepTargets.Should().BeEmpty();
    }

    [Test]
    public async Task Jump_StepsToTarget_AndCommits()
    {
        var (time, run) = New();
        var runs = new List<ISimulationRun?>();
        time.RunChanged += (_, r) => runs.Add(r);

        (await time.JumpAsync(7 * 1440)).IsError.Should().BeFalse();

        run.StepTargets.Should().Equal(Start + 7 * 1440);
        run.EndCalls.Should().Be(1);
        time.DisplayTick.Should().Be(Start + 7 * 1440);
        runs.Should().HaveCount(2);
        runs[1].Should().BeNull();
    }

    [Test]
    public async Task StopJump_CommitsAtLastBoundary()
    {
        var (time, run) = New();
        run.BoundaryDelay = TimeSpan.FromMilliseconds(5);
        var jump = time.JumpAsync(365 * 1440);
        time.IsJumping.Should().BeTrue();
        await WaitUntil(() => run.CurrentTick >= Start + 3 * 1440);

        time.StopJump();
        (await jump.WaitAsync(TimeSpan.FromSeconds(10))).IsError.Should().BeFalse();

        run.EndCalls.Should().Be(1);
        time.IsRunActive.Should().BeFalse();
        time.IsJumping.Should().BeFalse();
        (run.CurrentTick % 1440).Should().Be(0);
        run.CurrentTick.Should().BeLessThan(Start + 365 * 1440);
        time.FileTick.Should().Be(run.CurrentTick);
    }

    [Test]
    public async Task StepError_ResetsClockAndRaisesRunFailed()
    {
        var (time, run) = New();
        run.FailNextStep = new InvalidOperationException("boom");
        string? failed = null;
        time.RunFailed += (_, m) => failed = m;

        await time.JumpAsync(3 * 1440);

        failed.Should().Be("boom");
        time.DisplayTick.Should().Be(Start);
        time.IsRunActive.Should().BeFalse();
        run.EndCalls.Should().Be(0);
        run.DisposeCalls.Should().Be(1);
    }

    [Test]
    public async Task OpenFailure_ReturnsError_NoRun()
    {
        var time = new TimeController(Start, _ => Task.FromResult<ErrorOr<ISimulationRun>>(SimulationRunner.Errors.NotWal));

        var play = await time.PlayAsync();

        play.FirstError.Code.Should().Be("Runner.NotWal");
        time.IsPlaying.Should().BeFalse();
        time.IsRunActive.Should().BeFalse();
    }

    [Test]
    public async Task EndAnyRun_DuringJump_StopsAndCommitsOnce()
    {
        var (time, run) = New();
        run.BoundaryDelay = TimeSpan.FromMilliseconds(5);
        var jump = time.JumpAsync(365 * 1440);
        await WaitUntil(() => run.CurrentTick >= Start + 2 * 1440);

        await time.EndAnyRunAsync();

        jump.IsCompleted.Should().BeTrue();
        run.EndCalls.Should().Be(1);
        time.IsRunActive.Should().BeFalse();
    }

    [Test]
    public async Task Jump_DoesNotRaiseDataStepped()
    {
        long now = 0;
        var (time, run) = New(nowMs: () => now += 300);
        run.BoundaryDelay = TimeSpan.FromMilliseconds(2);
        var data = 0;
        time.DataStepped += (_, _) => data++;

        await time.JumpAsync(30 * 1440);

        data.Should().Be(0, "the map and panels re-read once, when the jump has committed");
        run.EndCalls.Should().Be(1);
    }

    [Test]
    public async Task MaxToPaced_WhileMaxStepRuns_NoSecondStep()
    {
        var (time, run) = New();
        run.HoldSteps = true;
        time.Speed = PlaySpeed.Max;
        await time.PlayAsync();
        await run.StepStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        time.Speed = PlaySpeed.OneDay;
        time.Update(TimeSpan.FromSeconds(5));

        run.StepTargets.Should().HaveCount(1, "the Max loop's step is still running");
        run.HoldSteps = false;
        run.ReleaseStep();
        await time.PauseAsync();
        run.EndCalls.Should().Be(1);
        time.IsRunActive.Should().BeFalse();
    }

    [Test]
    public async Task EndAnyRun_DuringPauseCommit_EndsOnce()
    {
        var (time, run) = New();
        run.HoldEnd = true;
        string? failed = null;
        time.RunFailed += (_, m) => failed = m;
        await time.PlayAsync();

        var pause = time.PauseAsync();
        await run.EndStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = time.EndAnyRunAsync();
        run.ReleaseEnd();
        await Task.WhenAll(pause, close).WaitAsync(TimeSpan.FromSeconds(10));

        run.EndCalls.Should().Be(1);
        failed.Should().BeNull();
        time.IsRunActive.Should().BeFalse();
    }

    [Test]
    public async Task DisposeFailure_AfterCommit_StillClosesTheRun()
    {
        var (time, run) = New();
        run.DisposeThrows = true;
        var runs = new List<ISimulationRun?>();
        time.RunChanged += (_, r) => runs.Add(r);

        await time.JumpAsync(1440);

        runs.Should().HaveCount(2);
        runs[1].Should().BeNull();
        time.IsRunActive.Should().BeFalse();
        time.FileTick.Should().Be(Start + 1440);
    }
}
