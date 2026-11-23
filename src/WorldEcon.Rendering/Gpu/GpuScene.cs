using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering.Gpu;

/// <summary>
/// The static GPU (graphics processing unit) data of a world, built once on the CPU and uploaded once: the globe cell
/// mesh and the texel atlas (design 2026-11-23-gpu-map-rendering §6). Plain typed arrays only (no graphics API), so the
/// desktop OpenGL backend and a WebGL2 front end consume the same buffers. Per-frame work is <see cref="GpuFrame"/> only.
/// </summary>
public sealed class GpuScene
{
    /// <summary>Atlas layer side in texels (2048: the WebGL2 minimum texture size).</summary>
    public const int AtlasSize = 2048;

    /// <summary>Cells per atlas row (and rows per layer): <see cref="AtlasSize"/> / <see cref="CellImagery.Size"/>.</summary>
    public const int CellsPerRow = AtlasSize / CellImagery.Size;

    /// <summary>Cells per atlas layer.</summary>
    public const int CellsPerLayer = CellsPerRow * CellsPerRow;

    /// <summary>Floats per mesh vertex: position (3), cell centre (3), texel coordinate (2).</summary>
    public const int FloatsPerVertex = 8;

    /// <summary>Width of the per-cell textures (cell colour, cell flags); rows = ⌈cells / width⌉.</summary>
    public const int CellTextureWidth = 2048;

    private GpuScene(float[] vertices, uint[] vertexCells, uint[] indices, int cellCount, byte[]? atlasClass, short[]? atlasValue, int atlasLayers, GpuGlyphs globeGlyphs, GpuGlyphs regionGlyphs)
    {
        GlobeGlyphs = globeGlyphs;
        RegionGlyphs = regionGlyphs;
        Vertices = vertices;
        VertexCells = vertexCells;
        Indices = indices;
        CellCount = cellCount;
        AtlasClass = atlasClass;
        AtlasValue = atlasValue;
        AtlasLayers = atlasLayers;
    }

    /// <summary>Interleaved vertices, <see cref="FloatsPerVertex"/> floats each: unit position, cell centre, texel coordinate (0–16).</summary>
    public float[] Vertices { get; }

    /// <summary>Globe cell id of each vertex (an integer attribute).</summary>
    public uint[] VertexCells { get; }

    /// <summary>Triangle list, a fan from each cell's centre vertex.</summary>
    public uint[] Indices { get; }

    public int CellCount { get; }

    public int VertexCount => VertexCells.Length;

    /// <summary>RGBA8UI texels, <see cref="AtlasSize"/>² per layer: biome, landform, water kind, flow level; null without imagery.</summary>
    public byte[]? AtlasClass { get; }

    /// <summary>RGBA16I texels, <see cref="AtlasSize"/>² per layer: elevation m, temperature deci-°C, precipitation mm, 0; null without imagery.</summary>
    public short[]? AtlasValue { get; }

    /// <summary>Mountain symbols of globe cells (Globe View): land, mountain share at least <see cref="MapRenderer.MountainSymbolMinBp"/>.</summary>
    public GpuGlyphs GlobeGlyphs { get; }

    /// <summary>Mountain symbols of region cells (Region View), grouped by their globe cell (the CPU renderer's <c>EmitRegionGlyphs</c>).</summary>
    public GpuGlyphs RegionGlyphs { get; }

    /// <summary>Layers of the atlas texture arrays (0 without imagery).</summary>
    public int AtlasLayers { get; }

    /// <summary>Rows of the per-cell textures (<see cref="CellTextureWidth"/> wide).</summary>
    public int CellTextureRows => (CellCount + CellTextureWidth - 1) / CellTextureWidth;

    /// <summary>Atlas texel (x, y, layer) of texel (i, j) of <paramref name="cell"/>; the fragment shader computes the same.</summary>
    public static (int X, int Y, int Layer) AtlasTexel(int cell, int i, int j)
    {
        var local = cell % CellsPerLayer;
        return (((local % CellsPerRow) * CellImagery.Size) + i, ((local / CellsPerRow) * CellImagery.Size) + j, cell / CellsPerLayer);
    }

