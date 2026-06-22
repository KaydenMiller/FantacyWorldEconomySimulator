using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// Many-to-many nesting/overlap between regions: <see cref="ParentRegionId"/> contains
/// <see cref="ChildRegionId"/>. A child may have several parents (a region spanning multiple
/// states), and a parent may contain several children (sub-areas of a park).
/// </summary>
public sealed class RegionContainment : AggregateRoot<RegionContainmentId>
{
    public WorldId WorldId { get; }
    public RegionId ParentRegionId { get; private set; }
    public RegionId ChildRegionId { get; private set; }

    private RegionContainment() : base(default) { }

    private RegionContainment(RegionContainmentId id, WorldId worldId, RegionId parent, RegionId child) : base(id)
    {
        WorldId = worldId;
        ParentRegionId = parent;
        ChildRegionId = child;
    }

    public static ErrorOr<RegionContainment> Create(WorldId worldId, RegionId parentRegionId, RegionId childRegionId)
    {
        if (parentRegionId.Equals(childRegionId))
            return Error.Validation("regioncontainment.selfloop", "A region cannot contain itself.");
        return new RegionContainment(RegionContainmentId.New(), worldId, parentRegionId, childRegionId);
    }
}
