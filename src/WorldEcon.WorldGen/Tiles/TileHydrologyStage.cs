using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Tile stage 4 (design spec §6 step 7): drainage, lakes, dry lake beds and rivers per tile. No randomness. Sequential
/// except the per-tile passes (contribution, final classification), which write only their own index, so the output does
/// not depend on the thread count.
/// <para>
/// Flood. A priority flood (Barnes et al. 2014) over <see cref="TileGrid.Neighbours"/>: a binary min-heap of keys
/// (filled level, tile id) packed into one <see cref="long"/> per entry, held in one array. Every ocean tile is visited
/// at the start; the ocean tiles with a land neighbour are pushed at their own elevation. Each pop visits the unvisited
/// neighbours in slot order and gives each filled = max(own elevation, popped level) and the popped tile as flood parent.
/// Keys are unique (the id breaks ties), so the order is deterministic. The flood parent is a neighbour with the lowest
/// filled level; among equal levels the earliest popped, so flats drain in flood order. A world without ocean is seeded
/// with its lowest tile (ties: lowest id), a sink without downstream (the synthetic outlet, as on the globe level).
/// </para>
/// <para>
/// Basins. A land tile with filled &gt; elevation is in a depression; neighbouring depression tiles have the same filled
/// level, so a connected component of them has one spill level L. A component shallower than
/// <see cref="TilesSettings.MinBasinDepthM"/> (L − lowest elevation) drains through along its flood parents. Every
/// other component is a basin. Basins are resolved in reverse pop order of their earliest-popped tile (the basin outlet,
/// whose flood parent is the spill tile outside the basin), so each basin is resolved after every basin upstream of it.
/// </para>
/// <para>
/// Nested depressions inside a basin (merge tree). The basin's tiles are added in (elevation, id) order to a union-find:
/// a tile with no added neighbour starts a new pit (a leaf node); a tile that joins two components (a saddle at elevation
/// s) merges them. A component shallower than MinBasinDepthM at the saddle (s − its floor &lt; MinBasinDepthM) is absorbed
/// into the other one (when both are, the one with the higher floor, ties the later node): it holds no water of its own.
/// Otherwise the merge creates an internal node with the two components as children, each with top level s. The root's
/// top is L. Each tile drains to its lowest neighbour added before it (steepest descent; ties by (elevation, id)); its
/// water goes to the pit its descent reaches. An absorbed pit's water and descent are redirected over its saddle into the
/// other component (the descent path from the saddle's neighbour to the absorbed pit is reversed).
/// </para>
/// <para>
/// Water balance (fill and spill). Evaporation of a tile e = round((<see cref="HydrologySettings.EvaporationBaseMm"/> +
/// <see cref="HydrologySettings.EvaporationMmPerDegree"/> × max(0, tile °C)) × tile area km²), mm·km²/yr; E(node, h) = Σ e
/// over the node's tiles below h. Each pit receives the flow of the tiles draining to it (their precipitation × area
/// plus inflow from outside the basin). A node is full when its water reaches E(node, top). The excess spills over the
/// saddle: into the sibling (at the pit that the saddle's neighbour on the sibling side drains to) when the sibling is
/// not full, else both children form the parent's lake with their combined water, which may fill further. The root's
/// excess leaves the basin. A full inner lake therefore feeds the outer basin.
/// </para>
/// <para>
/// Lakes. A lake node is a node holding water (a pit, or a node whose children are both full) whose parent does not.
/// Full: water level = its top. Otherwise the level is the lowest elevation e such that E(node, e + 1) exceeds its
/// water (whole elevation bins are flooded, so a lake has one level). Level − floor &lt; max(MinBasinDepthM, 1): a
/// <see cref="TileWaterKind.DryLakeBed"/> covering the node's tiles below floor + max(MinBasinDepthM, 1); otherwise a
/// <see cref="TileWaterKind.Lake"/> covering its tiles below the level. Each connected set of such tiles is one
/// <see cref="TileLake"/>.
/// </para>
/// <para>
/// Routing. Inside each lake or dry bed a priority flood restricted to its tiles (keys (max(elevation, popped level),
/// id)) sets each tile's downstream toward a seed: the outlet for a full lake, else the lowest tile, which has no
/// downstream (endorheic sink; terminal lake or dry lake bed). A full inner lake drains over its saddle: the descent path
/// from the saddle's neighbour on the lake side to the lake is reversed, that neighbour drains to the saddle tile, and
/// the saddle tile drains to its neighbour on the sibling side (first assignment wins when a saddle merges more than two
/// components). A lake whose water reaches the shared saddle of children inside it but does not flood the saddle's
/// elevation bin is several wet components at one level; they are linked over those saddles (descent path reversed as
/// above) into one tree ending at the lake's exit, or for a lake without exit at the component holding its lowest tile.
/// A set of wet tiles holding no pit (an absorbed pit flooded above its saddle level but cut off from the lake) stays
/// land. The root lake when full covers the whole basin and its outlet drains to the spill tile. Tiles outside
/// lakes keep their descent. Flow is accumulated along this routing (Kahn order inside the basin). A full lake's
/// evaporation (all its components) is booked once at its exit tile, where all the water the balance delivered to the
/// lake node passes: the outlet for the root lake, the saddle tile for an inner lake (water from the reversed exit path and
/// from the saddle tile itself reaches the saddle without crossing lake tiles, but the balance counted it for the lake).
/// The exit tile passes its flow − min(flow, evaporation) downstream (<see cref="TileLake.ExitTile"/>).
/// Shallow merges use max(MinBasinDepthM, 1), so at MinBasinDepthM 0 a pit on a flat floor (saddle at the floor
/// elevation) is absorbed instead of forming a node with top = floor.
/// </para>
/// <para>
/// Hydrological conditioning (the stage changes <see cref="TileLayers.ElevationM"/>). A depression that drains through
/// is raised to its filled level; the dry tiles of an absorbed pit are raised to its saddle elevation (sediment fill).
/// Outlet breaching: a basin whose root lake is full drains when its depth (spill level − lowest elevation) is at most
/// <see cref="TilesSettings.BreachDepthM"/> × (outflow / <see cref="TilesSettings.MajorRiverFlow"/>)^(exponent), outflow
/// = basin inflow − evaporation of the filled basin; it is carved and filled to T = max(lowest elevation, tile sea
/// level): the path from the lowest tile to the outlet is set to T and, after every basin is resolved, the downstream
/// drainage path is lowered to T until the first tile at or below T, a water tile or a tile bordering a lake above T, the rest of the basin is raised to a priority-flood surface from the outlet, and tiles below T
/// connected to the lowest tile (floor below sea level) stay a full lake at level T draining through its first-reached
/// tile, its evaporation booked at the basin outlet (partial breach). A final pass raises each non-water land tile to at least its non-water downstream tile
/// (channels of two breaches meeting at different levels), so after the stage no land tile outside water lies below a
/// tile on its drainage path. Known limit: when the channel of a partial breach stops at its first tile (the spill tile
/// borders a lake above T), that pass raises the path from the residual lake to the spill tile above T while the lake
/// keeps level T, so its water exits over higher tiles (earth-5km seed 42: 5 of 1,641 lakes with an outlet, largest gap
/// 117 m; bounded by <c>TileStagesDefaultPresetTests</c>). Re-flooding the lake to the exit level would change its area,
/// evaporation and the flow downstream.
/// </para>
/// <para>
/// Flow outside basins: a tile adds its flow to its flood parent in reverse pop order; flow arriving on an ocean tile is
/// reset to 0. Rivers: land tiles outside lakes and dry beds with flow ≥ <see cref="TilesSettings.RiverMinFlow"/>.
/// </para>
/// </summary>
public static class TileHydrologyStage
{
    private const int Shallow = -2; // temporary LakeId of a depression too shallow to be a basin
    // Wet basin tiles hold −3 − lake id during the run, so they do not collide with basin indices ≥ 0.

