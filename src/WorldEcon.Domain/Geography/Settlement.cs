using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public sealed class Settlement : AggregateRoot<SettlementId>
{
    public WorldId WorldId { get; }
    public RegionId RegionId { get; private set; }
    public string Name { get; private set; }
    public SettlementType Type { get; private set; }
    public int X { get; private set; }              // display coordinate only (spec §9.1)
    public int Y { get; private set; }
    public long Population { get; private set; }
    public Provenance Provenance { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private Settlement() : base(default) => Name = null!;

    private Settlement(SettlementId id, WorldId worldId, RegionId regionId, string name,
        SettlementType type, int x, int y, long population, Provenance provenance) : base(id)
    {
        WorldId = worldId;
        RegionId = regionId;
        Name = name;
        Type = type;
        X = x;
        Y = y;
        Population = population;
        Provenance = provenance;
    }

    public static ErrorOr<Settlement> Create(WorldId worldId, RegionId regionId, string name,
        SettlementType type, int x, int y, long population)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("settlement.name.blank", "Settlement name must not be blank.");
        if (population < 0)
            return Error.Validation("settlement.population.negative", "Population must not be negative.");

        return new Settlement(SettlementId.New(), worldId, regionId, name.Trim(),
            type, x, y, population, Provenance.Authored);
    }
}
