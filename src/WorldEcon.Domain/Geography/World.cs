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
    // max(mass, volume × 1000 / VolumetricDivisor) grams × metres × TransportRate / 1_000_000_000 copper.
    public long VolumetricDivisor { get; private set; }   // cm³ that bill as one kg of volumetric weight
    public long TransportRate { get; private set; }       // copper per 1000 kg·km

    // Base travel speed per route surface, metres per in-world day. A vehicle's speed factor and a route's direction
    // factor and difficulty scale these (TravelTime.Ticks).
    public long TrackSpeedMpd { get; private set; }
    public long DirtRoadSpeedMpd { get; private set; }
    public long PavedSpeedMpd { get; private set; }
    public long OpenWaterSpeedMpd { get; private set; }
    public long SkySpeedMpd { get; private set; }

    public const long DefaultTrackSpeedMpd = 20_000;
    public const long DefaultDirtRoadSpeedMpd = 30_000;
    public const long DefaultPavedSpeedMpd = 40_000;
    public const long DefaultOpenWaterSpeedMpd = 100_000;
    public const long DefaultSkySpeedMpd = 150_000;

    // Trade knowledge and risk (ports and travel knowledge design).
    /// <summary>Largest chance, %, that a merchant with risk aversion 0 ignores the best trip and gambles.</summary>
    public int MaxExplorePct { get; private set; }

    /// <summary>Largest expected loss, % of trip cost, a merchant with risk aversion 0 will still gamble on.</summary>
    public int MaxLossMarginPct { get; private set; }

    /// <summary>Settlements get estimated travel times for destinations within this many ticks of travel.</summary>
    public long KnowledgeRadiusTicks { get; private set; }

    public const int DefaultMaxExplorePct = 20;
    public const int DefaultMaxLossMarginPct = 50;
    public const long DefaultKnowledgeRadiusTicks = 172_800; // 120 days
    public UnitSystem DisplayUnitSystem { get; private set; }

    private const long DefaultVolumetricDivisor = 5_000;  // real-world air-freight number
    private const long DefaultTransportRate = 1;

    // Globe (world generation). WorldGenVersion 0 = not generated yet.
    public int GridFrequency { get; private set; }
    public int PlanetRadiusKm { get; private set; }
    public int WorldGenVersion { get; private set; }

    /// <summary>Cells with elevation strictly below this value (metres) are ocean. Set by generation; 0 until then.</summary>
    public int SeaLevelM { get; private set; }

    private const int DefaultGridFrequency = 64;
    private const int DefaultPlanetRadiusKm = 6_371;

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
        TrackSpeedMpd = DefaultTrackSpeedMpd;
        DirtRoadSpeedMpd = DefaultDirtRoadSpeedMpd;
        PavedSpeedMpd = DefaultPavedSpeedMpd;
        OpenWaterSpeedMpd = DefaultOpenWaterSpeedMpd;
        SkySpeedMpd = DefaultSkySpeedMpd;
        MaxExplorePct = DefaultMaxExplorePct;
        MaxLossMarginPct = DefaultMaxLossMarginPct;
        KnowledgeRadiusTicks = DefaultKnowledgeRadiusTicks;
        DisplayUnitSystem = UnitSystem.Metric;
        GridFrequency = DefaultGridFrequency;
        PlanetRadiusKm = DefaultPlanetRadiusKm;
        WorldGenVersion = 0;
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

    /// <summary>Base speed of <paramref name="surface"/> in metres per day.</summary>
    public long SurfaceSpeed(RouteSurface surface) => surface switch
    {
        RouteSurface.Track => TrackSpeedMpd,
        RouteSurface.DirtRoad => DirtRoadSpeedMpd,
        RouteSurface.Paved => PavedSpeedMpd,
        RouteSurface.OpenWater => OpenWaterSpeedMpd,
        RouteSurface.Sky => SkySpeedMpd,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
    };

    /// <summary>DM tuning: base speed per surface in metres per day. Each must be positive.</summary>
    public ErrorOr<Success> SetSurfaceSpeeds(long track, long dirtRoad, long paved, long openWater, long sky)
    {
        if (track < 1 || dirtRoad < 1 || paved < 1 || openWater < 1 || sky < 1)
            return Error.Validation("world.surfacespeed.nonpositive", "Every surface speed must be positive.");
        TrackSpeedMpd = track;
        DirtRoadSpeedMpd = dirtRoad;
        PavedSpeedMpd = paved;
        OpenWaterSpeedMpd = openWater;
        SkySpeedMpd = sky;
        return Result.Success;
    }

    /// <summary>
    /// DM tuning for merchant gambles and knowledge: explore chance 0–100 %, loss limit 0–1,000 % of trip cost, knowledge
    /// radius 1,440–10,000,000 ticks.
    /// </summary>
    public ErrorOr<Success> SetTradeRisk(int maxExplorePct, int maxLossMarginPct, long knowledgeRadiusTicks)
    {
        if (maxExplorePct is < 0 or > 100)
            return Error.Validation("world.trade.explore", "Explore chance must be between 0 and 100 %.");
        if (maxLossMarginPct is < 0 or > 1_000)
            return Error.Validation("world.trade.lossMargin", "Loss margin must be between 0 and 1,000 %.");
        if (knowledgeRadiusTicks is < 1_440 or > 10_000_000)
            return Error.Validation("world.trade.knowledgeRadius", "Knowledge radius must be between 1,440 and 10,000,000 ticks.");
        MaxExplorePct = maxExplorePct;
        MaxLossMarginPct = maxLossMarginPct;
        KnowledgeRadiusTicks = knowledgeRadiusTicks;
        return Result.Success;
    }

    /// <summary>Which unit family the UI presents mass/volume in (display-only).</summary>
    public void SetDisplayUnitSystem(UnitSystem system) => DisplayUnitSystem = system;

    /// <summary>
    /// Records the globe parameters used by generation. Frequency and radius must be positive; version must not be negative.
    /// Once a globe is recorded (<see cref="WorldGenVersion"/> &gt; 0) a further call is a <c>world.globe.generated</c> error:
    /// regeneration is out of scope.
    /// </summary>
    public ErrorOr<Success> SetGlobe(int gridFrequency, int planetRadiusKm, int worldGenVersion)
    {
        if (gridFrequency < 1)
            return Error.Validation("world.globe.frequency", "Grid frequency must be at least 1.");
        if (planetRadiusKm < 1)
            return Error.Validation("world.globe.radius", "Planet radius must be positive.");
        if (worldGenVersion < 0)
            return Error.Validation("world.globe.version", "World generation version must not be negative.");
        if (WorldGenVersion > 0)
            return Error.Validation("world.globe.generated", $"World already has a generated globe (generator version {WorldGenVersion}); regeneration is not supported.");
        GridFrequency = gridFrequency;
        PlanetRadiusKm = planetRadiusKm;
        WorldGenVersion = worldGenVersion;
        return Result.Success;
    }

    /// <summary>Records the sea level chosen by generation, in metres. Any value is valid (it may be negative).</summary>
    public void SetSeaLevel(int seaLevelM) => SeaLevelM = seaLevelM;
}
