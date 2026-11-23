using System.Numerics;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering;

/// <summary>
/// A decoded <see cref="TileBlock"/> plus its unit-sphere geometry as float arrays, built once off the draw thread.
/// Immutable after construction except <see cref="ColoursFor"/>'s per-layer colour cache, which only the draw thread
/// (inside <see cref="MapRenderer"/>'s lock) touches.
/// </summary>
public sealed class TileBlockGeometry
{
    private readonly uint[]?[] _colours = new uint[]?[Enum.GetValues<GlobeLayer>().Length];

    internal TileBlockGeometry(TileBlock block, TileGrid tiles, TileGrid globeTiles)
    {
        Block = block;
        var n = block.Count;
        Centres = new Vector3[n];
        Corners = new Vector3[n * 6];
        CornerCount = new byte[n];
        Span<Vector3d> corners = stackalloc Vector3d[6];
        Span<int> ring = stackalloc int[6];
        var river = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var id = block.TileIds[i];
            Centres[i] = ToF(tiles.Centre(id));
            var k = tiles.Corners(id, corners);
            CornerCount[i] = (byte)k;
            for (var j = 0; j < k; j++) Corners[(i * 6) + j] = ToF(corners[j]);
            if (block.Downstream[i] >= 0) river.Add(i);
        }

        // Downstream end of every draining tile (the downstream tile may belong to another globe cell).
        RiverTiles = [.. river];
        RiverTo = new Vector3[RiverTiles.Length];
        RiverToGlobe = new int[RiverTiles.Length];
        RiverToOcean = new bool[RiverTiles.Length];
        RiverToTile = new int[RiverTiles.Length];
        RiverToLake = new bool[RiverTiles.Length];
        for (var r = 0; r < RiverTiles.Length; r++)
        {
            var i = RiverTiles[r];
            var ringSize = tiles.Neighbours(block.TileIds[i], ring);
            if (block.Downstream[i] >= ringSize)
                throw new InvalidDataException($"Tile {block.TileIds[i]} of globe cell {block.GlobeCell} has downstream slot {block.Downstream[i]}; it has {ringSize} neighbours.");
            var to = ring[block.Downstream[i]];
            RiverToTile[r] = to;
            RiverTo[r] = ToF(tiles.Centre(to));
            RiverToGlobe[r] = GridHierarchy.Parent(tiles, globeTiles, to);
            var local = Array.BinarySearch(block.TileIds, to);
            RiverToOcean[r] = local >= 0 && block.Water[local] == (byte)TileWaterKind.Ocean;
            RiverToLake[r] = local >= 0 && block.Water[local] == (byte)TileWaterKind.Lake;
        }

