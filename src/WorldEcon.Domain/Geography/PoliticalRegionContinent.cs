using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// Many-to-many membership of a region in a continent. A region (e.g. one straddling two continents) can belong to several
/// continents; this is the explicit continent membership used when a region has no single primary
/// country to derive it from.
/// </summary>
public sealed class PoliticalRegionContinent : AggregateRoot<PoliticalRegionContinentId>
{
    public WorldId WorldId { get; }
    public PoliticalRegionId PoliticalRegionId { get; private set; }
    public ContinentId ContinentId { get; private set; }

    private PoliticalRegionContinent() : base(default) { }

    private PoliticalRegionContinent(PoliticalRegionContinentId id, WorldId worldId, PoliticalRegionId regionId, ContinentId continentId) : base(id)
    {
        WorldId = worldId;
        PoliticalRegionId = regionId;
        ContinentId = continentId;
    }

    public static ErrorOr<PoliticalRegionContinent> Create(WorldId worldId, PoliticalRegionId regionId, ContinentId continentId)
        => new PoliticalRegionContinent(PoliticalRegionContinentId.New(), worldId, regionId, continentId);
}
