using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Noise;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>Plate boundary classification stored per cell in <see cref="WorldLayers.BoundaryKind"/>.</summary>
public enum BoundaryKind : byte
{
    None,
    Convergent,
    Divergent,
    Transform,
}

/// <summary>Stage 2: boundary-driven elevation, spread into plate interiors, plus noise detail and continental shelves.</summary>
public static class ElevationStage
{
    public static void Run(WorldLayers layers, WorldGenSettings settings, ulong seed)
    {
        var s = settings.Elevation;
        var grid = layers.Grid;
        var n = grid.CellCount;
        var plateOf = layers.PlateOf;

        // Per boundary cell: the strongest-pressure foreign neighbour (tie: lower id, by ascending scan and strict >).
        var partner = new int[n];
        var pressureOf = new double[n];
        var shearOf = new double[n];
        Array.Fill(partner, -1);
        var maxPressure = 0.0;

        for (var c = 0; c < n; c++)
        {
            var mc = PlateStage.MotionAt(layers, c);
            var cc = layers.Geometry.Centre(c);
            var bestAbs = -1.0;
            foreach (var nb in grid.Neighbours(c).ToArray().OrderBy(x => x))
            {
                if (plateOf[nb] == plateOf[c])
                    continue;
                var d = PlateStage.MotionAt(layers, nb) - mc;
                var dir = (layers.Geometry.Centre(nb) - cc).Normalize();
                var along = Vector3d.Dot(d, dir);
                var pressure = -along;
                var shear = (d - (dir * along)).Length;
                if (Math.Abs(pressure) > bestAbs)
                {
                    bestAbs = Math.Abs(pressure);
                    partner[c] = nb;
                    pressureOf[c] = pressure;
                    shearOf[c] = shear;
                }
            }

            if (partner[c] >= 0)
                maxPressure = Math.Max(maxPressure, bestAbs);
        }

        var kind = new byte[n];
        var boundaryValue = new double[n];
        for (var c = 0; c < n; c++)
        {
            if (partner[c] < 0)
                continue;
            var p = pressureOf[c];
            var sh = shearOf[c];
            var k = p > 0 && p >= sh ? BoundaryKind.Convergent
                : p < 0 && -p >= sh ? BoundaryKind.Divergent
                : BoundaryKind.Transform;
            kind[c] = (byte)k;

            var mine = layers.Plates[plateOf[c]].Continental;
            var theirs = layers.Plates[plateOf[partner[c]]].Continental;
            var t = maxPressure > 0 ? Math.Clamp(Math.Abs(p) / maxPressure, 0.0, 1.0) : 0.0;
            boundaryValue[c] = k switch
            {
                BoundaryKind.Convergent when mine && theirs => Lerp(s.CollisionWeakM, s.CollisionStrongM, t),
                BoundaryKind.Convergent when mine => Lerp(s.CoastalRangeWeakM, s.CoastalRangeStrongM, t),
                BoundaryKind.Convergent when theirs => Lerp(s.TrenchWeakM, s.TrenchStrongM, t),
                BoundaryKind.Convergent => Lerp(s.IslandArcWeakM, s.IslandArcStrongM, t),
                BoundaryKind.Divergent => mine ? s.DivergentContinentalM : s.DivergentOceanicM,
                _ => mine ? s.ContinentalBaseM : s.OceanicBaseM,
            };
        }

        // Multi-source Dijkstra (unit steps) within each plate from its own boundary cells.
        var dist = new int[n];
        var source = new int[n];
        var settled = new bool[n];
        Array.Fill(dist, int.MaxValue);
        Array.Fill(source, -1);
        var queue = new PriorityQueue<int, (int Dist, int Source, int Cell)>(
            Comparer<(int Dist, int Source, int Cell)>.Create((a, b) =>
            {
                var r = a.Dist.CompareTo(b.Dist);
                if (r != 0) return r;
                r = a.Source.CompareTo(b.Source);
                return r != 0 ? r : a.Cell.CompareTo(b.Cell);
            }));
        for (var c = 0; c < n; c++)
        {
            if (partner[c] < 0)
                continue;
            dist[c] = 0;
            source[c] = c;
            queue.Enqueue(c, (0, c, c));
        }

        while (queue.TryDequeue(out var cell, out var pri))
        {
            if (settled[cell])
                continue;
            settled[cell] = true;
            dist[cell] = pri.Dist;
            source[cell] = pri.Source;
            foreach (var nb in grid.Neighbours(cell))
            {
                if (settled[nb] || plateOf[nb] != plateOf[cell])
                    continue;
                queue.Enqueue(nb, (pri.Dist + 1, pri.Source, nb));
            }
        }

        var falloff = Math.Max(1, s.FalloffCells);
        double frequency = s.NoiseFrequency;
        var persistence = s.NoisePersistenceBp / 10_000.0;
        var lacunarity = s.NoiseLacunarityBp / 10_000.0;
        var elevation = new int[n];
        for (var c = 0; c < n; c++)
        {
            var baseElev = layers.Plates[plateOf[c]].Continental ? s.ContinentalBaseM : s.OceanicBaseM;
            double e;
            if (source[c] < 0)
            {
                e = baseElev;
            }
            else
            {
                var w = Math.Min(1.0, dist[c] / (double)falloff);
                e = Lerp(boundaryValue[source[c]], baseElev, w);
            }

            var noise = SphereNoise.Sample(layers.Geometry.Centre(c), seed, s.NoiseOctaves, frequency, persistence, lacunarity);
            elevation[c] = Math.Clamp((int)Math.Round(e + (noise * s.NoiseAmplitudeM)), s.MinElevationM, s.MaxElevationM);
        }

        AddShelves(layers, elevation, kind, settings);
        layers.ElevationM = elevation;
        layers.BoundaryKind = kind;
    }