        // Steps to the block exit: 0 when the next tile is outside the block, is water, or does not drain; otherwise the
        // next tile's count + 1. Walked iteratively along each path, filling every tile on it.
        var riverIndex = new int[n];
        Array.Fill(riverIndex, -1);
        for (var r = 0; r < RiverTiles.Length; r++) riverIndex[RiverTiles[r]] = r;
        const int visiting = -2;
        StepsToExit = new int[RiverTiles.Length];
        Array.Fill(StepsToExit, -1);
        var path = new List<int>();
        for (var r0 = 0; r0 < RiverTiles.Length; r0++)
        {
            if (StepsToExit[r0] >= 0) continue;
            path.Clear();
            var r = r0;
            var v = 0;
            while (true)
            {
                if (StepsToExit[r] >= 0) { v = StepsToExit[r] + 1; break; } // joins a counted path
                if (StepsToExit[r] == visiting) break; // a loop: cut it here
                StepsToExit[r] = visiting;
                path.Add(r);
                var local = Array.BinarySearch(block.TileIds, RiverToTile[r]);
                var next = local < 0 || RiverToOcean[r] || RiverToLake[r] ? -1 : riverIndex[local];
                if (next < 0) break;
                r = next;
            }

            for (var k = path.Count - 1; k >= 0; k--) StepsToExit[path[k]] = v++;
        }
    }

    public TileBlock Block { get; }

    public int GlobeCell => Block.GlobeCell;

    /// <summary>Unit tile centres, index as in <see cref="TileBlock.TileIds"/>.</summary>
    public Vector3[] Centres { get; }

    /// <summary>Unit corners, 6 slots per tile (5 used by pentagons), corner k between neighbour k and k + 1.</summary>
    public Vector3[] Corners { get; }

    public byte[] CornerCount { get; }

    /// <summary>Block indices of tiles with a downstream neighbour.</summary>
    public int[] RiverTiles { get; }

    /// <summary>Unit centre of the downstream tile of <see cref="RiverTiles"/>[r].</summary>
    public Vector3[] RiverTo { get; }

    /// <summary>Tile id of the downstream tile of <see cref="RiverTiles"/>[r].</summary>
    public int[] RiverToTile { get; }

    /// <summary>Globe cell (tile-level parent) of the downstream tile.</summary>
    public int[] RiverToGlobe { get; }

    /// <summary>True when the downstream tile is an ocean tile of this block (the river line then stops at the coast). A downstream tile in another block is checked at draw time against that block when it is loaded (<see cref="IsOcean"/>).</summary>
    public bool[] RiverToOcean { get; }

    /// <summary>True when the downstream tile is a lake tile of this block (see <see cref="IsLake"/> for other blocks).</summary>
    public bool[] RiverToLake { get; }

    /// <summary>
    /// Per <see cref="RiverTiles"/> entry: downstream steps until the river leaves this block or reaches water (0 when
    /// the next tile is outside the block, is ocean or lake, or does not drain). River arrows at Detail View sit on tiles
    /// where this is a multiple of the arrow step, so spacing restarts at block borders.
    /// </summary>
    public int[] StepsToExit { get; }

    /// <summary>True when <paramref name="tile"/> is in this block and is a lake tile.</summary>
    public bool IsLake(int tile)
    {
        var local = Array.BinarySearch(Block.TileIds, tile);
        return local >= 0 && Block.Water[local] == (byte)TileWaterKind.Lake;
    }

    /// <summary>True when <paramref name="tile"/> is in this block and is an ocean tile.</summary>
    public bool IsOcean(int tile)
    {
        var local = Array.BinarySearch(Block.TileIds, tile);
        return local >= 0 && Block.Water[local] == (byte)TileWaterKind.Ocean;
    }

    /// <summary>Tile colours for a layer, computed once per layer by <paramref name="build"/>. Call from the draw thread only.</summary>
    internal uint[] ColoursFor(GlobeLayer layer, Func<TileBlockGeometry, GlobeLayer, uint[]> build)
    {
        var i = (int)layer;
        return _colours[i] ??= build(this, layer);
    }

    private static Vector3 ToF(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}

/// <summary>Load state of a globe cell's tile block in <see cref="TileBlockCache"/>.</summary>
public enum TileBlockState
{
    /// <summary>Not cached and not being loaded.</summary>
    NotLoaded,

    /// <summary>A load is queued or running.</summary>
    Loading,

    /// <summary>Decoded with geometry.</summary>
    Ready,

    /// <summary>The source has no block for the cell (it is drawn with region cells).</summary>
    Missing,

    /// <summary>
    /// The load threw; the exception is in <see cref="TileBlockCache.LastError"/>. Drawn with region cells; retried by the
    /// next request after a backoff (<see cref="TileBlockCache.RetryDelay"/> doubling per failure of that cell, at most
    /// <see cref="TileBlockCache.MaxRetryDelay"/>).
    /// </summary>
    Failed,
}

/// <summary>
/// Bounded least-recently-used cache of tile blocks with their geometry (<see cref="TileBlockGeometry"/>), keyed by globe
/// cell. <see cref="Request"/> starts a background load (a <see cref="Task"/> on the thread pool; at most one in flight
/// per cell; at most <see cref="MaxConcurrency"/> loading at once); <see cref="TilesLoaded"/> is raised on the loading
/// thread when a block is ready, missing or failed, and again when a failed block's backoff has passed (the redraw that
/// follows requests it again), so the user interface can invalidate. A queued load whose cell was not
/// requested during the last <see cref="StaleFrames"/> frames (<see cref="BeginFrame"/>) is dropped before it reads
/// the source, so panning away does not leave a backlog. Eviction never removes an entry used (read with
/// <see cref="TryGet"/>, requested, or inserted) in the current or the previous frame, so the cache holds every visible
/// block even when more globe cells are visible than <see cref="Capacity"/>.
/// </summary>
/// <remarks>
/// Thread safety: every public member may be called from any thread. The LRU (least recently used) list, the state table and the in-flight
/// table are guarded by one lock; the source is called outside the lock (concurrently, up to <see cref="MaxConcurrency"/>
/// calls; a source that is not thread-safe must serialise itself, as <c>GlobeStore</c> does). Entries returned by
/// <see cref="TryGet"/> are immutable except their colour cache (see <see cref="TileBlockGeometry"/>).
/// </remarks>
public sealed class TileBlockCache : IDisposable
{
    /// <summary>Default capacity in globe cells (spec §11).</summary>
    public const int DefaultCapacity = 256;

