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
    /// <summary>The cell's geological record (Tectonics stage); null for a world without one.</summary>
    public CellGeology? Geology { get; init; }

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
            settlements.Where(s => s.Cell == cell).Select(s => s.Name).ToList())
        {
            Geology = l.Crust is { } r && cell < r.Count ? CellGeology.From(l, r, cell) : null,
        };
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

/// <summary>
/// One cell's geological record (design 2026-11-24, section 4). Ages are million years (My) before present; null means
/// never.
/// </summary>
public sealed record CellGeology(
    WorldEcon.WorldGen.Tectonics.CrustKind Kind,
    double ThicknessKm,
    ushort FormedAgeMy,
    WorldEcon.WorldGen.Tectonics.OrogenyKind Orogeny,
    ushort? LastOrogenyAgeMy,
    int OrogenyCount,
    ushort? ArcMagmaAgeMy,
    WorldEcon.WorldGen.Tectonics.CrustFlags Flags,
    ushort? RiftAgeMy,
    ushort? PassiveMarginAgeMy,
    double SedimentKm,
    double MaxBurialKm,
    int CoalExposureMy,
    int EvaporiteExposureMy,
    int CollisionStrengthPermille,
    int BoundaryDistanceCells)
{
    /// <summary>Label/value rows for the GUI details panel and <c>globe cell</c>.</summary>
    public IReadOnlyList<(string Label, string Value)> Describe()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Ago(ushort? a) => a is { } v ? string.Create(inv, $"{v:N0} My ago") : "never";
        var flags = new List<string>();
        if ((Flags & WorldEcon.WorldGen.Tectonics.CrustFlags.Suture) != 0) flags.Add("suture");
        if ((Flags & WorldEcon.WorldGen.Tectonics.CrustFlags.CollisionGranite) != 0) flags.Add("collision granite");
        if ((Flags & WorldEcon.WorldGen.Tectonics.CrustFlags.FailedRift) != 0) flags.Add("failed rift");
        var orogeny = Orogeny == WorldEcon.WorldGen.Tectonics.OrogenyKind.None
            ? "none"
            : string.Create(inv, $"{Orogeny}, last {Ago(LastOrogenyAgeMy)} ({OrogenyCount} episode{(OrogenyCount == 1 ? "" : "s")})");
        if (ArcMagmaAgeMy is not null) orogeny += $", arc magma {Ago(ArcMagmaAgeMy)}";
        if (flags.Count > 0) orogeny += "; " + string.Join(", ", flags);
        var rift = RiftAgeMy is null && PassiveMarginAgeMy is null
            ? "none"
            : string.Join(", ", new[] { RiftAgeMy is null ? null : $"rifted {Ago(RiftAgeMy)}", PassiveMarginAgeMy is null ? null : $"passive margin since {Ago(PassiveMarginAgeMy)}" }.Where(x => x is not null));
        return
        [
            ("Crust", string.Create(inv, $"{Kind}, {ThicknessKm:0.#} km thick, formed {FormedAgeMy:N0} My ago")),
            ("Mountain building", orogeny),
            ("Rift", rift),
            ("Sediment", string.Create(inv, $"{SedimentKm:0.00} km (deepest burial {MaxBurialKm:0.00} km)")),
            ("Coal / salt exposure", string.Create(inv, $"coal {CoalExposureMy:N0} My, evaporite {EvaporiteExposureMy:N0} My")),
            ("Plate boundary", string.Create(inv, $"collision strength {CollisionStrengthPermille} ‰, {(BoundaryDistanceCells == 0 ? "on the boundary" : $"{BoundaryDistanceCells} cells from it")}")),
        ];
    }

    internal static CellGeology From(WorldLayers l, WorldEcon.WorldGen.Tectonics.CrustRecord r, int c)
    {
        static ushort? Age(ushort v) => v == WorldEcon.WorldGen.Tectonics.CrustRecord.Never ? null : v;
        return new CellGeology(r.Kind[c], r.ThicknessDam[c] / 100.0, r.FormedAgeMy[c], r.Orogeny[c], Age(r.LastOrogenyAgeMy[c]),
            r.OrogenyCount[c], Age(r.ArcMagmaAgeMy[c]), r.Flags[c], Age(r.RiftAgeMy[c]), Age(r.PassiveMarginAgeMy[c]),
            r.SedimentDam[c] / 100.0, r.MaxBurialDam[c] / 100.0, r.CoalExposureMy[c], r.EvaporiteExposureMy[c],
            c < l.CollisionStrengthPermille.Length ? l.CollisionStrengthPermille[c] : 0,
            c < l.BoundaryDistanceCells.Length ? l.BoundaryDistanceCells[c] : 0);
    }
}
