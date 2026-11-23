using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// The lowest stored level of the map's picture pyramid: per globe cell a <see cref="Size"/> × <see cref="Size"/> grid of
/// texels over the cell's <see cref="CellChart"/>, each holding the fields of the tile nearest the texel centre (a globe
/// cell spans about 24 tiles, so 16 texels is close to tile resolution). The map colours texels with the same palette as
/// tiles, so a coastal cell shows its land and water parts where the tiles put them. Derived view data: not hashed.
/// Texel (i, j) of cell c is at <see cref="Index"/>(c, i, j).
/// </summary>
public sealed class CellImagery
{
    public const int Size = 16;
    public const int TexelsPerCell = Size * Size;

    public CellImagery(int cellCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cellCount);
        CellCount = cellCount;
        var n = checked(cellCount * TexelsPerCell);
        Biome = new byte[n];
        Landform = new byte[n];
        Water = new byte[n];
        ElevationM = new short[n];
        TemperatureDeciC = new short[n];
        PrecipitationMm = new ushort[n];
        FlowLevel = new byte[n];
    }

    public int CellCount { get; }

    public byte[] Biome { get; }

    public byte[] Landform { get; }

    /// <summary><see cref="TileWaterKind"/>, with ocean tiles as <see cref="TileWaterKind.Ocean"/>.</summary>
    public byte[] Water { get; }

    public short[] ElevationM { get; }

    public short[] TemperatureDeciC { get; }

    public ushort[] PrecipitationMm { get; }

    /// <summary>Flow accumulation on a log scale (<see cref="FlowToLevel"/>).</summary>
    public byte[] FlowLevel { get; }

    public static int Index(int cell, int i, int j) => (cell * TexelsPerCell) + (j * Size) + i;

    /// <summary>round(16 × log10(flow + 1)), at most 255: 1/16 of a decade per step.</summary>
    public static byte FlowToLevel(long flow) => (byte)Math.Clamp(Math.Round(16 * Math.Log10(Math.Max(0, flow) + 1.0)), 0, 255);

    /// <summary>Inverse of <see cref="FlowToLevel"/> (within 1/32 of a decade).</summary>
    public static long LevelToFlow(byte level) => (long)Math.Round(Math.Pow(10, level / 16.0) - 1);

    /// <summary>Samples every globe cell's texels from <paramref name="tiles"/> (nearest tile per texel centre).</summary>
    public static CellImagery Build(TileLayers tiles, GlobeGrid grid, GlobeGeometry geometry, ParallelOptions? parallel = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(geometry);
        var img = new CellImagery(grid.CellCount);
        Parallel.For(0, grid.CellCount, parallel ?? new ParallelOptions(), cell =>
        {
            var chart = CellChart.For(grid, geometry, cell);
            for (var j = 0; j < Size; j++)
                for (var i = 0; i < Size; i++)
                {
                    var t = tiles.Grid.Nearest(chart.TexelCentre(i, j, Size));
                    var k = Index(cell, i, j);
                    img.Biome[k] = tiles.Biome[t];
                    img.Landform[k] = tiles.Landform[t];
                    img.Water[k] = tiles.Ocean[t] ? (byte)TileWaterKind.Ocean : tiles.WaterKind[t];
                    img.ElevationM[k] = tiles.ElevationM[t];
                    img.TemperatureDeciC[k] = tiles.TemperatureDeciC[t];
                    img.PrecipitationMm[k] = tiles.PrecipitationMm[t];
                    img.FlowLevel[k] = FlowToLevel(tiles.FlowAccum[t]);
                }
        });
        return img;
    }
}

/// <summary>
/// Every major river edge at tile resolution: tile <see cref="From"/>[k] (land, or a lake's outlet tile draining onto land,
/// <see cref="FromWater"/>) drains into <see cref="To"/>[k] with <see cref="Flow"/> ≥ Tiles.MajorRiverFlow (the from
/// tile's flow; for an outlet, <see cref="TileLake.Outflow"/>, what leaves the lake after evaporation) and whether the downstream tile is ocean or lake (<see cref="ToWater"/>). The map traces Globe and
/// Region View rivers from these, so they start at a lake shore and end at the tile coast. Edges are in ascending
/// from-tile order. Derived view data: not hashed.
/// </summary>
public sealed class RiverCourses(int[] from, int[] to, long[] flow, bool[] toWater, bool[] fromWater)
{
    /// <summary>True where the from tile is a lake tile (the edge is a lake outlet).</summary>
    public bool[] FromWater { get; } = fromWater;

    public int[] From { get; } = from;

    public int[] To { get; } = to;

    public long[] Flow { get; } = flow;

    public bool[] ToWater { get; } = toWater;

    public int Count => From.Length;

    public static RiverCourses Build(TileLayers tiles, long majorRiverFlow)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var from = new List<int>();
        var to = new List<int>();
        var flow = new List<long>();
        var water = new List<bool>();
        var fromWater = new List<bool>();
        for (var i = 0; i < tiles.Count; i++)
        {
            if (tiles.Ocean[i]) continue;
            var d = tiles.DownstreamOf(i);
            if (d < 0) continue;
            var toWater = tiles.Ocean[d] || tiles.WaterKind[d] == (byte)TileWaterKind.Lake;
            var lake = tiles.WaterKind[i] == (byte)TileWaterKind.Lake;
            // A lake tile starts a river only as its lake's outlet onto land; other shore tiles can point at land without
            // draining there (terminal lakes, non-exit components).
            if (lake && (toWater || tiles.Lakes[tiles.LakeId[i]].OutletTile != i)) continue;
            // A lake outlet carries what leaves the lake after evaporation (TileLake.Outflow); a lake tile's own flow is
            // its inflow, and the downstream tile's flow can include a tributary joining there.
            var edgeFlow = lake ? tiles.Lakes[tiles.LakeId[i]].Outflow : tiles.FlowAccum[i];
            if (edgeFlow < majorRiverFlow) continue;
            from.Add(i);
            to.Add(d);
            flow.Add(edgeFlow);
            water.Add(toWater);
            fromWater.Add(lake);
        }

        return new RiverCourses([.. from], [.. to], [.. flow], [.. water], [.. fromWater]);
    }
}
