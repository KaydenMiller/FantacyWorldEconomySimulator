using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Region-cell summary of the tiles (design spec §8), one value per region cell (<see cref="GlobeGrid.Build"/> at
/// Frequency × RegionMultiplier), built by <see cref="TileSummaryStage"/> from the tiles whose
/// <see cref="TileLayers.RegionParent"/> is the cell. Arrays only. Rules are in <see cref="TileSummaryStage"/>.
/// </summary>
public sealed class RegionSummary
{
    public RegionSummary(GlobeGrid grid, GlobeGeometry geometry, int[] globeParent)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(globeParent);
        if (globeParent.Length != grid.CellCount)
            throw new ArgumentException($"{globeParent.Length} globe parents for {grid.CellCount} region cells.", nameof(globeParent));
        Grid = grid;
        Geometry = geometry;
        GlobeParent = globeParent;
        int n = grid.CellCount;
        TileCount = new int[n];
        Kind = new byte[n];
        Biome = new byte[n];
        ElevationM = new short[n];
        TemperatureDeciC = new short[n];
        PrecipitationMm = new ushort[n];
        MountainShareBp = new ushort[n];
        Flow = new long[n];
        Downstream = new int[n];
        LakeId = new int[n];
        InLargeLake = new bool[n];
    }

    public GlobeGrid Grid { get; }

    public GlobeGeometry Geometry { get; }

    /// <summary>Globe cell of each region cell (<see cref="TileLayers.RegionGlobeParent"/>).</summary>
    public int[] GlobeParent { get; }

    public int Count => Grid.CellCount;

    /// <summary>Number of tiles whose region parent is the cell.</summary>
    public int[] TileCount { get; }

    /// <summary>Drawing kind: <see cref="Pipeline.Landform"/> as byte (Ocean or Lake when water tiles ≥ 1/3, else the most common land landform).</summary>
    public byte[] Kind { get; }

    /// <summary>Most common land-tile <see cref="Pipeline.Biome"/> as byte (Ocean or Lake when the cell has no land tile).</summary>
    public byte[] Biome { get; }

    /// <summary>Mean tile elevation, metres.</summary>
    public short[] ElevationM { get; }

    /// <summary>Mean tile temperature, tenths of °C.</summary>
    public short[] TemperatureDeciC { get; }

    /// <summary>Mean tile precipitation, mm/year.</summary>
    public ushort[] PrecipitationMm { get; }

    /// <summary>Share of Mountain-landform tiles, basis points.</summary>
    public ushort[] MountainShareBp { get; }

    /// <summary>Largest tile flow, mm·km²/yr.</summary>
    public long[] Flow { get; }

    /// <summary>Region cell that the largest-flow tile's drainage path enters first; −1 when the path ends inside the cell.</summary>
    public int[] Downstream { get; }

    /// <summary>Largest lake (by area, ties lowest id) with a tile in the cell; −1 for none. Dry lake beds are not counted.</summary>
    public int[] LakeId { get; }

    /// <summary>
    /// The region cells that draw large lakes (area ≥ <see cref="Pipeline.TilesSettings.LargeLakeMinKm2"/>) at Globe View:
    /// large-lake tiles × 3 ≥ the cell's tiles, or the cell holding the most tiles of a large lake (every large lake has
    /// at least one such cell; see <see cref="TileSummaryStage"/>). Such a cell may have a land <see cref="Kind"/>.
    /// </summary>
    public bool[] InLargeLake { get; }
}
