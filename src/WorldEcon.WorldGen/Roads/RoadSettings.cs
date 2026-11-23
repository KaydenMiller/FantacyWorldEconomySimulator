using ErrorOr;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Roads;

/// <summary>Medium a generated route crosses. Mirrors the Domain's RouteCategory (WorldGen does not reference Domain).
/// Sea routes are voyages between ports (<see cref="VoyageGenerator"/>), never authored roads.</summary>
public enum RoadCategory { Land = 0, Sea = 1, Air = 2 }

/// <summary>What water a settlement's tile touches: none, river only, or lake/ocean. Mirrors the Domain's PortKind.</summary>
public enum PortKind { None = 0, River = 1, Coastal = 2 }

/// <summary>What a network node stands for. Mirrors the Domain's RouteNodeKind.</summary>
public enum RoadNodeKind { Settlement = 0, Junction = 1, Site = 2 }

/// <summary>
/// Tunable numbers of road generation: the <c>Roads</c> section of <see cref="WorldGenSettings"/> (seed
/// <c>generation.Roads</c>, <c>new --generation</c>, stored with the world's generation run). All integers; percentages are
/// of 100. Roads are generated after pinning, outside the staged pipeline, so no stage hash depends on these values.
/// </summary>
public sealed record RoadSettings
{
    /// <summary>Cost of one land step on plain terrain before the landform factor. 1–10,000.</summary>
    public int BaseStepCost { get; init; } = 100;
    public int PlainFactorPct { get; init; } = 100;
    public int HillsFactorPct { get; init; } = 150;
    public int MountainFactorPct { get; init; } = 400;
    public int ValleyFactorPct { get; init; } = 110;
    public int BasinFactorPct { get; init; } = 110;
    public int WetlandFactorPct { get; init; } = 250;
    public int DryLakeBedFactorPct { get; init; } = 130;

    /// <summary>Added step cost per metre of elevation change. 0–1,000.</summary>
    public int SlopeCostPerM { get; init; } = 2;

    /// <summary>Added cost for stepping onto a river tile, per log2 of its flow (a bridge or ford). 0–1,000.</summary>
    public int RiverCostPerLog2Flow { get; init; } = 10;

    /// <summary>Search cost of a step on a tile an earlier route of the same category already uses, in %. 1–100.</summary>
    public int RoadDiscountPct { get; init; } = 60;

    /// <summary>Direction factor lost per metre climbed per km. 0–100.</summary>
    public int UphillPenalty { get; init; } = 1;

    /// <summary>Direction factor gained per metre descended per km. 0–100.</summary>
    public int DownhillBonus { get; init; } = 1;
    public int MinDirectionFactor { get; init; } = 25;
    public int MaxDirectionFactor { get; init; } = 150;

    /// <summary>A land step steeper than this (metres per km) limits the route to tier 1 vehicles. 1–10,000.</summary>
    public int SteepSlopeMPerKm { get; init; } = 150;

    /// <summary>Highest vehicle tier a route allows when nothing limits it. 1–99.</summary>
    public int DefaultMaxTier { get; init; } = 99;

    /// <summary>Highest vehicle tier a voyage allows when its path crosses lake water (small craft only). 1–99.</summary>
    public int LakeMaxTier { get; init; } = 1;

    /// <summary>Speed factor of a voyage's river step with the flow, %. 1–1,000.</summary>
    public int RiverDownstreamPct { get; init; } = 130;

    /// <summary>Speed factor of a voyage's river step against the flow, %. 1–1,000.</summary>
    public int RiverUpstreamPct { get; init; } = 60;

    /// <summary>Highest vehicle tier a voyage allows when its path uses a river. 1–99.</summary>
    public int RiverMaxTier { get; init; } = 1;

    /// <summary>Most ports a world may have; voyages grow with the square of the port count. 0–100,000.</summary>
    public int MaxVoyagePorts { get; init; } = 2_000;

    /// <summary>
    /// Longest voyage generated, km of water path; ports farther apart get none (at 8,000 km a merchant vessel needs about
    /// 67 days, beyond the default 60-day merchant reach). Bounds the search work. 1–40,000.
    /// </summary>
    public int MaxVoyageKm { get; init; } = 8_000;

    /// <summary>Range checks of every property (codes <c>settings.Roads.&lt;Name&gt;</c>).</summary>
    public ErrorOr<Success> Validate()
    {
        var v = new SettingsValidator();
        Validate(v.For("Roads"));
        return v.Errors.Count == 0 ? Result.Success : v.Errors.ToList();
    }

    internal void Validate(SectionValidator v)
    {
        v.Range(nameof(BaseStepCost), BaseStepCost, 1, 10_000);
        v.Range(nameof(PlainFactorPct), PlainFactorPct, 1, 10_000);
        v.Range(nameof(HillsFactorPct), HillsFactorPct, 1, 10_000);
        v.Range(nameof(MountainFactorPct), MountainFactorPct, 1, 10_000);
        v.Range(nameof(ValleyFactorPct), ValleyFactorPct, 1, 10_000);
        v.Range(nameof(BasinFactorPct), BasinFactorPct, 1, 10_000);
        v.Range(nameof(WetlandFactorPct), WetlandFactorPct, 1, 10_000);
        v.Range(nameof(DryLakeBedFactorPct), DryLakeBedFactorPct, 1, 10_000);
        v.Range(nameof(SlopeCostPerM), SlopeCostPerM, 0, 1_000);
        v.Range(nameof(RiverCostPerLog2Flow), RiverCostPerLog2Flow, 0, 1_000);
        v.Range(nameof(RoadDiscountPct), RoadDiscountPct, 1, 100);
        v.Range(nameof(UphillPenalty), UphillPenalty, 0, 100);
        v.Range(nameof(DownhillBonus), DownhillBonus, 0, 100);
        v.Range(nameof(MinDirectionFactor), MinDirectionFactor, 1, 100);
        v.Range(nameof(MaxDirectionFactor), MaxDirectionFactor, 100, 1_000);
        v.Range(nameof(SteepSlopeMPerKm), SteepSlopeMPerKm, 1, 10_000);
        v.Range(nameof(DefaultMaxTier), DefaultMaxTier, 1, 99);
        v.Range(nameof(LakeMaxTier), LakeMaxTier, 1, 99);
        v.Range(nameof(RiverDownstreamPct), RiverDownstreamPct, 1, 1_000);
        v.Range(nameof(RiverUpstreamPct), RiverUpstreamPct, 1, 1_000);
        v.Range(nameof(RiverMaxTier), RiverMaxTier, 1, 99);
        v.Range(nameof(MaxVoyagePorts), MaxVoyagePorts, 0, 100_000);
        v.Range(nameof(MaxVoyageKm), MaxVoyageKm, 1, 40_000);
    }
}
