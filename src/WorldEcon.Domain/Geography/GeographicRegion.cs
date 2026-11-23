using ErrorOr;
using WorldEcon.SharedKernel.Domain;
using WorldEcon.SharedKernel.Globe;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// A natural geographic area (ocean, mountain range, desert, ...). Its extent is a set of globe cells
/// with a coverage share, held by <see cref="GeographicRegionCell"/>. <see cref="NameAuthored"/> is true
/// when the DM supplied the name rather than the generator.
/// </summary>
public sealed class GeographicRegion : AggregateRoot<GeographicRegionId>
{
    public WorldId WorldId { get; }
    public string Name { get; private set; }
    public GeographicRegionKind Kind { get; }
    public bool NameAuthored { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private GeographicRegion() : base(default) => Name = null!;

    private GeographicRegion(GeographicRegionId id, WorldId worldId, string name, GeographicRegionKind kind, bool nameAuthored) : base(id)
    {
        WorldId = worldId;
        Name = name;
        Kind = kind;
        NameAuthored = nameAuthored;
    }

    public static ErrorOr<GeographicRegion> Create(WorldId worldId, string name, GeographicRegionKind kind, bool nameAuthored = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("geographicregion.name.blank", "Geographic region name must not be blank.");
        return new GeographicRegion(GeographicRegionId.New(), worldId, name.Trim(), kind, nameAuthored);
    }

    public ErrorOr<Success> Rename(string name, bool authored)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("geographicregion.name.blank", "Geographic region name must not be blank.");
        Name = name.Trim();
        NameAuthored = authored;
        return Result.Success;
    }
}