    public static void Run(TileLayers tiles, WorldGenSettings settings, ParallelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(settings);
        if (tiles.ElevationM.Length != tiles.Count || tiles.Ocean.Length != tiles.Count
            || tiles.PrecipitationMm.Length != tiles.Count || tiles.TemperatureDeciC.Length != tiles.Count)
            throw new ArgumentException("Tile layers must have completed TileElevation, TileSeaLevel and TileClimate.", nameof(tiles));

        var grid = tiles.Grid;
        var n = grid.Count;
        var elev = tiles.ElevationM;
        var ocean = tiles.Ocean;
        double radius = settings.Grid.PlanetRadiusKm;
        var hydro = settings.Hydrology;

        var filled = new short[n];
        var parent = new int[n];
        var visited = new bool[n];
        var heap = new MinHeap(1 << 16);
        var order = Flood(tiles, filled, parent, visited, heap, options, out var popped);
        visited = null!;

        // Basins: components of depression tiles, numbered in pop order of their earliest tile.
        var lakeId = new int[n];
        Array.Fill(lakeId, -1);
        var basinStart = new List<int>();
        var basinTiles = new List<int>();
        var component = new List<int>();
        Span<int> ring = stackalloc int[6];
        for (int i = 0; i < popped; i++)
        {
            int t = order[i];
            if (lakeId[t] != -1 || filled[t] <= elev[t]) continue;
            component.Clear();
            component.Add(t);
            lakeId[t] = Shallow;
            int lowest = elev[t];
            for (int head = 0; head < component.Count; head++)
            {
                int c = component[head];
                lowest = Math.Min(lowest, elev[c]);
                int k = grid.Neighbours(c, ring);
                for (int s = 0; s < k; s++)
                {
                    int u = ring[s];
                    if (lakeId[u] != -1 || ocean[u] || filled[u] <= elev[u]) continue;
                    lakeId[u] = Shallow;
                    component.Add(u);
                }
            }

            if (filled[t] - lowest < settings.Tiles.MinBasinDepthM)
            {
                // Drains through: sediment fill to the spill level, so the flood-parent path never rises.
                foreach (var c in component) elev[c] = filled[c];
                continue;
            }
            int b = basinStart.Count;
            basinStart.Add(basinTiles.Count);
            foreach (var c in component) lakeId[c] = b;
            basinTiles.Add(t); // the outlet first
            foreach (var c in component)
                if (c != t) basinTiles.Add(c);
        }
        int basins = basinStart.Count;
        basinStart.Add(basinTiles.Count);
        var basinTileArray = basinTiles.ToArray();
        basinTiles = null!;

        var flow = new long[n];
        var precipitation = tiles.PrecipitationMm;
        TileParallel.For(n, options, (s, e) =>
        {
            for (int t = s; t < e; t++)
                if (!ocean[t]) flow[t] = (long)Math.Round(precipitation[t] * grid.AreaKm2(t, radius));
        });

        var remaining = new int[basins];
        for (int b = 0; b < basins; b++) remaining[b] = basinStart[b + 1] - basinStart[b];
        var resolver = new BasinResolver(tiles, settings, lakeId, parent, flow, filled, heap, n);
        for (int i = popped - 1; i >= 0; i--)
        {
            int t = order[i];
            int b = lakeId[t];
            if (b < 0)
            {
                int p = parent[t];
                if (p >= 0) flow[p] += flow[t];
                continue;
            }
            if (--remaining[b] > 0) continue;
            // t is the basin outlet: every basin tile and everything upstream of the basin has been processed.
            resolver.Resolve(b, new ArraySegment<int>(basinTileArray, basinStart[b], basinStart[b + 1] - basinStart[b]));
        }

        var lakes = resolver.Lakes;
        var lakeKind = new byte[lakes.Count];
        for (int i = 0; i < lakes.Count; i++) lakeKind[i] = (byte)lakes[i].Kind;

        var water = new byte[n];
        var slot = new sbyte[n];
        long riverMin = settings.Tiles.RiverMinFlow;
        TileParallel.For(n, options, (s, e) =>
        {
            Span<int> nb = stackalloc int[6];
            for (int t = s; t < e; t++)
            {
                slot[t] = -1;
                if (ocean[t])
                {
                    water[t] = (byte)TileWaterKind.Ocean;
                    flow[t] = 0;
                    continue;
                }

                int v = lakeId[t];
                int id = v <= -3 ? -3 - v : -1;
                lakeId[t] = id;
                water[t] = id >= 0 ? lakeKind[id] : (byte)(flow[t] >= riverMin ? TileWaterKind.River : TileWaterKind.None);
                int d = parent[t];
                if (d < 0) continue;
                int k = grid.Neighbours(t, nb);
                for (int x = 0; x < k; x++)
                {
                    if (nb[x] != d) continue;
                    slot[t] = (sbyte)x;
                    break;
                }
            }
        });

        CarveChannels(tiles, resolver.Channels, water, lakeId, lakes, parent);
        RaiseAlongDrainage(elev, water, parent, n);
        tiles.WaterKind = water;
        tiles.LakeId = lakeId;
        tiles.DownstreamSlot = slot;
        tiles.FlowAccum = flow;
        tiles.Lakes = lakes;
    }

