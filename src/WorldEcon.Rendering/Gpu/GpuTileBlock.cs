using System.Numerics;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering.Gpu;

/// <summary>
/// One tile block as GPU data (design 2026-11-23-gpu-map-rendering §6, phase 4): the triangles between three
/// neighbouring tile centres (the CPU renderer's <c>EmitTileTriangles</c>), each vertex carrying the class and value
/// channels of all three tiles so <c>tile.frag</c> can blend them as <c>BlendSink</c> does, plus the block's river lines.
/// Layer-independent: a layer change needs no rebuild. Built on a worker thread when the block is cached; rebuilt once
/// a neighbouring block that was missing arrives (<see cref="Complete"/> false).
/// </summary>
public sealed class GpuTileBlock
{
    /// <summary>Bytes per vertex: position (3 × f32), class A/B/C (3 × RGBA8UI), value A/B/C (3 × 3 × i16), 2 bytes padding.</summary>
    public const int Stride = 44;

    private GpuTileBlock(int globeCell, byte[] vertices, int vertexCount, bool complete, GpuRivers rivers, bool riversComplete)
    {
        GlobeCell = globeCell;
        Vertices = vertices;
        VertexCount = vertexCount;
        Complete = complete;
        Rivers = rivers;
        RiversComplete = riversComplete;
    }

    public int GlobeCell { get; }

    /// <summary>Interleaved vertex bytes (<see cref="Stride"/> each), triangles in order (corner = vertex index mod 3).</summary>
    public byte[] Vertices { get; }

    public int VertexCount { get; }

    /// <summary>False when a triangle vertex lies in a neighbouring block that was not loaded (it took the emitting tile's data).</summary>
    public bool Complete { get; }

    /// <summary>The block's smoothed river lines as one river level (widths on the tile scale).</summary>
    public GpuRivers Rivers { get; }

    /// <summary>False until every block the rivers flow into was loaded.</summary>
    public bool RiversComplete { get; }

    /// <summary>
    /// Builds block <paramref name="e"/>; <paramref name="blockOf"/> returns a loaded neighbouring block by globe cell, or
    /// null (as the tile cache's <c>TryGet</c>).
    /// </summary>
    public static GpuTileBlock Build(MapRenderer renderer, TileBlockGeometry e, Func<int, TileBlockGeometry?> blockOf)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(blockOf);
        var w = renderer.World;
        var tiles = w.Tiles ?? throw new InvalidOperationException("The world has no tile grid.");
        var ids = e.Block.TileIds;
        var complete = true;
        var bytes = new List<byte>(ids.Length * 2 * 3 * Stride);
        Span<int> nb = stackalloc int[6];
        Span<byte> v = stackalloc byte[Stride];

        // Data source of a triangle corner: (block, local index), or the emitting tile when its block is not loaded.
        (TileBlockGeometry Block, int Local) Source(int tile, int local, int own)
        {
            if (local >= 0) return (e, local);
            if (blockOf(GridHierarchy.Parent(tiles, w.GlobeTiles, tile)) is { } other)
            {
                var l2 = Array.BinarySearch(other.Block.TileIds, tile);
                if (l2 >= 0) return (other, l2);
            }

            complete = false;
            return (e, own);
        }

        for (var i = 0; i < ids.Length; i++)
        {
            var n = tiles.Neighbours(ids[i], nb);
            for (var k = 0; k < n; k++)
            {
                int j = nb[k], m = nb[(k + 1) % n];
                int lj = Array.BinarySearch(ids, j), lm = Array.BinarySearch(ids, m);
                if ((lj >= 0 && j < ids[i]) || (lm >= 0 && m < ids[i])) continue;
                var a = (e, i);
                var b = Source(j, lj, i);
                var c = Source(m, lm, i);
                Vector3 pa = e.Centres[i];
                var pb = lj >= 0 ? e.Centres[lj] : ToF(tiles.Centre(j));
                var pc = lm >= 0 ? e.Centres[lm] : ToF(tiles.Centre(m));
                foreach (var p in (ReadOnlySpan<Vector3>)[pa, pb, pc])
                {
                    v.Clear();
                    BitConverter.TryWriteBytes(v[0..], p.X);
                    BitConverter.TryWriteBytes(v[4..], p.Y);
                    BitConverter.TryWriteBytes(v[8..], p.Z);
                    WriteClass(v[12..], a);
                    WriteClass(v[16..], b);
                    WriteClass(v[20..], c);
                    WriteValue(v[24..], a);
                    WriteValue(v[30..], b);
                    WriteValue(v[36..], c);
                    foreach (var x in v) bytes.Add(x);
                }
            }
        }

        var river = renderer.BlockRiverCourse(e);
        var rivers = GpuRivers.FromLevels(river is { } r ? [r.Level] : []);
        return new GpuTileBlock(e.GlobeCell, [.. bytes], bytes.Count / Stride, complete, rivers, river?.Complete ?? true);
    }

    private static void WriteClass(Span<byte> d, (TileBlockGeometry Block, int Local) s)
    {
        var b = s.Block.Block;
        d[0] = b.Biome[s.Local];
        d[1] = b.Landform[s.Local];
        d[2] = b.Water[s.Local];
        d[3] = CellImagery.FlowToLevel(b.Flow[s.Local]);
    }

    private static void WriteValue(Span<byte> d, (TileBlockGeometry Block, int Local) s)
    {
        var b = s.Block.Block;
        BitConverter.TryWriteBytes(d[0..], b.ElevationM[s.Local]);
        BitConverter.TryWriteBytes(d[2..], b.TemperatureDeciC[s.Local]);
        BitConverter.TryWriteBytes(d[4..], (short)Math.Min(b.PrecipitationMm[s.Local], (ushort)short.MaxValue));
    }

    private static Vector3 ToF(Vector3d p) => new((float)p.X, (float)p.Y, (float)p.Z);
}