    /// <summary>
    /// Continental shelves. The sea level that Stage 3 will pick is computed here (<see cref="SeaLevelStage.Compute"/>);
    /// a breadth-first search runs from every land cell on a continental plate through ocean cells, up to
    /// <see cref="ElevationSettings.ShelfWidthCells"/> steps, and raises each reached ocean cell to at least sea level +
    /// <see cref="ElevationSettings.ShelfDepthM"/>. The depth is negative, so raised cells stay below the sea level and
    /// Stage 3 picks the same sea level (cells below the cut stay below it). Convergent boundary cells (trenches, island
    /// arcs) are neither raised nor crossed, so an active margin keeps its trench.
    /// </summary>
    internal static void AddShelves(WorldLayers layers, int[] elevation, byte[] kind, WorldGenSettings settings)
    {
        var s = settings.Elevation;
        if (s.ShelfWidthCells <= 0) return;
        var grid = layers.Grid;
        var n = grid.CellCount;
        var seaLevel = SeaLevelStage.Compute(layers, elevation, settings);
        var shelf = seaLevel + s.ShelfDepthM;
        var dist = new int[n];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        for (var c = 0; c < n; c++)
        {
            if (elevation[c] < seaLevel || !layers.Plates[layers.PlateOf[c]].Continental) continue;
            dist[c] = 0;
            queue.Enqueue(c);
        }

        while (queue.TryDequeue(out var c))
        {
            if (dist[c] >= s.ShelfWidthCells) continue;
            foreach (var nb in grid.Neighbours(c))
            {
                if (dist[nb] >= 0 || elevation[nb] >= seaLevel || kind[nb] == (byte)BoundaryKind.Convergent) continue;
                dist[nb] = dist[c] + 1;
                queue.Enqueue(nb);
            }
        }

        // Raised after the search, so the search sees the unshelved elevations only.
        for (var c = 0; c < n; c++)
        {
            if (dist[c] > 0) elevation[c] = Math.Max(elevation[c], shelf);
        }
    }

    public static ulong Hash(WorldLayers l) =>
        WorldEcon.Simulation.Random.DeterministicHash.Combine(LayerHash.Of(l.ElevationM), LayerHash.Of(l.BoundaryKind), 0, 0);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
