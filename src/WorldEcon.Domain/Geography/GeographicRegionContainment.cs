using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// Many-to-many nesting/overlap between geographic regions: <see cref="ParentRegionId"/> contains
/// <see cref="ChildRegionId"/> (for example a sea inside an ocean).
/// </summary>
public sealed class GeographicRegionContainment : AggregateRoot<GeographicRegionContainmentId>
{
    public WorldId WorldId { get; }
    public GeographicRegionId ParentRegionId { get; private set; }
    public GeographicRegionId ChildRegionId { get; private set; }

    private GeographicRegionContainment() : base(default) { }

    private GeographicRegionContainment(GeographicRegionContainmentId id, WorldId worldId, GeographicRegionId parent, GeographicRegionId child) : base(id)
    {
        WorldId = worldId;
        ParentRegionId = parent;
        ChildRegionId = child;
    }

    public static ErrorOr<GeographicRegionContainment> Create(WorldId worldId, GeographicRegionId parentRegionId, GeographicRegionId childRegionId)
    {
        if (parentRegionId.Equals(childRegionId))
            return Error.Validation("geographicregioncontainment.selfloop", "A region cannot contain itself.");
        return new GeographicRegionContainment(GeographicRegionContainmentId.New(), worldId, parentRegionId, childRegionId);
    }
}
