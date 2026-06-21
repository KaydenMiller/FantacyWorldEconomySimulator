using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Region : AggregateRoot<RegionId>
{
    public WorldId WorldId { get; }
    public CountryId CountryId { get; }
    public string Name { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private Region() : base(default) => Name = null!;

    private Region(RegionId id, WorldId worldId, CountryId countryId, string name) : base(id)
    {
        WorldId = worldId;
        CountryId = countryId;
        Name = name;
    }

    public static ErrorOr<Region> Create(WorldId worldId, CountryId countryId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("region.name.blank", "Region name must not be blank.");
        return new Region(RegionId.New(), worldId, countryId, name.Trim());
    }
}
