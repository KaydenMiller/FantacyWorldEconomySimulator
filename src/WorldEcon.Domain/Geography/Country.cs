using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Country : AggregateRoot<CountryId>
{
    public WorldId WorldId { get; }
    public ContinentId ContinentId { get; }
    public string Name { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private Country() : base(default) => Name = null!;

    private Country(CountryId id, WorldId worldId, ContinentId continentId, string name) : base(id)
    {
        WorldId = worldId;
        ContinentId = continentId;
        Name = name;
    }

    public static ErrorOr<Country> Create(WorldId worldId, ContinentId continentId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("country.name.blank", "Country name must not be blank.");
        return new Country(CountryId.New(), worldId, continentId, name.Trim());
    }
}