    // Breach channels downstream of the outlets (see the class comment), in resolution order: from the spill tile along
    // the final drainage, each land tile that is not water, is above the channel level and does not border a lake or dry
    // lake bed whose level is above the channel level is lowered to it; the walk stops at the first other tile.
    private static void CarveChannels(TileLayers tiles, List<(int Start, short Level)> channels, byte[] water, int[] lakeId,
        List<TileLake> lakes, int[] down)
    {
        var grid = tiles.Grid;
        var elev = tiles.ElevationM;
        Span<int> ring = stackalloc int[6];
        foreach (var (start, level) in channels)
        {
            for (int cur = start; cur >= 0; cur = down[cur])
            {
                if (water[cur] is (byte)TileWaterKind.Ocean or (byte)TileWaterKind.Lake or (byte)TileWaterKind.DryLakeBed) break;
                if (elev[cur] <= level) break;
                bool rim = false;
                int k = grid.Neighbours(cur, ring);
                for (int x = 0; x < k && !rim; x++)
                {
                    int id = lakeId[ring[x]];
                    rim = id >= 0 && lakes[id].WaterLevelM > level;
                }
                if (rim) break;
                elev[cur] = level;
            }
        }
    }

    // Final conditioning (see the class comment): every land tile that is not water is raised to at least the elevation
    // of its downstream tile when that tile is not water, starting from the end of each path (water, ocean or sink), so
    // no drainage path rises. Breach channels of two basins can meet at different carve levels, and a later breach can
    // raise a tile on an earlier channel; this pass removes the resulting steps. Sequential; each path is walked once.
    private static void RaiseAlongDrainage(short[] elev, byte[] water, int[] down, int n)
    {
        var done = new bool[n];
        var path = new List<int>();
        static bool IsWater(byte w) => w is (byte)TileWaterKind.Ocean or (byte)TileWaterKind.Lake or (byte)TileWaterKind.DryLakeBed;
        for (int id = 0; id < n; id++)
        {
            if (done[id] || IsWater(water[id])) continue;
            path.Clear();
            int cur = id;
            short floor = short.MinValue;
            while (true)
            {
                path.Add(cur);
                int d = down[cur];
                if (d < 0 || IsWater(water[d])) break;
                if (done[d]) { floor = elev[d]; break; }
                cur = d;
            }
            for (int i = path.Count - 1; i >= 0; i--)
            {
                int x = path[i];
                if (elev[x] < floor) elev[x] = floor;
                floor = elev[x];
                done[x] = true;
            }
        }
    }

    // Priority flood from the coastal ocean tiles (see the class comment). Returns the land tiles in pop order.
    private static int[] Flood(TileLayers tiles, short[] filled, int[] parent, bool[] visited, MinHeap heap,
        ParallelOptions? options, out int popped)
    {
        var grid = tiles.Grid;
        var elev = tiles.ElevationM;
        var ocean = tiles.Ocean;
        var n = grid.Count;
        Array.Fill(parent, -1);

        var coastal = new bool[n];
        TileParallel.For(n, options, (s, e) =>
        {
            Span<int> nb = stackalloc int[6];
            for (int t = s; t < e; t++)
            {
                if (!ocean[t]) continue;
                visited[t] = true;
                filled[t] = elev[t];
                int k = grid.Neighbours(t, nb);
                for (int x = 0; x < k; x++)
                {
                    if (ocean[nb[x]]) continue;
                    coastal[t] = true;
                    break;
                }
            }
        });

        int land = 0;
        for (int t = 0; t < n; t++)
        {
            if (!ocean[t]) land++;
            else if (coastal[t]) heap.Push(Key(elev[t], t));
        }

        if (land == n && n > 0)
        {
            int lowest = 0;
            for (int t = 1; t < n; t++)
                if (elev[t] < elev[lowest]) lowest = t;
            visited[lowest] = true;
            filled[lowest] = elev[lowest];
            heap.Push(Key(elev[lowest], lowest));
        }

        var order = new int[land];
        popped = 0;
        Span<int> ring = stackalloc int[6];
        while (heap.Count > 0)
        {
            long key = heap.Pop();
            int t = KeyId(key);
            short level = KeyLevel(key);
            if (!ocean[t]) order[popped++] = t;
            int k = grid.Neighbours(t, ring);
            for (int s = 0; s < k; s++)
            {
                int u = ring[s];
                if (visited[u]) continue;
                visited[u] = true;
                parent[u] = t;
                short f = Math.Max(elev[u], level);
                filled[u] = f;
                heap.Push(Key(f, u));
            }
        }
        return order;
    }

    private static long Key(short level, int id) => ((long)(level - short.MinValue) << 32) | (uint)id;
    private static int KeyId(long key) => (int)(key & 0xFFFF_FFFFL);
    private static short KeyLevel(long key) => (short)((key >> 32) + short.MinValue);

    public static ulong Hash(TileLayers t)
    {
        var h = DeterministicHash.Combine(LayerHash.Of(t.WaterKind), LayerHash.Of(t.LakeId), LayerHash.Of(t.DownstreamSlot), LayerHash.Of(t.FlowAccum));
        h = DeterministicHash.Combine(h, LayerHash.Of(t.ElevationM), 0, 0); // fills and breach channels change elevations
        foreach (var l in t.Lakes)
        {
            h = DeterministicHash.Combine(h, (ulong)l.Id, (ulong)l.Kind, unchecked((ulong)(long)l.WaterLevelM));
            h = DeterministicHash.Combine(h, (ulong)l.TileCount, unchecked((ulong)BitConverter.DoubleToInt64Bits(l.AreaKm2)),
                unchecked((ulong)(long)l.OutletTile));
            h = DeterministicHash.Combine(h, (ulong)l.LowestTile, unchecked((ulong)l.Inflow), unchecked((ulong)l.Evaporation));
        }
        return DeterministicHash.Combine(h, (ulong)t.Lakes.Count, 0, 0);
    }

    /// <summary>
    /// Resolves one basin (merge tree, water balance, lakes, routing, flow); see the class comment. Holds per-basin
    /// scratch arrays sized by the basin and one global tile → local index map.
    /// </summary>
    private sealed class BasinResolver
    {
        private readonly TileGrid _grid;
        private readonly short[] _elev;
        private readonly short[] _temp;
        private readonly int[] _lakeId;
        private readonly int[] _down;   // flood parent outside resolved basins, final downstream inside them
        private readonly long[] _flow;
        private readonly short[] _filled;
        private readonly MinHeap _heap;
        private readonly int[] _local;
        private readonly double _radius;
        private readonly HydrologySettings _h;
        private readonly int _minDepth;
        private readonly int _dryDepth;

        // Merge-tree nodes.
        private readonly List<int> _nParent = [], _nChild0 = [], _nChild1 = [], _nTop = [], _nFloor = [], _nFloorTile = [], _nSaddle = [];
        private readonly List<int> _nEntry0 = [], _nEntry1 = [], _nRep = [], _nLeafRedirect = [], _nAbsorbLevel = [];
        private readonly List<long> _nEtop = [], _nW = [], _nInput = [];
        private readonly List<bool> _nFull = [], _nFormed = [];

