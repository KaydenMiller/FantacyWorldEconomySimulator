using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// A country's claim over a settlement or region — either full control or an unresolved dispute.
/// Multiple claims on the same target model contested territory (e.g. a city held by one country and
/// disputed by another). The lightweight precursor to the full faction control/influence system (§10).
/// </summary>
public sealed class TerritorialClaim : AggregateRoot<TerritorialClaimId>
{
    public WorldId WorldId { get; }
    public CountryId CountryId { get; private set; }
    public ClaimType ClaimType { get; private set; }
    public ClaimTargetKind TargetKind { get; private set; }
    public Guid TargetId { get; private set; }

    private TerritorialClaim() : base(default) { }

    private TerritorialClaim(TerritorialClaimId id, WorldId worldId, CountryId countryId,
        ClaimType claimType, ClaimTargetKind targetKind, Guid targetId) : base(id)
    {
        WorldId = worldId;
        CountryId = countryId;
        ClaimType = claimType;
        TargetKind = targetKind;
        TargetId = targetId;
    }

    public static ErrorOr<TerritorialClaim> Create(WorldId worldId, CountryId countryId,
        ClaimType claimType, ClaimTargetKind targetKind, Guid targetId)
    {
        if (targetId == Guid.Empty)
            return Error.Validation("claim.target.empty", "Claim target id must not be empty.");
        return new TerritorialClaim(TerritorialClaimId.New(), worldId, countryId, claimType, targetKind, targetId);
    }

    public static ErrorOr<TerritorialClaim> CreateForSettlement(WorldId worldId, CountryId countryId, SettlementId settlement, ClaimType claimType)
        => Create(worldId, countryId, claimType, ClaimTargetKind.Settlement, settlement.Value);

    public static ErrorOr<TerritorialClaim> CreateForRegion(WorldId worldId, CountryId countryId, RegionId region, ClaimType claimType)
        => Create(worldId, countryId, claimType, ClaimTargetKind.Region, region.Value);
}
