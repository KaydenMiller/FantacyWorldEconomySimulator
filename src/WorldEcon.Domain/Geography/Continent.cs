using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Continent : AggregateRoot<ContinentId>
{
    public WorldId WorldId { get; }
    public string Name { get; private set; }

    private Continent(ContinentId id, WorldId worldId, string name) : base(id)
    {
        WorldId = worldId;
        Name = name;
    }

    public static ErrorOr<Continent> Create(WorldId worldId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("continent.name.blank", "Continent name must not be blank.");
        return new Continent(ContinentId.New(), worldId, name.Trim());
    }
}
