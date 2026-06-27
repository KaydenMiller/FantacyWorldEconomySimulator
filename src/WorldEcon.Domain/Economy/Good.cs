using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public sealed class Good : AggregateRoot<GoodId>
{
    public WorldId WorldId { get; }
    public string Name { get; private set; }
    public GoodCategory Category { get; private set; }
    public Money BaseValue { get; private set; }
    public string BaseUnit { get; private set; }
    public SizeClass Size { get; private set; }
    public long ShelfLifeTicks { get; private set; } // 0 = imperishable
    public bool Divisible { get; private set; }
    public long ConsumptionPerCapitaBp { get; private set; } // units consumed per capita per day, in bp; 0 = not consumed
    public NeedTier Need { get; private set; }
    // The most a consumer will pay for their first (most-needed) unit, as a multiple of base value in
    // basis points (10000 = 1× base). Willingness declines linearly to 1× base at the desired quantity,
    // so a higher peak = a more inelastic good (keeps buying when scarce). DM-tunable; tier-defaulted.
    public long PeakWillingnessMultipleBasisPoints { get; private set; }
    public Provenance Provenance { get; private set; }

    private Good() : base(default) { Name = null!; BaseUnit = null!; } // EF

    private Good(GoodId id, WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible, long consumptionPerCapitaBp,
        long peakWillingnessMultipleBasisPoints, Provenance provenance, NeedTier need) : base(id)
    {
        WorldId = worldId;
        Name = name;
        Category = category;
        BaseValue = baseValue;
        BaseUnit = baseUnit;
        Size = size;
        ShelfLifeTicks = shelfLifeTicks;
        Divisible = divisible;
        ConsumptionPerCapitaBp = consumptionPerCapitaBp;
        PeakWillingnessMultipleBasisPoints = peakWillingnessMultipleBasisPoints;
        Provenance = provenance;
        Need = need;
    }

    /// <summary>Default peak willingness-to-pay multiple by need tier: essentials are inelastic (people
    /// pay a lot for the first units), comforts are elastic (demand collapses when expensive).</summary>
    public static long DefaultPeakWillingnessForTier(NeedTier tier) => tier switch
    {
        NeedTier.Essential => 40_000, // up to 4× base for the most-needed unit
        NeedTier.Standard => 18_000,  // up to 1.8×
        NeedTier.Comfort => 13_000,   // up to 1.3×
        _ => 15_000,
    };

    public static ErrorOr<Good> Create(WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible, long consumptionPerCapitaBp = 0,
        NeedTier needTier = NeedTier.Essential, long? peakWillingnessMultipleBasisPoints = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("good.name.blank", "Good name must not be blank.");
        if (string.IsNullOrWhiteSpace(baseUnit))
            return Error.Validation("good.baseunit.blank", "Base unit must not be blank.");
        if (baseValue.IsNegative)
            return Error.Validation("good.basevalue.negative", "Base value must not be negative.");
        if (shelfLifeTicks < 0)
            return Error.Validation("good.shelflife.negative", "Shelf life must not be negative.");
        if (consumptionPerCapitaBp < 0)
            return Error.Validation("good.consumption.negative", "Consumption per capita must not be negative.");

        long peak = peakWillingnessMultipleBasisPoints ?? DefaultPeakWillingnessForTier(needTier);
        if (peak < 10_000)
            return Error.Validation("good.peakwillingness.belowbase",
                "Peak willingness multiple must be at least 10000 (1× base value).");

        return new Good(GoodId.New(), worldId, name.Trim(), category, baseValue,
            baseUnit.Trim(), size, shelfLifeTicks, divisible, consumptionPerCapitaBp, peak, Provenance.Authored, needTier);
    }

    /// <summary>DM tuning: set the peak willingness-to-pay multiple (basis points of base value, ≥ 10000).</summary>
    public ErrorOr<Success> SetPeakWillingnessMultiple(long basisPoints)
    {
        if (basisPoints < 10_000)
            return Error.Validation("good.peakwillingness.belowbase",
                "Peak willingness multiple must be at least 10000 (1× base value).");
        PeakWillingnessMultipleBasisPoints = basisPoints;
        return Result.Success;
    }
}
