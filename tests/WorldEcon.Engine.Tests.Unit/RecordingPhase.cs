using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

/// <summary>Test double phase: counts executions and records the ticks it ran at.</summary>
internal sealed class RecordingPhase : ISimulationPhase
{
    private readonly List<string>? _sharedOrderLog;

    public RecordingPhase(string name, int order, long cadenceTicks, List<string>? sharedOrderLog = null)
    {
        Name = name;
        Order = order;
        CadenceTicks = cadenceTicks;
        _sharedOrderLog = sharedOrderLog;
    }

    public string Name { get; }
    public int Order { get; }
    public long CadenceTicks { get; }

    public int Count { get; private set; }
    public List<long> RanAtTicks { get; } = new();

    public Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        Count++;
        RanAtTicks.Add(tick.Value);
        _sharedOrderLog?.Add(Name);
        return Task.CompletedTask;
    }
}