    /// <summary>Builds the mesh (and the atlas when <paramref name="world"/> has imagery). Runs once per world; about 0.3 s at 40,962 cells.</summary>
    public static GpuScene Build(MapWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var grid = world.Globe.Grid;
        var geo = world.Globe.Geometry;
        var cells = grid.CellCount;

        // Vertex and index offsets per cell (fan: ring + 1 vertices, ring triangles).
        var vStart = new int[cells + 1];
        var iStart = new int[cells + 1];
        for (var c = 0; c < cells; c++)
        {
            var ring = grid.CornerTriangles(c).Length;
            vStart[c + 1] = vStart[c] + ring + 1;
            iStart[c + 1] = iStart[c] + (3 * ring);
        }

        var vertices = new float[vStart[cells] * FloatsPerVertex];
        var vertexCells = new uint[vStart[cells]];
        var indices = new uint[iStart[cells]];
        Parallel.For(0, cells, c =>
        {
            var chart = CellChart.For(grid, geo, c);
            var centre = geo.Centre(c);
            var ring = grid.CornerTriangles(c);
            var v0 = vStart[c];
            void Put(int v, Vector3d p)
            {
                var (u, w) = chart.ToTexel(p, CellImagery.Size);
                var o = v * FloatsPerVertex;
                vertices[o] = (float)p.X;
                vertices[o + 1] = (float)p.Y;
                vertices[o + 2] = (float)p.Z;
                vertices[o + 3] = (float)centre.X;
                vertices[o + 4] = (float)centre.Y;
                vertices[o + 5] = (float)centre.Z;
                vertices[o + 6] = (float)u;
                vertices[o + 7] = (float)w;
                vertexCells[v] = (uint)c;
            }

            Put(v0, centre);
            for (var k = 0; k < ring.Length; k++) Put(v0 + 1 + k, geo.Corner(ring[k]));
            var i0 = iStart[c];
            for (var k = 0; k < ring.Length; k++)
            {
                indices[i0 + (3 * k)] = (uint)v0;
                indices[i0 + (3 * k) + 1] = (uint)(v0 + 1 + k);
                indices[i0 + (3 * k) + 2] = (uint)(v0 + 1 + ((k + 1) % ring.Length));
            }
        });

        var globeGlyphs = GpuGlyphs.Build(
            Enumerable.Range(0, cells).Where(c => !world.Globe.IsOcean(c) && LayerPalette.MountainShareBp(world.Globe, c) >= MapRenderer.MountainSymbolMinBp),
            c => geo.Centre(c), c => geo.Corner(grid.CornerTriangles(c)[0]), c => c);
        var regionGlyphs = world.Regions is { } r
            ? GpuGlyphs.Build(
                Enumerable.Range(0, r.Count).Where(c => r.MountainShareBp[c] >= MapRenderer.MountainSymbolMinBp && r.Kind[c] != (byte)Landform.Ocean && r.Kind[c] != (byte)Landform.Lake),
                c => r.Geometry.Centre(c), c => r.Geometry.Corner(r.Grid.CornerTriangles(c)[0]), c => r.GlobeParent[c])
            : GpuGlyphs.Build([], _ => default, _ => default, _ => 0);

        if (world.Imagery is not { } img) return new GpuScene(vertices, vertexCells, indices, cells, null, null, 0, globeGlyphs, regionGlyphs);

        var layers = (cells + CellsPerLayer - 1) / CellsPerLayer;
        var layerTexels = (long)AtlasSize * AtlasSize;
        var atlasClass = new byte[checked(layers * layerTexels * 4)];
        var atlasValue = new short[checked(layers * layerTexels * 4)];
        Parallel.For(0, cells, c =>
        {
            for (var j = 0; j < CellImagery.Size; j++)
                for (var i = 0; i < CellImagery.Size; i++)
                {
                    var k = CellImagery.Index(c, i, j);
                    var (x, y, layer) = AtlasTexel(c, i, j);
                    var o = ((layer * layerTexels) + ((long)y * AtlasSize) + x) * 4;
                    atlasClass[o] = img.Biome[k];
                    atlasClass[o + 1] = img.Landform[k];
                    atlasClass[o + 2] = img.Water[k];
                    atlasClass[o + 3] = img.FlowLevel[k];
                    atlasValue[o] = img.ElevationM[k];
                    atlasValue[o + 1] = img.TemperatureDeciC[k];
                    atlasValue[o + 2] = (short)Math.Min(img.PrecipitationMm[k], (ushort)short.MaxValue);
                }
        });

        return new GpuScene(vertices, vertexCells, indices, cells, atlasClass, atlasValue, layers, globeGlyphs, regionGlyphs);
    }
}

/// <summary>
/// Mountain symbols as GPU data: 3 vertices per symbol (the corner is <c>gl_VertexID % 3</c>), each carrying the cell's
/// centre and first corner (6 floats: the symbol is sized from their projected distance) and its group (globe cell, for
/// the Region View group scaling).
/// </summary>
public sealed record GpuGlyphs(float[] Vertices, uint[] Groups)
{
    public const int FloatsPerVertex = 6;

    public int VertexCount => Groups.Length;

    public static GpuGlyphs Build(IEnumerable<int> cells, Func<int, Vector3d> centre, Func<int, Vector3d> corner, Func<int, int> group)
    {
        var v = new List<float>();
        var g = new List<uint>();
        foreach (var c in cells)
        {
            var a = centre(c);
            var b = corner(c);
            for (var k = 0; k < 3; k++)
            {
                v.AddRange([(float)a.X, (float)a.Y, (float)a.Z, (float)b.X, (float)b.Y, (float)b.Z]);
                g.Add((uint)group(c));
            }
        }

        return new GpuGlyphs([.. v], [.. g]);
    }
}
