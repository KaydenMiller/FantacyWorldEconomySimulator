using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// A political region (administrative area). <see cref="CountryId"/> is the optional primary
/// country. Continent membership is many-to-many via <see cref="PoliticalRegionContinent"/>;
/// disputed control is modelled by <see cref="TerritorialClaim"/>.
/// </summary>
public sealed class PoliticalRegion : AggregateRoot<PoliticalRegionId>
{
    public WorldId WorldId { get; }
    public CountryId? CountryId { get; private set; }
    public string Name { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private PoliticalRegion() : base(default) => Name = null!;

    private PoliticalRegion(PoliticalRegionId id, WorldId worldId, CountryId? countryId, string name) : base(id)
    {
        WorldId = worldId;
        CountryId = countryId;
        Name = name;
    }

    public static ErrorOr<PoliticalRegion> Create(WorldId worldId, string name, CountryId? countryId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("region.name.blank", "Region name must not be blank.");
        return new PoliticalRegion(PoliticalRegionId.New(), worldId, countryId, name.Trim());
    }

    public void SetPrimaryCountry(CountryId? countryId) => CountryId = countryId;
}