        public BasinResolver(TileLayers tiles, WorldGenSettings settings, int[] lakeId, int[] down, long[] flow, short[] filled,
            MinHeap heap, int n)
        {
            _grid = tiles.Grid;
            _elev = tiles.ElevationM;
            _temp = tiles.TemperatureDeciC;
            _lakeId = lakeId;
            _down = down;
            _flow = flow;
            _filled = filled;
            _heap = heap;
            _local = new int[n];
            _radius = settings.Grid.PlanetRadiusKm;
            _h = settings.Hydrology;
            _minDepth = settings.Tiles.MinBasinDepthM;
            _dryDepth = Math.Max(1, _minDepth);
            _breachDepthM = settings.Tiles.BreachDepthM;
            _seaLevel = tiles.SeaLevelM;
            _ocean = tiles.Ocean;
            _breachExponent = settings.Tiles.BreachExponentPermille / 1000.0;
            _breachReference = settings.Tiles.MajorRiverFlow;
        }

        private readonly int _breachDepthM;
        private readonly int _seaLevel;
        private readonly bool[] _ocean;
        private readonly double _breachExponent;
        private readonly double _breachReference;

        public List<TileLake> Lakes { get; } = [];

        /// <summary>Breach channels to carve downstream of the outlets: (spill tile, channel level), in resolution order.</summary>
        public List<(int Start, short Level)> Channels { get; } = [];

