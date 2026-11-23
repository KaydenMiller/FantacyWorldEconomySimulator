namespace WorldEcon.Rendering.Gpu;

/// <summary>
/// Globe and Region View rivers as GPU quads (design 2026-11-23-gpu-map-rendering §6): every piece of every river course
/// level, sorted by width level within a level (thicker pieces drawn later, as the CPU renderer strokes its width paths in
/// order). The vertex shader projects both ends and expands the quad to the stroke width; the fragment shader draws an
/// anti-aliased round-capped capsule. Built once per world; a frame picks one level's index range.
/// </summary>
public sealed class GpuRivers
{
    /// <summary>
    /// Floats per vertex: end A (3), end B (3), corner (2: along 0/1, across −1/+1), width level (1), river length rad (1),
    /// log10 flow at A and at B (2; the shader's flow filter, as the CPU renderer's <c>AddRiverPaths</c> minFlow / maxFlow).
    /// </summary>
    public const int FloatsPerVertex = 12;

    /// <summary>Stroke width as the CPU renderer's Globe/Region rivers: unit × (MinFrac + AddFrac × level / 31), 1 … MaxPx.</summary>
    public const float MinFrac = 0.07f;
    public const float AddFrac = 0.18f;
    public const float MaxPx = 10f;

    private GpuRivers(float[] vertices, uint[] cells, uint[] indices, (int First, int Count)[] levels, double[] tolerances, double[] minLengthPx)
    {
        Vertices = vertices;
        Cells = cells;
        Indices = indices;
        Levels = levels;
        TolerancesRad = tolerances;
        MinLengthPx = minLengthPx;
    }

    public float[] Vertices { get; }

    /// <summary>Globe cell of end A and end B per vertex (two integers per vertex, for the Region View group scaling).</summary>
    public uint[] Cells { get; }

    public uint[] Indices { get; }

    /// <summary>Index range (first index, index count) of each course level, finest first.</summary>
    public IReadOnlyList<(int First, int Count)> Levels { get; }

    /// <summary>Simplification tolerance of each level (radians).</summary>
    public IReadOnlyList<double> TolerancesRad { get; }

    /// <summary>Rivers shorter than this on screen are skipped, per level (0: none skipped).</summary>
    public IReadOnlyList<double> MinLengthPx { get; }

    public int PieceCount => Indices.Length / 6;

    /// <summary>Globe and Region View rivers: every course level of <paramref name="renderer"/>'s world.</summary>
    public static GpuRivers Build(MapRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return FromLevels(renderer.RiverCourseLevels());
    }

    internal static GpuRivers FromLevels(IReadOnlyList<MapRenderer.RiverCourseLevel> levels)
    {
        var pieces = new List<(int Level, int River, int Point, byte Width)>();
        var ranges = new (int First, int Count)[levels.Count];
        foreach (var (li, level) in levels.Select((l, i) => (i, l)))
        {
            var start = pieces.Count;
            for (var k = 0; k < level.Points.Length; k++)
                for (var i = 0; i + 1 < level.Points[k].Length; i++)
                    pieces.Add((li, k, i, level.Level[k][i + 1]));
            pieces.Sort(start, pieces.Count - start, Comparer<(int Level, int River, int Point, byte Width)>.Create((a, b) => a.Width.CompareTo(b.Width)));
            ranges[li] = (start * 6, (pieces.Count - start) * 6);
        }

        var vertices = new float[pieces.Count * 4 * FloatsPerVertex];
        var cells = new uint[pieces.Count * 4 * 2];
        var indices = new uint[pieces.Count * 6];
        ReadOnlySpan<(float Along, float Across)> corners = [(0, -1), (1, -1), (1, 1), (0, 1)];
        for (var p = 0; p < pieces.Count; p++)
        {
            var (li, k, i, width) = pieces[p];
            var level = levels[li];
            var a = level.Points[k][i];
            var b = level.Points[k][i + 1];
            for (var v = 0; v < 4; v++)
            {
                var o = ((p * 4) + v) * FloatsPerVertex;
                vertices[o] = a.X;
                vertices[o + 1] = a.Y;
                vertices[o + 2] = a.Z;
                vertices[o + 3] = b.X;
                vertices[o + 4] = b.Y;
                vertices[o + 5] = b.Z;
                vertices[o + 6] = corners[v].Along;
                vertices[o + 7] = corners[v].Across;
                vertices[o + 8] = width;
                vertices[o + 9] = (float)level.LengthRad[k];
                vertices[o + 10] = (float)Math.Log10(Math.Max(1, level.Flow[k][i]));
                vertices[o + 11] = (float)Math.Log10(Math.Max(1, level.Flow[k][i + 1]));
                cells[(((p * 4) + v) * 2)] = (uint)level.Globe[k][i];
                cells[(((p * 4) + v) * 2) + 1] = (uint)level.Globe[k][i + 1];
            }

            var b0 = (uint)(p * 4);
            var io = p * 6;
            indices[io] = b0;
            indices[io + 1] = b0 + 1;
            indices[io + 2] = b0 + 2;
            indices[io + 3] = b0;
            indices[io + 4] = b0 + 2;
            indices[io + 5] = b0 + 3;
        }

        return new GpuRivers(vertices, cells, indices, ranges,
            [.. levels.Select(l => l.ToleranceRad)], [.. levels.Select(l => l.MinLengthPx)]);
    }

    /// <summary>
    /// RGBA32F per-cell geometry (<see cref="GpuScene.CellTextureWidth"/> wide, 2 texels per cell: centre, first corner),
    /// so the river vertex shader can apply a globe cell's Region View group scaling (as the CPU renderer's <c>Cluster</c>).
    /// </summary>
    public static float[] CellGeometry(MapWorld world, out int rows)
    {
        ArgumentNullException.ThrowIfNull(world);
        var grid = world.Globe.Grid;
        var geo = world.Globe.Geometry;
        rows = ((2 * grid.CellCount) + GpuScene.CellTextureWidth - 1) / GpuScene.CellTextureWidth;
        var data = new float[GpuScene.CellTextureWidth * rows * 4];
        for (var c = 0; c < grid.CellCount; c++)
        {
            var centre = geo.Centre(c);
            var corner = geo.Corner(grid.CornerTriangles(c)[0]);
            var o = c * 8;
            (data[o], data[o + 1], data[o + 2]) = ((float)centre.X, (float)centre.Y, (float)centre.Z);
            (data[o + 4], data[o + 5], data[o + 6]) = ((float)corner.X, (float)corner.Y, (float)corner.Z);
        }

        return data;
    }
}
