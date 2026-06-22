using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// A region/area. <see cref="CountryId"/> is the optional primary (administrative) country — oceans
/// and unclaimed wilderness have none (null). Continent membership is many-to-many via
/// <see cref="RegionContinent"/> (a region may derive its continent from its primary country, or
/// span several continents like an ocean); disputed control is modelled by <see cref="TerritorialClaim"/>;
/// nesting/overlap (a park region spanning several states) by <see cref="RegionContainment"/>.
/// </summary>
public sealed class Region : AggregateRoot<RegionId>
{
    public WorldId WorldId { get; }
    public CountryId? CountryId { get; private set; }
    public RegionKind Kind { get; private set; }
    public string Name { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private Region() : base(default) => Name = null!;

    private Region(RegionId id, WorldId worldId, CountryId? countryId, RegionKind kind, string name) : base(id)
    {
        WorldId = worldId;
        CountryId = countryId;
        Kind = kind;
        Name = name;
    }

    /// <summary>Back-compatible: a land region administered by a country.</summary>
    public static ErrorOr<Region> Create(WorldId worldId, CountryId countryId, string name)
        => Create(worldId, name, RegionKind.Land, countryId);

    /// <summary>A region of any kind, with an optional primary country (none for oceans/wilderness).</summary>
    public static ErrorOr<Region> Create(WorldId worldId, string name, RegionKind kind, CountryId? countryId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("region.name.blank", "Region name must not be blank.");
        return new Region(RegionId.New(), worldId, countryId, kind, name.Trim());
    }

    public void SetKind(RegionKind kind) => Kind = kind;

    public void SetPrimaryCountry(CountryId? countryId) => CountryId = countryId;
}
