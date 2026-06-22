using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// Many-to-many membership of a region in a continent. A region (e.g. an ocean) can belong to several
/// continents; this is the explicit continent membership used when a region has no single primary
/// country to derive it from.
/// </summary>
public sealed class RegionContinent : AggregateRoot<RegionContinentId>
{
    public WorldId WorldId { get; }
    public RegionId RegionId { get; private set; }
    public ContinentId ContinentId { get; private set; }

    private RegionContinent() : base(default) { }

    private RegionContinent(RegionContinentId id, WorldId worldId, RegionId regionId, ContinentId continentId) : base(id)
    {
        WorldId = worldId;
        RegionId = regionId;
        ContinentId = continentId;
    }

    public static ErrorOr<RegionContinent> Create(WorldId worldId, RegionId regionId, ContinentId continentId)
        => new RegionContinent(RegionContinentId.New(), worldId, regionId, continentId);
}
