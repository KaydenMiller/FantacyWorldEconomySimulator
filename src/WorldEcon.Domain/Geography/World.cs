using ErrorOr;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Currency;
using WorldEcon.SharedKernel.Domain;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Geography;

public sealed class World : AggregateRoot<WorldId>
{
    public string Name { get; private set; }
    public ulong Seed { get; }
    public CalendarDefinition Calendar { get; }
    public CurrencyDefinition Currency { get; private set; }
    public Tick CurrentTick { get; private set; }
    public string RulesetVersion { get; private set; }

    // Market-pricing parameters (spec: supply/demand pricing). See SetPricingParameters for meaning.
    public int ElasticityExponent { get; private set; }
    public int MinPriceMultBp { get; private set; } // floor on price multiplier, in bp
    public int MaxPriceMultBp { get; private set; } // ceiling on price multiplier, in bp

    private const int DefaultElasticityExponent = 1;
    private const int DefaultMinPriceMultBp = 1_000;    // 0.1x
    private const int DefaultMaxPriceMultBp = 100_000;  // 10x

    // Price-discovery belief-update tuning (basis points). How fast a shop's price belief band narrows
    // when its asks sell, and widens / shifts toward the market when they don't. Gentle defaults orbit
    // equilibrium without thrashing; DM-tunable via SetBeliefTuning.
    public long BeliefNarrowFractionBasisPoints { get; private set; }
    public long BeliefWidenFractionBasisPoints { get; private set; }
    public long BeliefShiftFractionBasisPoints { get; private set; }

    private const long DefaultBeliefNarrowFraction = 1_000; // 10%
    private const long DefaultBeliefWidenFraction = 1_000;  // 10%
    private const long DefaultBeliefShiftFraction = 2_000;  // 20%

    // Transport (Layer A). Dimensional-weight haulage cost is computed as
    // max(mass, volume × 1000 / VolumetricDivisor) grams × distance × TransportRate / 1_000_000 copper.
    public long VolumetricDivisor { get; private set; }   // cm³ that bill as one kg of volumetric weight
    public long TransportRate { get; private set; }       // copper per 1000 kg·distance
    public UnitSystem DisplayUnitSystem { get; private set; }

    private const long DefaultVolumetricDivisor = 5_000;  // real-world air-freight number
    private const long DefaultTransportRate = 1;

    // Parameterless ctor for EF Core materialization (sets private/get-only props via backing fields).
    private World() : base(default)
    {
        Name = null!;
        Calendar = null!;
        Currency = null!;
        RulesetVersion = null!;
    }

    private World(WorldId id, string name, ulong seed, CalendarDefinition calendar, Tick currentTick, string rulesetVersion)
        : base(id)
    {
        Name = name;
        Seed = seed;
        Calendar = calendar;
        Currency = CurrencyDefinition.Default;
        CurrentTick = currentTick;
        RulesetVersion = rulesetVersion;
        ElasticityExponent = DefaultElasticityExponent;
        MinPriceMultBp = DefaultMinPriceMultBp;
        MaxPriceMultBp = DefaultMaxPriceMultBp;
        BeliefNarrowFractionBasisPoints = DefaultBeliefNarrowFraction;
        BeliefWidenFractionBasisPoints = DefaultBeliefWidenFraction;
        BeliefShiftFractionBasisPoints = DefaultBeliefShiftFraction;
        VolumetricDivisor = DefaultVolumetricDivisor;
        TransportRate = DefaultTransportRate;
        DisplayUnitSystem = UnitSystem.Metric;
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

    /// <summary>DM tuning for price-discovery belief updates (all in basis points; must be non-negative).</summary>
    public void SetBeliefTuning(long narrowFractionBasisPoints, long widenFractionBasisPoints, long shiftFractionBasisPoints)
    {
        if (narrowFractionBasisPoints < 0 || widenFractionBasisPoints < 0 || shiftFractionBasisPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(narrowFractionBasisPoints), "Belief fractions must be non-negative.");
        BeliefNarrowFractionBasisPoints = narrowFractionBasisPoints;
        BeliefWidenFractionBasisPoints = widenFractionBasisPoints;
        BeliefShiftFractionBasisPoints = shiftFractionBasisPoints;
    }

    /// <summary>DM tuning for haulage cost. Both must be ≥ 1.</summary>
    public void SetTransportTuning(long volumetricDivisor, long transportRate)
    {
        if (volumetricDivisor < 1)
            throw new ArgumentOutOfRangeException(nameof(volumetricDivisor), volumetricDivisor, "Volumetric divisor must be at least 1.");
        if (transportRate < 1)
            throw new ArgumentOutOfRangeException(nameof(transportRate), transportRate, "Transport rate must be at least 1.");
        VolumetricDivisor = volumetricDivisor;
        TransportRate = transportRate;
    }

    /// <summary>Which unit family the UI presents mass/volume in (display-only).</summary>
    public void SetDisplayUnitSystem(UnitSystem system) => DisplayUnitSystem = system;
}