    /// <summary>A queued load older than this many frames is dropped.</summary>
    public const int StaleFrames = 2;

    /// <summary>An entry used within this many frames before the current one is not evicted (1: the current and the previous frame).</summary>
    public const int PinnedFrames = 1;

    /// <summary>Longest wait before a failed block is loaded again.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ITileBlockSource _source;
    private readonly TileGrid _tiles;
    private readonly TileGrid _globeTiles;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new(); // first = most recently used
    private readonly Dictionary<int, Task> _inFlight = [];
    private readonly Dictionary<int, long> _requestedFrame = [];
    private readonly Dictionary<int, int> _failures = []; // consecutive failed loads per cell
    private Exception? _lastError;
    private long _frame;
    private int _loadsStarted;
    private int _evictions;
    private bool _disposed;

    private sealed class Entry(int cell, TileBlockState state, TileBlockGeometry? geometry)
    {
        public int Cell { get; } = cell;

        public TileBlockState State { get; } = state;

        public TileBlockGeometry? Geometry { get; } = geometry;

        /// <summary>Failed entries: <see cref="Environment.TickCount64"/> from which a request loads the cell again.</summary>
        public long RetryAtMs { get; init; }

        /// <summary>Last frame (<see cref="BeginFrame"/> count) in which the cell was read, requested or inserted; guarded by the cache lock.</summary>
        public long LastFrame { get; set; }
    }

    /// <param name="retryDelay">Wait after the first failed load of a cell before it is loaded again (doubles per
    /// further failure, at most <see cref="MaxRetryDelay"/>); default 1 s.</param>
    public TileBlockCache(ITileBlockSource source, TileGrid tiles, TileGrid globeTiles, int capacity = DefaultCapacity, int maxConcurrency = 2, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(globeTiles);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _source = source;
        _tiles = tiles;
        _globeTiles = globeTiles;
        Capacity = capacity;
        MaxConcurrency = maxConcurrency;
        RetryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryDelay, TimeSpan.Zero, nameof(retryDelay));
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>Raised (on a thread-pool thread) with the globe cell when its load finished (ready, missing or failed).</summary>
    public event EventHandler<int>? TilesLoaded;

    public int Capacity { get; }

    public int MaxConcurrency { get; }

    /// <summary>Wait after the first failure of a cell before the next load.</summary>
    public TimeSpan RetryDelay { get; }

    /// <summary>Loads that reached the source (dropped stale requests are not counted).</summary>
    public int LoadsStarted => Volatile.Read(ref _loadsStarted);

    /// <summary>Entries evicted since creation.</summary>
    public int Evictions => Volatile.Read(ref _evictions);

    /// <summary>Last exception thrown by the source or the geometry build (null after a later successful load of every failed cell).</summary>
    public Exception? LastError => Volatile.Read(ref _lastError);

    /// <summary>Cached entries whose load failed (drawn with region cells until a retry succeeds).</summary>
    public int FailedCount
    {
        get
        {
            lock (_gate) return _entries.Values.Count(n => n.Value.State == TileBlockState.Failed);
        }
    }

    /// <summary>Cached entries for which the source has no block.</summary>
    public int MissingCount
    {
        get
        {
            lock (_gate) return _entries.Values.Count(n => n.Value.State == TileBlockState.Missing);
        }
    }

    /// <summary>Cached entries (ready, missing or failed).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>Marks the start of a draw; requests made since then count as fresh.</summary>
    public void BeginFrame()
    {
        lock (_gate) _frame++;
    }

