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

    // Market-pricing parameters (spec: supply/demand pricing). See SetPricingParameters for meaning.
    public int ElasticityExponent { get; private set; }
    public int MinPriceMultBp { get; private set; } // floor on price multiplier, in bp
    public int MaxPriceMultBp { get; private set; } // ceiling on price multiplier, in bp

    private const int DefaultElasticityExponent = 1;
    private const int DefaultMinPriceMultBp = 1_000;    // 0.1x
    private const int DefaultMaxPriceMultBp = 100_000;  // 10x

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
        ElasticityExponent = DefaultElasticityExponent;
        MinPriceMultBp = DefaultMinPriceMultBp;
        MaxPriceMultBp = DefaultMaxPriceMultBp;
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

    /// <summary>
    /// Configures supply/demand pricing: <paramref name="elasticityExponent"/> raises the scarcity
    /// ratio to that integer power; the resulting multiplier (bp) is clamped to
    /// [<paramref name="minMultBp"/>, <paramref name="maxMultBp"/>].
    /// </summary>
    public void SetPricingParameters(int elasticityExponent, int minMultBp, int maxMultBp)
    {
        if (elasticityExponent < 0)
            throw new ArgumentOutOfRangeException(nameof(elasticityExponent), elasticityExponent,
                "Elasticity exponent must not be negative.");
        if (minMultBp <= 0)
            throw new ArgumentOutOfRangeException(nameof(minMultBp), minMultBp,
                "Minimum price multiplier must be positive.");
        if (minMultBp > maxMultBp)
            throw new ArgumentOutOfRangeException(nameof(maxMultBp), maxMultBp,
                "Maximum price multiplier must be at least the minimum.");

        ElasticityExponent = elasticityExponent;
        MinPriceMultBp = minMultBp;
        MaxPriceMultBp = maxMultBp;
    }
}
