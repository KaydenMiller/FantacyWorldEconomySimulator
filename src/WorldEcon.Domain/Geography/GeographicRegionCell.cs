using ErrorOr;
using WorldEcon.SharedKernel.Globe;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// Membership of one globe cell in a <see cref="GeographicRegion"/>, with the share of the cell the
/// region covers in basis points (1–10,000; 10,000 = the whole cell). Identified by the pair
/// (<see cref="GeographicRegionId"/>, <see cref="Cell"/>), so it carries no id of its own.
/// </summary>
public sealed class GeographicRegionCell
{
    public const short MaxCoverageBp = 10_000;

    public GeographicRegionId GeographicRegionId { get; private set; }
    public GlobeCellId Cell { get; private set; }
    public short CoverageBp { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private GeographicRegionCell() { }

    private GeographicRegionCell(GeographicRegionId regionId, GlobeCellId cell, short coverageBp)
    {
        GeographicRegionId = regionId;
        Cell = cell;
        CoverageBp = coverageBp;
    }

    public static ErrorOr<GeographicRegionCell> Create(GeographicRegionId regionId, GlobeCellId cell, short coverageBp)
    {
        if (coverageBp < 1 || coverageBp > MaxCoverageBp)
            return Error.Validation("geographicregioncell.coverage.range", "Coverage must be between 1 and 10,000 basis points.");
        return new GeographicRegionCell(regionId, cell, coverageBp);
    }
}
