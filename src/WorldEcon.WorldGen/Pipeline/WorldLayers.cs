using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Pipeline;

/// <summary>
/// The generated data for one world: the grid, its geometry, and one array per layer. Each stage task
/// appends its own layer arrays (length <see cref="GlobeGrid.CellCount"/>, or edge/area lists).
/// </summary>
public sealed class WorldLayers
{
    public WorldLayers(GlobeGrid grid, GlobeGeometry geometry)
    {
        Grid = grid;
        Geometry = geometry;
    }

    public GlobeGrid Grid { get; }

    public GlobeGeometry Geometry { get; }

    /// <summary>Stage 1: plate index per cell.</summary>
    public int[] PlateOf { get; set; } = [];

    /// <summary>Stage 1: plate descriptors, indexed by plate id.</summary>
    public PlateInfo[] Plates { get; set; } = [];

    /// <summary>Stage 2: elevation in metres per cell.</summary>
    public int[] ElevationM { get; set; } = [];

    /// <summary>Stage 2: plate boundary classification per cell (<see cref="BoundaryKind"/> as byte).</summary>
    public byte[] BoundaryKind { get; set; } = [];

    /// <summary>Stage 3: cells with elevation strictly below this value (metres) are ocean.</summary>
    public int SeaLevelM { get; set; }

    /// <summary>
    /// Stage 4: neighbour slot (index into <c>Neighbours(c)</c>) of the surface current direction; −1 on land of the
    /// globe-stage ocean mask (Stage 3). The tile summary later rewrites the ocean mask, so a cell can be land in the
    /// summary and still carry a direction (seed 42, earth-5km: 678 of 40,962 cells change ocean state, 343 summary-land
    /// cells have a direction ≥ 0).
    /// </summary>
    public sbyte[] CurrentDir { get; set; } = [];

    /// <summary>Stage 4: sea-surface temperature anomaly from the current, tenths of °C; 0 on land and away from margins.</summary>
    public short[] CurrentAnomalyDeciC { get; set; } = [];

    /// <summary>Stage 5: neighbour slot (index into <c>Neighbours(c)</c>) the prevailing wind blows toward.</summary>
    public sbyte[] WindDir { get; set; } = [];

    /// <summary>Stage 5: prevailing wind strength, 0–255.</summary>
    public byte[] WindStrength { get; set; } = [];

    /// <summary>Stage 6: mean surface temperature per cell, tenths of °C.</summary>
    public short[] TemperatureDeciC { get; set; } = [];

    /// <summary>Stage 6: precipitation per cell, mm/year, 0–4000.</summary>
    public int[] PrecipitationMm { get; set; } = [];

    /// <summary>Stage 7: main type per cell (<see cref="Biome"/> as byte). Task 12 sets <c>Lake</c> on lake cells.</summary>
    public byte[] BiomeOf { get; set; } = [];

    /// <summary>
    /// Stage 7: composition per cell, 1–4 entries with distinct landforms summing to exactly 10,000 bp.
    /// Jagged (one array per cell) so Task 12 can replace a basin cell's entries without rebuilding an offset table.
    /// </summary>
    public CompositionEntry[][] Composition { get; set; } = [];

    /// <summary>Stage 8: neighbour each land cell drains to; −1 for ocean cells and endorheic sinks.</summary>
    public int[] DownstreamOf { get; set; } = [];

    /// <summary>Stage 8: accumulated flow through each cell, mm·km² per year (precipitation × area, summed upstream); 0 on ocean (flow reaching the sea is counted on the last land cell).</summary>
    public long[] FlowAccum { get; set; } = [];

    /// <summary>Stage 8: surface-water classification per cell (<see cref="Pipeline.WaterBodyKind"/> as byte).</summary>
    public byte[] WaterBodyKind { get; set; } = [];

    /// <summary>Stage 8: river steps sorted by (LowCell, HighCell).</summary>
    public List<RiverEdge> RiverEdges { get; set; } = [];

    /// <summary>Stage 9: natural areas (many-to-many with cells), indexed in (kind, smallest member cell) order.</summary>
    public List<GeneratedArea> Areas { get; set; } = [];

    /// <summary>Tectonics stage: the per-cell geological record (null before the stage, or for a world without one).</summary>
    public Tectonics.CrustRecord? Crust { get; set; }

    /// <summary>Tectonics stage: present-day collision strength at plate boundaries, permille of the largest; 0 away from boundaries.</summary>
    public ushort[] CollisionStrengthPermille { get; set; } = [];

    /// <summary>Tectonics stage: steps (cells) to the nearest present-day plate boundary within the cell's plate; 0 on boundaries.</summary>
    public ushort[] BoundaryDistanceCells { get; set; } = [];

    public bool IsOcean(int cell) => ElevationM[cell] < SeaLevelM;

    /// <summary>Number of <see cref="CloneGlobe"/> calls on the current thread (tests check that no copy is made without an observer).</summary>
    [ThreadStatic]
    internal static int CloneCount;

    /// <summary>
    /// A copy that later changes to this instance do not affect: every layer array is copied (each
    /// <see cref="Composition"/> row too), the lists are copied, and <see cref="Grid"/>, <see cref="Geometry"/> and the
    /// immutable <see cref="PlateInfo"/> records are shared.
    /// </summary>
    public WorldLayers CloneGlobe()
    {
        CloneCount++;
        return new WorldLayers(Grid, Geometry)
        {
            PlateOf = (int[])PlateOf.Clone(),
            Plates = (PlateInfo[])Plates.Clone(),
            ElevationM = (int[])ElevationM.Clone(),
            BoundaryKind = (byte[])BoundaryKind.Clone(),
            SeaLevelM = SeaLevelM,
            CurrentDir = (sbyte[])CurrentDir.Clone(),
            CurrentAnomalyDeciC = (short[])CurrentAnomalyDeciC.Clone(),
            WindDir = (sbyte[])WindDir.Clone(),
            WindStrength = (byte[])WindStrength.Clone(),
            TemperatureDeciC = (short[])TemperatureDeciC.Clone(),
            PrecipitationMm = (int[])PrecipitationMm.Clone(),
            BiomeOf = (byte[])BiomeOf.Clone(),
            Composition = Composition.Select(row => (CompositionEntry[])row.Clone()).ToArray(),
            DownstreamOf = (int[])DownstreamOf.Clone(),
            FlowAccum = (long[])FlowAccum.Clone(),
            WaterBodyKind = (byte[])WaterBodyKind.Clone(),
            RiverEdges = [.. RiverEdges],
            Areas = [.. Areas],
            Crust = Crust?.Clone(),
            CollisionStrengthPermille = (ushort[])CollisionStrengthPermille.Clone(),
            BoundaryDistanceCells = (ushort[])BoundaryDistanceCells.Clone(),
        };
    }
}
