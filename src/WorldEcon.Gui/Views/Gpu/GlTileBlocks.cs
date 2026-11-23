using System.Collections.Concurrent;
using Avalonia.OpenGL;
using WorldEcon.Rendering;
using WorldEcon.Rendering.Gpu;

namespace WorldEcon.Gui.Views.Gpu;

/// <summary>
/// GPU meshes of tile blocks (design 2026-11-23-gpu-map-rendering §9, phase 4), owned by the GL thread. Meshes are built
/// on worker threads (<see cref="GpuTileBlock.Build"/>) and uploaded here; a mesh built while a neighbouring block was
/// missing is built again once that block is cached. Least recently drawn meshes beyond <see cref="Capacity"/> are deleted.
/// </summary>
internal sealed unsafe class GlTileBlocks : IDisposable
{
    /// <summary>Meshes kept on the GPU (a 4K Detail View shows about 20–40 blocks, Region View up to a few hundred).</summary>
    public const int Capacity = 600;

    /// <summary>Builds running at once at most.</summary>
    private const int MaxBuildsInFlight = 8;

    /// <summary>Uploads per frame at most (each 0.1–0.3 MB).</summary>
    private const int MaxUploadsPerFrame = 4; // a frame with 16 uploads measured 12 ms of CPU in the zoom sweep

    private readonly GlInterface _gl;
    private readonly GlApi _api;
    private readonly MapRenderer _renderer;
    private readonly Dictionary<int, Mesh> _meshes = [];
    private readonly HashSet<int> _building = [];
    private readonly ConcurrentQueue<GpuTileBlock> _built = new();
    private long _frame;

    public sealed class Mesh
    {
        public int Cell;
        public int Vao;
        public int VertexBuffer;
        public int VertexCount;
        public int RiverVao;
        public int[] RiverBuffers = [];
        public int RiverIndexCount;
        public bool Complete;
        public bool RiversComplete;
        public long LastFrame;
    }

    public GlTileBlocks(GlInterface gl, GlApi api, MapRenderer renderer)
    {
        _gl = gl;
        _api = api;
        _renderer = renderer;
    }

    /// <summary>Raised on a worker thread when a mesh finished building (the view should draw again).</summary>
    public event Action? Built;

    public int Count => _meshes.Count;

    /// <summary>Builds started and not yet uploaded.</summary>
    public int Pending => _building.Count;

    /// <summary>Takes finished builds (at most <see cref="MaxUploadsPerFrame"/>) onto the GPU. GL thread, start of a frame.</summary>
    public void BeginFrame()
    {
        _frame++;
        for (var n = 0; n < MaxUploadsPerFrame && _built.TryDequeue(out var block); n++)
        {
            _building.Remove(block.GlobeCell);
            if (_meshes.Remove(block.GlobeCell, out var old)) Delete(old);
            _meshes[block.GlobeCell] = Upload(block);
        }
    }

    /// <summary>
    /// The mesh of <paramref name="cell"/>'s tile block when it is on the GPU (marked used this frame); starts a build when
    /// the block is cached but has no mesh yet, or when its mesh lacks a neighbour that is now cached. Null while missing.
    /// </summary>
    public Mesh? Get(int cell)
    {
        var cache = _renderer.Cache!;
        var block = cache.TryGet(cell, out _);
        _meshes.TryGetValue(cell, out var mesh);
        if (block is not null && !_building.Contains(cell) && _building.Count < MaxBuildsInFlight
            && (mesh is null || (!mesh.Complete && NeighboursCached(cell)) || (!mesh.RiversComplete && NeighboursCached(cell))))
            StartBuild(block);
        if (mesh is null) return null;
        mesh.LastFrame = _frame;
        return mesh;
    }

    private bool NeighboursCached(int cell)
    {
        foreach (var nb in _renderer.World.Globe.Grid.Neighbours(cell))
            if (_renderer.Cache!.StateOf(nb) is not (TileBlockState.Ready or TileBlockState.Missing)) return false;
        return true;
    }

    private void StartBuild(TileBlockGeometry block)
    {
        _building.Add(block.GlobeCell);
        var cache = _renderer.Cache!;
        _ = Task.Run(() =>
        {
            try
            {
                _built.Enqueue(GpuTileBlock.Build(_renderer, block, c => cache.TryGet(c, out _)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GPU tile block {block.GlobeCell} build failed: {ex.Message}");
            }
            finally
            {
                Built?.Invoke();
            }
        });
    }

    /// <summary>Deletes least recently drawn meshes beyond <see cref="Capacity"/>, never one drawn this frame. GL thread, end of a frame.</summary>
    public void Trim()
    {
        if (_meshes.Count <= Capacity) return;
        foreach (var m in _meshes.Values.Where(m => m.LastFrame < _frame).OrderBy(m => m.LastFrame).Take(_meshes.Count - Capacity).ToList())
        {
            _meshes.Remove(m.Cell);
            Delete(m);
        }
    }

    private int Buffer<T>(int target, T[] data) where T : unmanaged
    {
        var b = _gl.GenBuffer();
        _gl.BindBuffer(target, b);
        fixed (T* p = data) _gl.BufferData(target, (nint)((long)data.Length * sizeof(T)), (nint)p, GlApi.StaticDraw);
        return b;
    }

    private Mesh Upload(GpuTileBlock block)
    {
        var m = new Mesh { Cell = block.GlobeCell, VertexCount = block.VertexCount, Complete = block.Complete, RiversComplete = block.RiversComplete, LastFrame = _frame };
        m.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(m.Vao);
        m.VertexBuffer = Buffer(GlApi.ArrayBuffer, block.Vertices);
        const int s = GpuTileBlock.Stride;
        _gl.VertexAttribPointer(0, 3, GlApi.Float, 0, s, 0);
        _api.VertexAttribIPointer(1, 4, GlApi.UnsignedByte, s, 12);
        _api.VertexAttribIPointer(2, 4, GlApi.UnsignedByte, s, 16);
        _api.VertexAttribIPointer(3, 4, GlApi.UnsignedByte, s, 20);
        _api.VertexAttribIPointer(4, 3, GlApi.Short, s, 24);
        _api.VertexAttribIPointer(5, 3, GlApi.Short, s, 30);
        _api.VertexAttribIPointer(6, 3, GlApi.Short, s, 36);
        for (var i = 0; i <= 6; i++) _gl.EnableVertexAttribArray(i);

        var rivers = block.Rivers;
        m.RiverVao = _gl.GenVertexArray();
        if (rivers.PieceCount > 0)
        {
            _gl.BindVertexArray(m.RiverVao);
            m.RiverBuffers = GlMapRenderer.SetUpRiverAttributes(_gl, _api, rivers, (t, d) => Buffer(t, d), (t, d) => Buffer(t, d));
            m.RiverIndexCount = rivers.Indices.Length;
        }

        _gl.BindVertexArray(0);
        return m;
    }

    private void Delete(Mesh m)
    {
        _gl.DeleteVertexArray(m.Vao);
        _gl.DeleteBuffer(m.VertexBuffer);
        _gl.DeleteVertexArray(m.RiverVao);
        foreach (var b in m.RiverBuffers) _gl.DeleteBuffer(b);
    }

    public void Dispose()
    {
        foreach (var m in _meshes.Values) Delete(m);
        _meshes.Clear();
    }
}