        public void Resolve(int basin, ArraySegment<int> tiles)
        {
            int k = tiles.Count;
            var g = new int[k];               // local → global tile
            for (int i = 0; i < k; i++)
            {
                g[i] = tiles[i];
                _local[g[i]] = i;
            }
            int outlet = 0;                   // local index of the basin outlet (first in the list)
            int spill = _down[g[outlet]];     // flood parent of the outlet, outside the basin
            int spillLevel = _filled[g[outlet]];

            var evap = new long[k];
            var area = new double[k];
            for (int i = 0; i < k; i++)
            {
                area[i] = _grid.AreaKm2(g[i], _radius);
                double c = Math.Max(0, _temp[g[i]] / 10.0);
                evap[i] = (long)Math.Round((_h.EvaporationBaseMm + (_h.EvaporationMmPerDegree * c)) * area[i]);
            }

            var keys = new long[k];
            var sorted = new int[k];
            for (int i = 0; i < k; i++)
            {
                keys[i] = Key(_elev[g[i]], g[i]);
                sorted[i] = i;
            }
            Array.Sort(keys, sorted);

            ClearNodes();
            var uf = new int[k];
            Array.Fill(uf, -1);
            var compNode = new int[k];
            var sumAll = new long[k];
            var sumBelow = new long[k];
            var bin = new int[k];
            var owner = new int[k];
            var waterLeaf = new int[k];
            Span<int> ring = stackalloc int[6];
            Span<int> roots = stackalloc int[6];
            Span<int> entries = stackalloc int[6];

            // Merge tree (see the class comment).
            for (int p = 0; p < k; p++)
            {
                int i = sorted[p];
                int t = g[i];
                int e = _elev[t];
                int nk = _grid.Neighbours(t, ring);
                int lowest = -1, rootCount = 0;
                for (int s = 0; s < nk; s++)
                {
                    int u = ring[s];
                    if (_lakeId[u] != basin) continue;
                    int lu = _local[u];
                    if (uf[lu] < 0) continue;
                    if (lowest < 0 || Before(lu, lowest, g)) lowest = lu;
                    int r = Find(uf, lu);
                    int at = roots[..rootCount].IndexOf(r);
                    if (at < 0) { roots[rootCount] = r; entries[rootCount] = lu; rootCount++; }
                    else if (Before(lu, entries[at], g)) entries[at] = lu;
                }

                if (lowest < 0)
                {
                    int leaf = NewNode(-1, -1, e, i, -1, -1, -1);
                    uf[i] = i;
                    compNode[i] = leaf;
                    bin[i] = e;
                    owner[i] = leaf;
                    waterLeaf[i] = leaf;
                    _down[t] = -1;
                    sumAll[i] = evap[i];
                    continue;
                }

                _down[t] = g[lowest];
                waterLeaf[i] = waterLeaf[lowest];
                int first = roots[..rootCount].IndexOf(Find(uf, lowest));
                int cur = roots[first], curEntry = entries[first];
                for (int x = 0; x < rootCount; x++)
                {
                    if (x == first) continue;
                    (cur, curEntry) = Merge(cur, roots[x], i, curEntry, entries[x], e, uf, compNode, sumAll, sumBelow, bin, waterLeaf, g);
                }
                uf[i] = cur;
                owner[i] = compNode[cur];
                if (bin[cur] != e) { sumBelow[cur] = sumAll[cur]; bin[cur] = e; }
                sumAll[cur] += evap[i];
            }

            int rootComp = Find(uf, 0);
            int root = compNode[rootComp];
            _nTop[root] = spillLevel;
            _nEtop[root] = sumAll[rootComp];

            // Water balance.
            for (int i = 0; i < k; i++)
                _nInput[ResolveLeaf(waterLeaf[i])] += _flow[g[i]];
            for (int x = 0; x < _nParent.Count; x++)
                if (_nChild0[x] < 0 && _nRep[x] == x) Deliver(x, _nInput[x], waterLeaf);

            // Outlet breaching: a basin whose water fills it to the spill level erodes its sill; it drains when its depth
            // is at most the breach depth of its outflow (see the class comment).
            if (_nFull[root] && spill >= 0 && _breachDepthM > 0)
            {
                long total = 0;
                for (int i = 0; i < k; i++) total += _flow[g[i]];
                long outflow = total - _nEtop[root];
                double breach = _breachDepthM * Math.Pow(Math.Max(0, outflow) / _breachReference, _breachExponent);
                if (spillLevel - _nFloor[root] <= breach)
                {
                    DrainThrough(basin, g, k, outlet, spill, evap, area);
                    return;
                }
            }

            // Lake nodes (parents have higher ids than their children).
            int nodes = _nParent.Count;
            var lakeOf = new int[nodes];
            for (int x = nodes - 1; x >= 0; x--)
            {
                if (_nRep[x] != x) { lakeOf[x] = -1; continue; }
                int pa = _nParent[x];
                lakeOf[x] = pa < 0 || !_nFormed[pa] ? (_nFormed[x] ? x : -1) : lakeOf[pa];
            }

            // Levels: full → top; otherwise flood whole elevation bins while the water lasts.
            var level = new int[nodes];
            var cum = new long[nodes];
            var binSum = new long[nodes];
            var isTouched = new bool[nodes];
            for (int x = 0; x < nodes; x++) level[x] = _nFull[x] ? _nTop[x] : int.MinValue;
            var lakeOfTile = new int[k];
            var touched = new List<int>();
            for (int p0 = 0; p0 < k;)
            {
                int e = _elev[g[sorted[p0]]];
                int p1 = p0;
                touched.Clear();
                while (p1 < k && _elev[g[sorted[p1]]] == e)
                {
                    int i = sorted[p1++];
                    int a = lakeOf[NodeFind(owner[i])];
                    lakeOfTile[i] = a;
                    if (a < 0 || level[a] != int.MinValue) continue;
                    if (!isTouched[a]) { isTouched[a] = true; touched.Add(a); }
                    binSum[a] += evap[i];
                }
                foreach (var a in touched)
                {
                    if (cum[a] + binSum[a] <= _nW[a]) cum[a] += binSum[a];
                    else level[a] = e;
                    binSum[a] = 0;
                    isTouched[a] = false;
                }
                p0 = p1;
            }
            for (int x = 0; x < nodes; x++)
                if (lakeOf[x] == x && level[x] == int.MinValue) level[x] = _nTop[x];

            // Wet tiles.
            var kind = new byte[k];
            for (int i = 0; i < k; i++)
            {
                int a = lakeOfTile[i];
                if (a < 0) continue;
                int e = _elev[g[i]];
                bool dry = level[a] - _nFloor[a] < _dryDepth;
                if (dry) { if (e < _nFloor[a] + _dryDepth) kind[i] = (byte)TileWaterKind.DryLakeBed; }
                else if (e < level[a]) kind[i] = (byte)TileWaterKind.Lake;
            }

            // Wet components: connected sets of wet tiles of one lake node. A component holding no pit of the node (a pit
            // absorbed into the node, flooded above its own saddle level but cut off from the lake) stays dry land and
            // drains by its descent.
            var comp = new int[k];
            Array.Fill(comp, -1);
            var queue = new List<int>();
            int rawComps = 0;
            var rawLowest = new List<int>();
            for (int p = 0; p < k; p++)
            {
                int i = sorted[p];
                if (kind[i] == 0 || comp[i] >= 0) continue;
                int c = rawComps++;
                rawLowest.Add(i); // first in sorted order = lowest (elevation, id)
                int a = lakeOfTile[i];
                comp[i] = c;
                queue.Clear();
                queue.Add(i);
                for (int head = 0; head < queue.Count; head++)
                {
                    int x = queue[head];
                    int nk = _grid.Neighbours(g[x], ring);
                    for (int s = 0; s < nk; s++)
                    {
                        int u = ring[s];
                        if (_lakeId[u] != basin) continue;
                        int lu = _local[u];
                        if (comp[lu] >= 0 || kind[lu] != kind[i] || lakeOfTile[lu] != a) continue;
                        comp[lu] = c;
                        queue.Add(lu);
                    }
                }
            }
            var holdsPit = new bool[rawComps];
            for (int x = 0; x < nodes; x++)
                if (_nChild0[x] < 0 && _nRep[x] == x && comp[_nFloorTile[x]] >= 0) holdsPit[comp[_nFloorTile[x]]] = true;
            var renumber = new int[rawComps];
            var compLowest = new List<int>();
            var compLevel = new List<int>();
            var compNodeOf = new List<int>();
            for (int c = 0; c < rawComps; c++)
            {
                renumber[c] = -1;
                if (!holdsPit[c]) continue;
                renumber[c] = compLowest.Count;
                int low = rawLowest[c];
                int a = lakeOfTile[low];
                compLowest.Add(low);
                compNodeOf.Add(a);
                compLevel.Add(kind[low] == (byte)TileWaterKind.Lake ? level[a] : _nFloor[a] + _dryDepth);
            }
            for (int i = 0; i < k; i++)
            {
                if (comp[i] < 0) continue;
                comp[i] = renumber[comp[i]];
                if (comp[i] < 0) kind[i] = 0;
            }
            // Dry tiles of an absorbed pit drain over its saddle: sediment fill to the saddle elevation, so the path over
            // the saddle never rises.
            for (int i = 0; i < k; i++)
            {
                if (kind[i] != 0) continue;
                int lv = _nAbsorbLevel[owner[i]];
                if (lv > _elev[g[i]]) _elev[g[i]] = (short)lv;
            }
            int comps = compLowest.Count;
            var compOutlet = new int[comps];
            Array.Fill(compOutlet, -1);

            // Exits. (1) Full lakes: the root over the spill tile, inner lakes over their saddle. (2) A lake whose level
            // equals the top of children inside it (the water reaches their shared saddle but does not flood the saddle's
            // elevation bin) is several components at one level; they are linked over those saddles into one tree that
            // ends at the lake's exit (full lake) or at the component holding its lowest tile (the sink).
            var saddleSet = new bool[k];
            var headOf = new int[nodes];
            var exitOf = new int[nodes]; // full lake node: local tile where its whole outflow passes (outlet or saddle)
            Array.Fill(headOf, -1);
            Array.Fill(exitOf, -1);
            for (int x = 0; x < nodes; x++)
            {
                if (lakeOf[x] != x || !_nFull[x]) continue;
                int pa = _nParent[x];
                if (pa < 0)
                {
                    if (comp[outlet] >= 0) compOutlet[comp[outlet]] = outlet;
                    _down[g[outlet]] = spill;
                    headOf[x] = outlet;
                    exitOf[x] = outlet;
                    continue;
                }
                headOf[x] = Exit(x, pa, x);
                exitOf[x] = _nSaddle[pa];
            }
            var frames = new Stack<(int Y, int Head)>();
            for (int x = 0; x < nodes; x++)
            {
                if (lakeOf[x] != x || !InCluster(x, level[x])) continue;
                int head = headOf[x] >= 0 ? headOf[x] : _nFloorTile[x];
                Orient(x, x, head);
            }

            bool InCluster(int y, int lv) => _nChild0[y] >= 0 && _nTop[_nChild0[y]] == lv;

            // Within lake node a, orient merge node y's two sides at the lake level: the side holding the head tile is
            // oriented toward it, the other side drains over y's saddle into it. Iterative with an explicit stack in the
            // order of the recursive form (the other side's subtree is finished before the head side continues), so the
            // stack depth does not depend on the terrain.
            void Orient(int a, int y0, int head0)
            {
                frames.Push((y0, head0));
                while (frames.Count > 0)
                {
                    var (y, head) = frames.Pop();
                    if (!InCluster(y, level[a])) continue;
                    int h = SideOf(y, head);
                    int o = h == _nChild0[y] ? _nChild1[y] : _nChild0[y];
                    int w = Exit(a, y, o);
                    frames.Push((h, head));
                    if (w >= 0) frames.Push((o, w));
                }
            }

            // The child of y whose subtree owns local tile i.
            int SideOf(int y, int i)
            {
                int x = NodeFind(owner[i]);
                while (_nParent[x] != y && _nParent[x] >= 0) x = _nParent[x];
                return x;
            }

            // Lake node lakeNode drains from the side of child `from` of merge node pa over pa's saddle: the descent path
            // from that side's entry to the first wet tile of the lake is reversed, the entry drains to the saddle and the
            // saddle (first assignment wins) to the entry on the other side. Returns the wet tile reached (the outlet of
            // its component), or −1.
            int Exit(int lakeNode, int pa, int from)
            {
                bool side0 = _nChild0[pa] == from;
                int entry = side0 ? _nEntry0[pa] : _nEntry1[pa];
                int other = side0 ? _nEntry1[pa] : _nEntry0[pa];
                int saddle = _nSaddle[pa];
                int prev = g[saddle], cur = g[entry], reached = -1;
                while (true)
                {
                    int lc = _local[cur];
                    int next = _down[cur];
                    _down[cur] = prev;
                    if (comp[lc] >= 0 && lakeOfTile[lc] == lakeNode)
                    {
                        if (compOutlet[comp[lc]] < 0) compOutlet[comp[lc]] = lc;
                        reached = lc;
                        break;
                    }
                    if (next < 0) break;
                    prev = cur;
                    cur = next;
                }
                if (!saddleSet[saddle])
                {
                    saddleSet[saddle] = true;
                    _down[g[saddle]] = g[other];
                }
                return reached;
            }

            // Routing inside each wet component.
            var routed = new bool[k];
            for (int c = 0; c < comps; c++)
            {
                int seed = compOutlet[c] >= 0 ? compOutlet[c] : compLowest[c];
                if (compOutlet[c] < 0) _down[g[seed]] = -1;
                routed[seed] = true;
                _heap.Push(Key(_elev[g[seed]], g[seed]));
                while (_heap.Count > 0)
                {
                    long key = _heap.Pop();
                    int t = KeyId(key);
                    short lv = KeyLevel(key);
                    int nk = _grid.Neighbours(t, ring);
                    for (int s = 0; s < nk; s++)
                    {
                        int u = ring[s];
                        if (_lakeId[u] != basin) continue;
                        int lu = _local[u];
                        if (comp[lu] != c || routed[lu]) continue;
                        routed[lu] = true;
                        _down[u] = t;
                        _heap.Push(Key(Math.Max(_elev[u], lv), u));
                    }
                }
            }

            // Lake evaporation and outlet losses.
            var compEvap = new long[comps];
            var compArea = new double[comps];
            var compCount = new int[comps];
            for (int p = 0; p < k; p++)
            {
                int i = sorted[p];
                if (comp[i] < 0) continue;
                compEvap[comp[i]] += evap[i];
                compArea[comp[i]] += area[i];
                compCount[comp[i]]++;
            }

            // Evaporation of a full lake is booked once, at its exit tile (outlet of the root lake, saddle of an inner
            // lake): all water the balance delivered to the lake node passes there, including water from tiles on the
            // reversed exit path and from the saddle tile, which bypass the lake tiles.
            var nodeEvap = new long[nodes];
            for (int c = 0; c < comps; c++) nodeEvap[compNodeOf[c]] += compEvap[c];
            var lossAt = new long[k];
            for (int x = 0; x < nodes; x++)
                if (exitOf[x] >= 0) lossAt[exitOf[x]] += nodeEvap[x];

            // Flow accumulation in Kahn order inside the basin.
            var inDegree = new int[k];
            var localDown = new int[k];
            for (int i = 0; i < k; i++)
            {
                int d = _down[g[i]];
                localDown[i] = d >= 0 && _lakeId[d] == basin ? _local[d] : -1;
                if (localDown[i] >= 0) inDegree[localDown[i]]++;
            }
            var kahn = new int[k];
            int qh = 0, qt = 0;
            for (int i = 0; i < k; i++)
                if (inDegree[i] == 0) kahn[qt++] = i;
            long toSpill = 0;
            while (qh < qt)
            {
                int i = kahn[qh++];
                long pass = _flow[g[i]];
                if (lossAt[i] > 0) pass -= Math.Min(pass, lossAt[i]);
                int d = localDown[i];
                if (d >= 0)
                {
                    _flow[g[d]] += pass;
                    if (--inDegree[d] == 0) kahn[qt++] = d;
                }
                else if (i == outlet && _down[g[i]] == spill && spill >= 0) toSpill = pass;
            }
            if (qt != k) throw new InvalidOperationException($"Drainage cycle inside basin {basin} ({k - qt} of {k} tiles).");
            if (spill >= 0 && _down[g[outlet]] == spill) _flow[spill] += toSpill;

            // Lakes and per-tile lake ids.
            // Head component of each full lake node: the one holding its exit path's first wet tile (fallbacks: the floor
            // tile's component, then the node's first component). It carries the node's inflow and evaporation.
            var headComp = new int[nodes];
            Array.Fill(headComp, -1);
            for (int x = 0; x < nodes; x++)
            {
                if (exitOf[x] < 0) continue;
                if (headOf[x] >= 0 && comp[headOf[x]] >= 0) headComp[x] = comp[headOf[x]];
                else if (comp[_nFloorTile[x]] >= 0) headComp[x] = comp[_nFloorTile[x]];
            }
            for (int c = 0; c < comps; c++)
                if (exitOf[compNodeOf[c]] >= 0 && headComp[compNodeOf[c]] < 0) headComp[compNodeOf[c]] = c;

            int firstId = Lakes.Count;
            for (int c = 0; c < comps; c++)
            {
                int low = compLowest[c];
                var kd = kind[low] == (byte)TileWaterKind.Lake ? TileWaterKind.Lake : TileWaterKind.DryLakeBed;
                int o = compOutlet[c];
                int node = compNodeOf[c];
                long inflow, evaporation;
                int exitTile = -1;
                if (exitOf[node] >= 0 && headComp[node] == c)
                {
                    exitTile = g[exitOf[node]];
                    inflow = _flow[exitTile];
                    evaporation = nodeEvap[node];
                }
                else
                {
                    inflow = _flow[g[o >= 0 ? o : low]];
                    evaporation = exitOf[node] >= 0 ? 0 : compEvap[c];
                }
                if (kd == TileWaterKind.DryLakeBed && o >= 0)
                    throw new InvalidOperationException($"Dry lake bed in basin {basin} has an outlet tile {g[o]}.");
                Lakes.Add(new TileLake(firstId + c, kd, compLevel[c], compCount[c], compArea[c], o >= 0 ? g[o] : -1, g[low],
                    inflow, evaporation, exitTile));
            }
            for (int i = 0; i < k; i++)
                _lakeId[g[i]] = comp[i] >= 0 ? -3 - (firstId + comp[i]) : -1;
        }

