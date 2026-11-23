using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>A tectonic plate: its continental flag, Euler rotation (axis and angular speed) and seed cell.</summary>
public sealed record PlateInfo(int Id, bool Continental, Vector3d RotationAxis, double AngularSpeed, int SeedCell);

/// <summary>Stage 1: partitions the globe into plates by randomised-priority flood fill from random seed cells.</summary>
public static class PlateStage
{
    public static void Run(WorldLayers layers, WorldGenSettings s, ulong seed)
    {
        var grid = layers.Grid;
        var cellCount = grid.CellCount;
        var plateCount = Math.Clamp(s.Plates.Count, 1, cellCount);
        var rng = new Xoshiro256StarStar(seed);

        var seeds = new int[plateCount];
        var taken = new bool[cellCount];
        for (var p = 0; p < plateCount; p++)
        {
            int c;
            do
            {
                c = rng.NextInt(cellCount);
            }
            while (taken[c]);
            taken[c] = true;
            seeds[p] = c;
        }

        var plateOf = new int[cellCount];
        Array.Fill(plateOf, -1);
        var sizes = new int[plateCount];

        // Element is (cell, plate); priority is (random value drawn at enqueue, cell id) so ties resolve by cell id.
        var queue = new PriorityQueue<(int Cell, int Plate), (ulong Priority, int Cell)>(
            Comparer<(ulong Priority, int Cell)>.Create((a, b) =>
            {
                var byPriority = a.Priority.CompareTo(b.Priority);
                return byPriority != 0 ? byPriority : a.Cell.CompareTo(b.Cell);
            }));

        // Seed cells are claimed up front so a neighbour's growth cannot take another plate's seed cell.
        for (var p = 0; p < plateCount; p++)
        {
            plateOf[seeds[p]] = p;
            sizes[p] = 1;
        }

        for (var p = 0; p < plateCount; p++)
        {
            foreach (var n in grid.Neighbours(seeds[p]))
            {
                if (plateOf[n] < 0)
                    queue.Enqueue((n, p), (rng.NextULong(), n));
            }
        }

        while (queue.TryDequeue(out var item, out _))
        {
            if (plateOf[item.Cell] >= 0)
                continue;
            plateOf[item.Cell] = item.Plate;
            sizes[item.Plate]++;
            foreach (var n in grid.Neighbours(item.Cell))
            {
                if (plateOf[n] < 0)
                    queue.Enqueue((n, item.Plate), (rng.NextULong(), n));
            }
        }

        var targetCells = (long)s.Plates.ContinentalFractionBp * cellCount / 10_000;
        var continental = new bool[plateCount];
        long cumulative = 0;
        for (var p = 0; p < plateCount && cumulative < targetCells; p++)
        {
            continental[p] = true;
            cumulative += sizes[p];
        }

        var minSpeed = s.Plates.MinAngularSpeedPermille / 1000.0;
        var maxSpeed = s.Plates.MaxAngularSpeedPermille / 1000.0;
        var plates = new PlateInfo[plateCount];
        for (var p = 0; p < plateCount; p++)
        {
            var axis = RandomUnitAxis(rng);
            var speed = minSpeed + (maxSpeed - minSpeed) * ToUnit(rng.NextULong());
            plates[p] = new PlateInfo(p, continental[p], axis, speed, seeds[p]);
        }

        layers.PlateOf = plateOf;
        layers.Plates = plates;
    }

    /// <summary>Tangent velocity of the plate containing <paramref name="cell"/>: Cross(axis, centre) × speed.</summary>
    public static Vector3d MotionAt(WorldLayers l, int cell)
    {
        var plate = l.Plates[l.PlateOf[cell]];
        return Vector3d.Cross(plate.RotationAxis, l.Geometry.Centre(cell)) * plate.AngularSpeed;
    }

    public static ulong Hash(WorldLayers l) => LayerHash.Of(l.PlateOf);

    // Maps a ulong to [0, 1].
    private static double ToUnit(ulong v) => (v >> 11) * (1.0 / ((1UL << 53) - 1));

    private static Vector3d RandomUnitAxis(Xoshiro256StarStar rng)
    {
        while (true)
        {
            var v = new Vector3d(
                (2 * ToUnit(rng.NextULong())) - 1,
                (2 * ToUnit(rng.NextULong())) - 1,
                (2 * ToUnit(rng.NextULong())) - 1);
            if (v.Length > 1e-9)
                return v.Normalize();
        }
    }
}