    /// <summary>State of a cell without touching the LRU order.</summary>
    public TileBlockState StateOf(int cell)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(cell, out var node)) return node.Value.State;
            return _inFlight.ContainsKey(cell) ? TileBlockState.Loading : TileBlockState.NotLoaded;
        }
    }

    /// <summary>
    /// The cached geometry of a cell (moved to most recently used), or null when it is not ready (any other state).
    /// </summary>
    public TileBlockGeometry? TryGet(int cell, out TileBlockState state)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(cell, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                node.Value.LastFrame = _frame;
                state = node.Value.State;
                return node.Value.Geometry;
            }

            state = _inFlight.ContainsKey(cell) ? TileBlockState.Loading : TileBlockState.NotLoaded;
            return null;
        }
    }

    /// <summary>Starts loading a cell unless it is cached or in flight; refreshes the request frame of a queued load.</summary>
    public void Request(int cell) => Start(cell, force: false);

    /// <summary>Loads a cell (no stale drop) and completes when it is cached; completes at once when it already is.</summary>
    public Task EnsureLoadedAsync(int cell) => Start(cell, force: true);

    /// <summary>Drops every cached entry (in-flight loads still complete and insert).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _failures.Clear();
            Volatile.Write(ref _lastError, null);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _cts.Cancel();
        _cts.Dispose();
    }

    private Task Start(int cell, bool force)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_entries.TryGetValue(cell, out var cached))
            {
                cached.Value.LastFrame = _frame;
                if (cached.Value.State != TileBlockState.Failed || Environment.TickCount64 < cached.Value.RetryAtMs) return Task.CompletedTask;
                // Backoff passed: load the failed cell again (the entry is replaced when the load completes).
                _lru.Remove(cached);
                _entries.Remove(cell);
            }

            _requestedFrame[cell] = force ? long.MaxValue : Math.Max(_frame, _requestedFrame.GetValueOrDefault(cell));
            if (_inFlight.TryGetValue(cell, out var running)) return running;
            var task = Task.Run(() => LoadAsync(cell));
            _inFlight[cell] = task;
            return task;
        }
    }

    /// <summary>
    /// Evicts least recently used entries until <see cref="Count"/> ≤ <see cref="Capacity"/>, skipping entries used in
    /// the current or the previous frame (<see cref="PinnedFrames"/>): a block visible in a frame is never evicted by a
    /// load that completes during that frame or before the next draw has read it again, so a view with more visible globe
    /// cells than the capacity holds all of them (the count exceeds the capacity until later frames stop using some).
    /// Caller holds the lock.
    /// </summary>
    private void EvictUnpinned()
    {
        var node = _lru.Last;
        while (_entries.Count > Capacity && node is not null)
        {
            var previous = node.Previous;
            if (_frame - node.Value.LastFrame > PinnedFrames)
            {
                _lru.Remove(node);
                _entries.Remove(node.Value.Cell);
                _evictions++;
            }

            node = previous;
        }
    }

    private async Task LoadAsync(int cell)
    {
        var ct = _cts.Token;
        var acquired = false;
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            lock (_gate)
            {
                // Dropped when the cell has not been requested recently (the view moved on).
                if (_frame - _requestedFrame.GetValueOrDefault(cell) > StaleFrames)
                {
                    _inFlight.Remove(cell);
                    _requestedFrame.Remove(cell);
                    return;
                }
            }

            Interlocked.Increment(ref _loadsStarted);
            Entry entry;
            try
            {
                var block = await _source.LoadAsync(cell, ct).ConfigureAwait(false);
                entry = block is null
                    ? new Entry(cell, TileBlockState.Missing, null)
                    : new Entry(cell, TileBlockState.Ready, new TileBlockGeometry(block, _tiles, _globeTiles));
                lock (_gate)
                {
                    _failures.Remove(cell);
                    if (_failures.Count == 0) Volatile.Write(ref _lastError, null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, ex);
                TimeSpan delay;
                lock (_gate)
                {
                    var failures = _failures.GetValueOrDefault(cell) + 1;
                    _failures[cell] = failures;
                    var ms = Math.Min(MaxRetryDelay.TotalMilliseconds, RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 30)));
                    delay = TimeSpan.FromMilliseconds(ms);
                }

                entry = new Entry(cell, TileBlockState.Failed, null) { RetryAtMs = Environment.TickCount64 + (long)delay.TotalMilliseconds };
                // Raise TilesLoaded again when the backoff has passed, so a redraw requests the cell again.
                _ = Task.Delay(delay, ct).ContinueWith(_ => TilesLoaded?.Invoke(this, cell), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }

            lock (_gate)
            {
                _inFlight.Remove(cell);
                _requestedFrame.Remove(cell);
                if (_disposed) return;
                if (_entries.TryGetValue(cell, out var old)) _lru.Remove(old);
                entry.LastFrame = _frame;
                _entries[cell] = _lru.AddFirst(entry);
                EvictUnpinned();
            }

            TilesLoaded?.Invoke(this, cell);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _inFlight.Remove(cell);
                _requestedFrame.Remove(cell);
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed while waiting for a slot.
        }
        finally
        {
            if (acquired && !_disposed) _slots.Release();
        }
    }
}