        // A breached basin (see the class comment): carve and fill. T = max(lowest elevation, tile sea level). C = the tiles
        // below T connected (through tiles below T) to the lowest tile: empty unless the floor is below sea level, else
        // they stay a full lake at level T (partial breach). 1. The restricted flood from the outlet (as for a full lake)
        // gives the path from the lowest tile to the outlet; its tiles outside C are set to T (the channel), the outlet
        // too unless it is in C. 2. Downstream of the outlet the flood-parent path is lowered to T until a tile at or below
        // T (or the ocean). 3. A priority flood over the basin from the outlet (key max(elevation, popped key)) raises every
        // tile outside C to its key (sediment fill of nested pits) and sets its downstream to the tile that reached it, so
        // elevations never rise downstream. 4. C (if any) drains through its first-reached tile e (outlet and exit tile):
        // a flood restricted to C from e routes C to e. 5. Flow in Kahn order; the lake's evaporation is booked at the basin
        // outlet (its exit tile), which all basin water passes, so the lake's inflow is the basin's whole inflow.
        private void DrainThrough(int basin, int[] g, int k, int outlet, int spill, long[] evap, double[] area)
        {
            Span<int> ring = stackalloc int[6];
            int lowest = 0;
            for (int i = 1; i < k; i++)
                if (Before(i, lowest, g)) lowest = i;
            int floor = _elev[g[lowest]];
            short target = (short)Math.Max(floor, _seaLevel);

            // C: tiles below T connected to the lowest tile.
            var inC = new bool[k];
            var cList = new List<int>();
            if (floor < target)
            {
                inC[lowest] = true;
                cList.Add(lowest);
                for (int h = 0; h < cList.Count; h++)
                {
                    int nk = _grid.Neighbours(g[cList[h]], ring);
                    for (int s = 0; s < nk; s++)
                    {
                        int u = ring[s];
                        if (_lakeId[u] != basin) continue;
                        int lu = _local[u];
                        if (inC[lu] || _elev[u] >= target) continue;
                        inC[lu] = true;
                        cList.Add(lu);
                    }
                }
            }

            // 1. Path from the lowest tile to the outlet on the restricted flood tree from the outlet.
            var routed = new bool[k];
            RestrictedFlood(basin, g, outlet, routed, null, ring);
            _down[g[outlet]] = spill;
            for (int cur = g[lowest]; cur >= 0;)
            {
                int lc = _local[cur];
                if (!inC[lc]) _elev[cur] = target;
                if (lc == outlet) break;
                cur = _down[cur];
            }

            // 2. Channel downstream of the outlet: carved after every basin is resolved (CarveChannels), so a channel never
            // lowers the rim of a lake or the tiles of a basin that is not resolved yet.
            Channels.Add((spill, target));

            // 3. Fill flood from the outlet.
            Array.Clear(routed);
            int first = -1;
            routed[outlet] = true;
            _heap.Push(Key(_elev[g[outlet]], g[outlet]));
            if (inC[outlet]) first = outlet;
            while (_heap.Count > 0)
            {
                long key = _heap.Pop();
                int t = KeyId(key);
                short lv = KeyLevel(key);
                int nk = _grid.Neighbours(t, ring);
                for (int s = 0; s < nk; s++)
                {
                    int u = ring[s];
                    if (_lakeId[u] != basin) continue;
                    int lu = _local[u];
                    if (routed[lu]) continue;
                    routed[lu] = true;
                    _down[u] = t;
                    short kv = Math.Max(_elev[u], lv);
                    if (inC[lu]) { if (first < 0) first = lu; }
                    else _elev[u] = kv;
                    _heap.Push(Key(kv, u));
                }
            }

            // 4. The lake below sea level drains through its first-reached tile.
            if (first >= 0)
            {
                var routedC = new bool[k];
                RestrictedFlood(basin, g, first, routedC, inC, ring);
            }

            // 5. Flow.
            long lakeEvap = 0;
            double lakeArea = 0;
            foreach (var c in cList) { lakeEvap += evap[c]; lakeArea += area[c]; }
            var inDegree = new int[k];
            var localDown = new int[k];
            for (int i = 0; i < k; i++)
            {
                int d = _down[g[i]];
                localDown[i] = d >= 0 && _lakeId[d] == basin ? _local[d] : -1;
                if (localDown[i] >= 0) inDegree[localDown[i]]++;
            }
            var kahn = new int[k];
            int qh = 0, qt = 0;
            for (int i = 0; i < k; i++)
                if (inDegree[i] == 0) kahn[qt++] = i;
            long toSpill = 0, lakeInflow = 0;
            while (qh < qt)
            {
                int i = kahn[qh++];
                long pass = _flow[g[i]];
                if (first >= 0 && i == outlet) { lakeInflow = pass; pass -= Math.Min(pass, lakeEvap); }
                int d = localDown[i];
                if (d < 0) { if (i == outlet) toSpill = pass; continue; }
                _flow[g[d]] += pass;
                if (--inDegree[d] == 0) kahn[qt++] = d;
            }
            if (qt != k) throw new InvalidOperationException($"Drainage cycle inside breached basin {basin} ({k - qt} of {k} tiles).");
            _flow[spill] += toSpill;
            for (int i = 0; i < k; i++) _lakeId[g[i]] = -1;
            if (first >= 0)
            {
                int id = Lakes.Count;
                foreach (var c in cList) _lakeId[g[c]] = -3 - id;
                Lakes.Add(new TileLake(id, TileWaterKind.Lake, target, cList.Count, lakeArea, g[first], g[lowest], lakeInflow, lakeEvap, g[outlet]));
            }
        }

