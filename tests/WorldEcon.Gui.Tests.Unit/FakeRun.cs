using ErrorOr;
using WorldEcon.Application.Queries;
using WorldEcon.Application.Simulation;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>
/// A scripted <see cref="ISimulationRun"/>: reads return fixed results; steps complete at once unless <see cref="HoldSteps"/>
/// is set (then each waits for <see cref="ReleaseStep"/>); counts steps and ends.
/// </summary>
internal sealed class FakeRun : ISimulationRun
{
    private TaskCompletionSource _stepGate = NewGate();

    public FakeRun(long tick, long boundaryEvery = 1440)
    {
        CurrentTick = tick;
        BoundaryEvery = boundaryEvery;
    }

    public long BoundaryEvery { get; }
    public long CurrentTick { get; set; }
    public long NextBoundaryTick => (CurrentTick / BoundaryEvery + 1) * BoundaryEvery;

    public bool HoldSteps { get; set; }

    /// <summary>Real time per boundary inside a step (lets a test stop a long jump part-way).</summary>
    public TimeSpan BoundaryDelay { get; set; }
    public List<long> StepTargets { get; } = [];
    public int EndCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool Ended => EndCalls > 0;

    /// <summary>When set, the next step fails with this exception.</summary>
    public Exception? FailNextStep { get; set; }

    /// <summary>When set, the next step returns this error.</summary>
    public Error? ErrorNextStep { get; set; }

    public TaskCompletionSource StepStarted { get; private set; } = NewGate();

    public CaravanPositions Caravans { get; set; } = new(99_999, []);
    public Func<Guid, SettlementDetails>? Details { get; set; }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseStep() => _stepGate.TrySetResult();

    public async Task<ErrorOr<RunStep>> StepToAsync(long tick, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        StepTargets.Add(tick);
        StepStarted.TrySetResult();
        if (HoldSteps)
        {
            await _stepGate.Task;
            _stepGate = NewGate();
        }

        StepStarted = NewGate();
        if (FailNextStep is { } ex)
        {
            FailNextStep = null;
            throw ex;
        }

        if (ErrorNextStep is { } err)
        {
            ErrorNextStep = null;
            return err;
        }

        if (tick <= CurrentTick) return Error.Validation("Runner.TargetNotAfterNow", "The target time must be after the current time.");
        var phases = false;
        while (CurrentTick < tick && !ct.IsCancellationRequested)
        {
            var next = Math.Min(NextBoundaryTick, tick);
            phases |= next % BoundaryEvery == 0;
            CurrentTick = next;
            progress?.Report(next);
            if (BoundaryDelay > TimeSpan.Zero) await Task.Delay(BoundaryDelay);
            else await Task.Yield();
        }

        return new RunStep(CurrentTick, phases);
    }

    /// <summary>When set, EndAsync waits for <see cref="ReleaseEnd"/>.</summary>
    public bool HoldEnd { get; set; }

    public TaskCompletionSource EndStarted { get; } = NewGate();

    private readonly TaskCompletionSource _endGate = NewGate();

    public void ReleaseEnd() => _endGate.TrySetResult();

    /// <summary>When set, DisposeAsync throws after counting.</summary>
    public bool DisposeThrows { get; set; }

    public async Task EndAsync()
    {
        EndCalls++;
        EndStarted.TrySetResult();
        if (HoldEnd) await _endGate.Task;
    }

    public Task<ErrorOr<CaravanPositions>> CaravanPositionsAsync(CancellationToken ct = default)
        => Task.FromResult<ErrorOr<CaravanPositions>>(Caravans with { CurrentTick = Caravans.CurrentTick });

    public Task<ErrorOr<SettlementDetails>> SettlementDetailsAsync(Guid settlementId, CancellationToken ct = default)
        => Task.FromResult<ErrorOr<SettlementDetails>>(Details is { } d ? d(settlementId) : RunTick(CityPanelTests.Sample(settlementId)));

    public Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>> SettlementListAsync(CancellationToken ct = default)
        => Task.FromResult<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>>(new List<(Guid, string)>());

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        if (DisposeThrows) throw new InvalidOperationException("dispose failed");
        return ValueTask.CompletedTask;
    }

    private SettlementDetails RunTick(SettlementDetails d) => d with { Header = d.Header with { ReadTick = Caravans.CurrentTick } };
}
