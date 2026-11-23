using WorldEcon.SharedKernel.Globe;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.Application.Globe;

/// <summary>A natural area containing a cell, with the cell's coverage in basis points (10,000 = 100 %).</summary>
public sealed record CellAreaEntry(int AreaIndex, GeographicRegionKind Kind, string Name, short CoverageBp);

/// <summary>A river step touching a cell, as (from, to) in flow direction.</summary>
public readonly record struct CellRiverEdge(int From, int To, long Flow);

/// <summary>
/// Everything known about one globe cell, as shown by <c>globe cell</c> and the desktop app's details panel. Built from
/// <see cref="WorldLayers"/> with <see cref="WorldLayers.Areas"/> populated (see <see cref="GlobeWorldLoader"/>).
/// </summary>
public sealed record GlobeCellDetails(
    int Cell,
    bool Pentagon,
    double LatDeg,
    double LonDeg,
    int PlateId,
    bool PlateContinental,
    BoundaryKind Boundary,
    int ElevationM,
    bool Ocean,
    int SeaLevelM,
    sbyte CurrentSlot,
    short CurrentAnomalyDeciC,
    sbyte WindSlot,
    byte WindStrength,
    short TemperatureDeciC,
    int PrecipitationMm,
    Biome Biome,
    IReadOnlyList<CompositionEntry> Composition,
    int DownstreamOf,
    long Flow,
    WaterBodyKind Water,
    IReadOnlyList<CellRiverEdge> RiverEdges,
    IReadOnlyList<int> Neighbours,
    IReadOnlyList<CellAreaEntry> Areas,
    IReadOnlyList<string> Settlements)
{
    /// <summary>
    /// Builds the details of <paramref name="cell"/>. Areas are ordered by (kind, name ordinal); settlements are those of
    /// <paramref name="settlements"/> on the cell, in the given order.
    /// </summary>
    public static GlobeCellDetails Build(WorldLayers l, int cell, IReadOnlyList<GlobeSettlement> settlements)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, l.Grid.CellCount);
        var (lat, lon) = l.Geometry.LatLon(cell);
        var plate = l.PlateOf[cell];
        var areas = new List<CellAreaEntry>();
        foreach (var a in l.Areas)
        {
            var coverage = CoverageOf(a, cell);
            if (coverage is { } bp) areas.Add(new CellAreaEntry(a.Index, a.Kind, a.Name, bp));
        }

        areas.Sort((x, y) => x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind) : string.CompareOrdinal(x.Name, y.Name));
        var rivers = l.RiverEdges.Where(e => e.LowCell == cell || e.HighCell == cell)
            .Select(e => e.FlowsLowToHigh ? new CellRiverEdge(e.LowCell, e.HighCell, e.Flow) : new CellRiverEdge(e.HighCell, e.LowCell, e.Flow))
            .ToList();

        return new GlobeCellDetails(
            cell,
            l.Grid.IsPentagon(cell),
            lat,
            lon,
            plate,
            plate < l.Plates.Length && l.Plates[plate].Continental,
            (BoundaryKind)l.BoundaryKind[cell],
            l.ElevationM[cell],
            l.IsOcean(cell),
            l.SeaLevelM,
            l.CurrentDir[cell],
            l.CurrentAnomalyDeciC[cell],
            l.WindDir[cell],
            l.WindStrength[cell],
            l.TemperatureDeciC[cell],
            l.PrecipitationMm[cell],
            (Biome)l.BiomeOf[cell],
            l.Composition[cell],
            l.DownstreamOf[cell],
            l.FlowAccum[cell],
            (WaterBodyKind)l.WaterBodyKind[cell],
            rivers,
            l.Grid.Neighbours(cell).ToArray(),
            areas,
            settlements.Where(s => s.Cell == cell).Select(s => s.Name).ToList());
    }

    /// <summary>Coverage of <paramref name="cell"/> in the area (member cells are sorted by cell id), or null.</summary>
    private static short? CoverageOf(GeneratedArea area, int cell)
    {
        int lo = 0, hi = area.Cells.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var c = area.Cells[mid].Cell;
            if (c == cell) return area.Cells[mid].CoverageBp;
            if (c < cell) lo = mid + 1;
            else hi = mid - 1;
        }

        return null;
    }
}
