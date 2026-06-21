using ErrorOr;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class World : AggregateRoot<WorldId>
{
    public string Name { get; private set; }
    public ulong Seed { get; }
    public CalendarDefinition Calendar { get; }
    public Tick CurrentTick { get; private set; }
    public string RulesetVersion { get; private set; }

    // Parameterless ctor for EF Core materialization (sets private/get-only props via backing fields).
    private World() : base(default)
    {
        Name = null!;
        Calendar = null!;
        RulesetVersion = null!;
    }

    private World(WorldId id, string name, ulong seed, CalendarDefinition calendar, Tick currentTick, string rulesetVersion)
        : base(id)
    {
        Name = name;
        Seed = seed;
        Calendar = calendar;
        CurrentTick = currentTick;
        RulesetVersion = rulesetVersion;
    }

    public static ErrorOr<World> Create(string name, ulong seed, CalendarDefinition calendar, string rulesetVersion)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("world.name.blank", "World name must not be blank.");
        if (string.IsNullOrWhiteSpace(rulesetVersion))
            return Error.Validation("world.ruleset.blank", "Ruleset version must not be blank.");

        return new World(WorldId.New(), name.Trim(), seed, calendar, Tick.Zero, rulesetVersion);
    }

    /// <summary>Advances in-world time to <paramref name="tick"/>. Time must not go backwards.</summary>
    public void AdvanceTo(Tick tick)
    {
        if (tick.Value < CurrentTick.Value)
            throw new ArgumentOutOfRangeException(
                nameof(tick),
                tick.Value,
                $"Cannot advance to tick {tick.Value}: current tick is {CurrentTick.Value} (time must not go backwards).");

        CurrentTick = tick;
    }
}
