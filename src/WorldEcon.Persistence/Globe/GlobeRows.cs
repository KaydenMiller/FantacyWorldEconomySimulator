using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Globe;

// Row types for the generated globe. They exist to give EF Core (Entity Framework Core) the table shapes (migrations, typed
// queries); GlobeStore writes them with raw parameterised commands, not through the change tracker.
// Enum-valued layers are stored as their byte value, matching the WorldLayers arrays.

/// <summary>One globe cell's per-cell layer values (table <c>globe_cells</c>, key WorldId + CellId).</summary>
public sealed class GlobeCellRow
{
    public WorldId WorldId { get; set; }
    public int CellId { get; set; }
    public int PlateId { get; set; }
    public int ElevationM { get; set; }
    public byte BoundaryKind { get; set; }
    public sbyte CurrentDir { get; set; }
    public short CurrentAnomalyDeciC { get; set; }
    public sbyte WindDir { get; set; }
    public byte WindStrength { get; set; }
    public short TemperatureDeciC { get; set; }
    public int PrecipitationMm { get; set; }
    public byte Biome { get; set; }
    public int DownstreamOf { get; set; }
    public long FlowAccum { get; set; }
    public byte WaterBodyKind { get; set; }
}

/// <summary>
/// One neighbour of a cell in grid order (table <c>globe_cell_neighbours</c>, key WorldId + CellId + Slot).
/// Written for queries outside the generator; <see cref="GlobeStore.LoadAsync"/> rebuilds topology from the
/// grid frequency instead of reading these rows.
/// </summary>
public sealed class GlobeCellNeighbourRow
{
    public WorldId WorldId { get; set; }
    public int CellId { get; set; }
    public byte Slot { get; set; }
    public int NeighbourCellId { get; set; }
}

/// <summary>
/// One tectonic plate (table <c>plates</c>, key WorldId + PlateId). The rotation axis and angular speed are
/// the generator's double values; SQLite REAL is an 8-byte IEEE 754 double, so they round-trip exactly.
/// </summary>
public sealed class PlateRow
{
    public WorldId WorldId { get; set; }
    public int PlateId { get; set; }
    public bool Continental { get; set; }
    public double RotationAxisX { get; set; }
    public double RotationAxisY { get; set; }
    public double RotationAxisZ { get; set; }
    public double AngularSpeed { get; set; }
    public int SeedCell { get; set; }
}

/// <summary>
/// One composition entry of a cell (table <c>globe_cell_composition</c>, key WorldId + CellId + Slot).
/// <see cref="Slot"/> keeps the generator's entry order, which the stage hash depends on.
/// </summary>
public sealed class GlobeCellCompositionRow
{
    public WorldId WorldId { get; set; }
    public int CellId { get; set; }
    public byte Slot { get; set; }
    public byte Landform { get; set; }
    public short ShareBp { get; set; }
}

/// <summary>One river step on an unordered neighbour pair (table <c>river_edges</c>, key WorldId + LowCellId + HighCellId).</summary>
public sealed class RiverEdgeRow
{
    public WorldId WorldId { get; set; }
    public int LowCellId { get; set; }
    public int HighCellId { get; set; }
    public bool FlowsLowToHigh { get; set; }
    public long Flow { get; set; }
}

/// <summary>
/// The generation run that produced a world's globe (table <c>worldgen_runs</c>, key WorldId; one run per
/// world because regeneration is out of scope). <see cref="SettingsJson"/> is the serialised
/// <c>WorldGenSettings</c> in its nested section form (<c>WorldGenSettingsJson</c>); <see cref="StagesJson"/> is a list of <see cref="WorldGenStageRecord"/>.
/// </summary>
public sealed class WorldGenRunRow
{
    public WorldId WorldId { get; set; }
    public int GeneratorVersion { get; set; }
    public string SettingsJson { get; set; } = "";
    public string StagesJson { get; set; } = "";
}

/// <summary>Per-stage entry of <see cref="WorldGenRunRow.StagesJson"/>: stage name, output hash, duration in ticks (100 ns).</summary>
public sealed record WorldGenStageRecord(string Stage, ulong Hash, long DurationTicks);

/// <summary>
/// The tiles of one globe cell (table <c>tile_blocks</c>, key CellId): <see cref="Data"/> is a
/// <c>TileBlockCodec</c> payload. The database holds one world's tiles, so there is no world id column.
/// </summary>
public sealed class TileBlockRow
{
    public int CellId { get; set; }
    public int TileCount { get; set; }
    public byte[] Data { get; set; } = [];
}

/// <summary>One lake or dry lake bed (table <c>lakes</c>, key Id = the tile layer's lake id): every field of <c>TileLake</c>. Kind is the byte value of <c>TileWaterKind</c>.</summary>
public sealed class LakeRow
{
    public int Id { get; set; }
    public byte Kind { get; set; }
    public int WaterLevelM { get; set; }
    public int TileCount { get; set; }
    public double AreaKm2 { get; set; }
    public int OutletTile { get; set; }
    public int LowestTile { get; set; }
    public long Inflow { get; set; }
    public long Evaporation { get; set; }
    public int ExitTile { get; set; }
}

/// <summary>The region-cell summary arrays as an opaque byte payload (table <c>region_level</c>, one row with Id 0; format owned by the writer).</summary>
public sealed class RegionLevelRow
{
    public int Id { get; set; }
    public byte[] Data { get; set; } = [];
}
