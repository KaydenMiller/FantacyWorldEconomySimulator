using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// Present-day plate boundaries from the final plates and motions (design section 6), with the classification the
/// version 5 Elevation stage used: per boundary cell the foreign neighbour with the strongest pressure, Convergent or
/// Divergent when |pressure| ≥ shear, else Transform. Adds the two values that stage computed and dropped: collision
/// strength (|pressure| / largest, permille) and steps to the nearest boundary within the plate. Also continental shelves.
/// </summary>
public static class PresentBoundaries
{
    /// <summary>Writes <see cref="WorldLayers.BoundaryKind"/>, <see cref="WorldLayers.CollisionStrengthPermille"/> and <see cref="WorldLayers.BoundaryDistanceCells"/>.</summary>
    public static void Classify(WorldLayers layers)
    {
        var grid = layers.Grid;
        var n = grid.CellCount;
        var plateOf = layers.PlateOf;
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
                if (plateOf[nb] == plateOf[c]) continue;
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

            if (partner[c] >= 0) maxPressure = Math.Max(maxPressure, bestAbs);
        }

        var kind = new byte[n];
        var strength = new ushort[n];
        var dist = new ushort[n];
        Array.Fill(dist, ushort.MaxValue);
        var queue = new Queue<int>();
        for (var c = 0; c < n; c++)
        {
            if (partner[c] < 0) continue;
            var p = pressureOf[c];
            var sh = shearOf[c];
            kind[c] = (byte)(p > 0 && p >= sh ? BoundaryKind.Convergent : p < 0 && -p >= sh ? BoundaryKind.Divergent : BoundaryKind.Transform);
            strength[c] = maxPressure > 0 ? (ushort)Math.Round(Math.Abs(p) / maxPressure * 1000) : (ushort)0;
            dist[c] = 0;
            queue.Enqueue(c);
        }

        while (queue.TryDequeue(out var c))
        {
            foreach (var nb in grid.Neighbours(c))
            {
                if (plateOf[nb] != plateOf[c] || dist[nb] != ushort.MaxValue) continue;
                dist[nb] = (ushort)Math.Min(ushort.MaxValue - 1, dist[c] + 1);
                queue.Enqueue(nb);
            }
        }

        layers.BoundaryKind = kind;
        layers.CollisionStrengthPermille = strength;
        layers.BoundaryDistanceCells = dist;
    }

    /// <summary>
    /// Continental shelves as the version 5 stage made them, seeded from land cells with continental (or accreted arc) crust:
    /// ocean cells within <see cref="ElevationSettings.ShelfWidthCells"/> steps are raised to at least sea level +
    /// <see cref="ElevationSettings.ShelfDepthM"/>; convergent boundary cells (trenches) are neither raised nor crossed.
    /// </summary>
    public static void AddShelves(WorldLayers layers, int[] elevation, WorldGenSettings settings)
    {
        var s = settings.Elevation;
        if (s.ShelfWidthCells <= 0 || layers.Crust is not { } crust) return;
        var grid = layers.Grid;
        var n = grid.CellCount;
        var seaLevel = SeaLevelStage.Compute(layers, elevation, settings);
        var shelf = seaLevel + s.ShelfDepthM;
        var d = new int[n];
        Array.Fill(d, -1);
        var queue = new Queue<int>();
        for (var c = 0; c < n; c++)
        {
            if (elevation[c] < seaLevel || crust.Kind[c] == CrustKind.Oceanic) continue;
            d[c] = 0;
            queue.Enqueue(c);
        }

        while (queue.TryDequeue(out var c))
        {
            if (d[c] >= s.ShelfWidthCells) continue;
            foreach (var nb in grid.Neighbours(c))
            {
                if (d[nb] >= 0 || elevation[nb] >= seaLevel || layers.BoundaryKind[nb] == (byte)BoundaryKind.Convergent) continue;
                d[nb] = d[c] + 1;
                queue.Enqueue(nb);
            }
        }

        for (var c = 0; c < n; c++)
            if (d[c] > 0) elevation[c] = Math.Max(elevation[c], shelf);
    }
}