        // Priority flood restricted to the basin (or to the tiles with only[i] set) from a seed: each reached tile's
        // downstream is the tile that reached it; keys (max(elevation, popped level), id). Elevations are not changed.
        private void RestrictedFlood(int basin, int[] g, int seed, bool[] routed, bool[]? only, Span<int> ring)
        {
            routed[seed] = true;
            _heap.Push(Key(_elev[g[seed]], g[seed]));
            while (_heap.Count > 0)
            {
                long key = _heap.Pop();
                int t = KeyId(key);
                short lv = KeyLevel(key);
                int nk = _grid.Neighbours(t, ring);
                for (int s = 0; s < nk; s++)
                {
                    int u = ring[s];
                    if (_lakeId[u] != basin) continue;
                    int lu = _local[u];
                    if (routed[lu] || (only is not null && !only[lu])) continue;
                    routed[lu] = true;
                    _down[u] = t;
                    _heap.Push(Key(Math.Max(_elev[u], lv), u));
                }
            }
        }

        private bool Before(int a, int b, int[] g) =>
            _elev[g[a]] < _elev[g[b]] || (_elev[g[a]] == _elev[g[b]] && g[a] < g[b]);

        private (int Root, int Entry) Merge(int x, int y, int saddle, int entryX, int entryY, int s, int[] uf, int[] compNode,
            long[] sumAll, long[] sumBelow, int[] bin, int[] waterLeaf, int[] g)
        {
            int nx = compNode[x], ny = compNode[y];
            // max(MinBasinDepthM, 1): a zero-depth pit on a flat floor is absorbed, so no kept node has top = floor.
            bool shallowX = s - _nFloor[nx] < _dryDepth, shallowY = s - _nFloor[ny] < _dryDepth;
            long belowX = bin[x] == s ? sumBelow[x] : sumAll[x];
            long belowY = bin[y] == s ? sumBelow[y] : sumAll[y];
            int merged;
            if (shallowX || shallowY)
            {
                bool absorbX = shallowX && (!shallowY || _nFloor[nx] > _nFloor[ny] || (_nFloor[nx] == _nFloor[ny] && nx > ny));
                int a = absorbX ? nx : ny, keep = absorbX ? ny : nx;
                int entryA = absorbX ? entryX : entryY, entryKeep = absorbX ? entryY : entryX;
                int rootA = absorbX ? x : y;
                if (_nChild0[a] >= 0) throw new InvalidOperationException("A shallow component must be a single pit.");
                _nRep[a] = keep;
                _nAbsorbLevel[a] = s;
                // Reverse the descent path from the absorbed side's entry to its pit; the entry drains to the saddle.
                int prev = g[saddle], cur = g[entryA];
                while (cur >= 0)
                {
                    int next = _down[cur];
                    _down[cur] = prev;
                    prev = cur;
                    cur = next;
                }
                int sd = _down[g[saddle]];
                if (sd >= 0 && Find(uf, _local[sd]) == rootA) _down[g[saddle]] = g[entryKeep];
                // The absorbed pit's water follows the routing: over the saddle to where the saddle drains.
                _nLeafRedirect[a] = waterLeaf[_local[_down[g[saddle]]]];
                merged = keep;
            }
            else
            {
                merged = NewNode(nx, ny, Math.Min(_nFloor[nx], _nFloor[ny]),
                    Before(_nFloorTile[nx], _nFloorTile[ny], g) ? _nFloorTile[nx] : _nFloorTile[ny], saddle, entryX, entryY);
                _nParent[nx] = merged;
                _nParent[ny] = merged;
                _nTop[nx] = s;
                _nTop[ny] = s;
                _nEtop[nx] = belowX;
                _nEtop[ny] = belowY;
            }
            uf[y] = x;
            sumAll[x] += sumAll[y];
            sumBelow[x] = belowX + belowY;
            bin[x] = s;
            compNode[x] = merged;
            return (x, Before(entryX, entryY, g) ? entryX : entryY);
        }

        // Water w arrives at leaf x (see the class comment).
        private void Deliver(int x, long w, int[] waterLeaf)
        {
            int nd = Climb(x);
            while (true)
            {
                _nW[nd] += w;
                w = 0;
                if (_nW[nd] < _nEtop[nd]) return;
                long excess = _nW[nd] - _nEtop[nd];
                _nW[nd] = _nEtop[nd];
                _nFull[nd] = true;
                int pa = _nParent[nd];
                if (pa < 0) return; // the root's excess leaves the basin (routed flow carries it)
                int sib = _nChild0[pa] == nd ? _nChild1[pa] : _nChild0[pa];
                if (_nFull[sib])
                {
                    _nFormed[pa] = true;
                    _nW[pa] = _nW[nd] + _nW[sib];
                    w = excess;
                    nd = pa;
                    continue;
                }
                int entry = _nChild0[pa] == sib ? _nEntry0[pa] : _nEntry1[pa];
                nd = Climb(ResolveLeaf(waterLeaf[entry]));
                w = excess;
            }
        }

        private int Climb(int x)
        {
            while (_nParent[x] >= 0 && _nFormed[_nParent[x]]) x = _nParent[x];
            return x;
        }

        private int ResolveLeaf(int x)
        {
            int r = x;
            while (_nLeafRedirect[r] >= 0) r = _nLeafRedirect[r];
            while (_nLeafRedirect[x] >= 0) { int nx = _nLeafRedirect[x]; _nLeafRedirect[x] = r; x = nx; }
            return r;
        }

        private int NodeFind(int x)
        {
            int r = x;
            while (_nRep[r] != r) r = _nRep[r];
            while (_nRep[x] != r) { int nx = _nRep[x]; _nRep[x] = r; x = nx; }
            return r;
        }

        private static int Find(int[] uf, int x)
        {
            while (uf[x] != x)
            {
                uf[x] = uf[uf[x]];
                x = uf[x];
            }
            return x;
        }

        private int NewNode(int child0, int child1, int floor, int floorTile, int saddle, int entry0, int entry1)
        {
            int id = _nParent.Count;
            _nParent.Add(-1);
            _nChild0.Add(child0);
            _nChild1.Add(child1);
            _nTop.Add(int.MaxValue);
            _nFloor.Add(floor);
            _nFloorTile.Add(floorTile);
            _nSaddle.Add(saddle);
            _nEntry0.Add(entry0);
            _nEntry1.Add(entry1);
            _nRep.Add(id);
            _nLeafRedirect.Add(-1);
            _nAbsorbLevel.Add(int.MinValue);
            _nEtop.Add(0);
            _nW.Add(0);
            _nInput.Add(0);
            _nFull.Add(false);
            _nFormed.Add(child0 < 0);
            return id;
        }

        private void ClearNodes()
        {
            _nParent.Clear(); _nChild0.Clear(); _nChild1.Clear(); _nTop.Clear(); _nFloor.Clear(); _nFloorTile.Clear(); _nSaddle.Clear();
            _nEntry0.Clear(); _nEntry1.Clear(); _nRep.Clear(); _nLeafRedirect.Clear(); _nAbsorbLevel.Clear();
            _nEtop.Clear(); _nW.Clear(); _nInput.Clear(); _nFull.Clear(); _nFormed.Clear();
        }
    }

    /// <summary>Binary min-heap of packed (level, id) keys in one growable array.</summary>
    private sealed class MinHeap(int capacity)
    {
        private long[] _a = new long[Math.Max(16, capacity)];

        public int Count { get; private set; }

        public void Push(long key)
        {
            if (Count == _a.Length) Array.Resize(ref _a, _a.Length * 2);
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_a[p] <= key) break;
                _a[i] = _a[p];
                i = p;
            }
            _a[i] = key;
        }

        public long Pop()
        {
            long top = _a[0];
            long last = _a[--Count];
            int i = 0;
            int half = Count >> 1;
            while (i < half)
            {
                int c = (2 * i) + 1;
                if (c + 1 < Count && _a[c + 1] < _a[c]) c++;
                if (_a[c] >= last) break;
                _a[i] = _a[c];
                i = c;
            }
            if (Count > 0) _a[i] = last;
            return top;
        }
    }
}
