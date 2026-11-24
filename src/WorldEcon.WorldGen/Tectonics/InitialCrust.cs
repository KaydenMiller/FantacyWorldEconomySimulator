using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>The starting crust: the per-cell record, the craton seed cells, and the minimum seed separation used (0 for a supercontinent).</summary>
public sealed record InitialCrustResult(CrustRecord Record, IReadOnlyList<int> CratonSeeds, double MinSeparationRad);

/// <summary>
/// Starting crust (design section 3). <see cref="TectonicsSettings.CratonCount"/> cratons grow by randomised flood fill
/// until <see cref="PlateSettings.ContinentalFractionBp"/> of cells are continental, independent of plates.
/// <see cref="TectonicsSettings.StartArrangement"/> 0 seeds them near one random point and grows outward from it (a
/// supercontinent); 1 seeds them at least two craton radii apart. Craton crust is older than the history; ocean crust is
/// 0–200 My old.
/// </summary>
public static class InitialCrust
{
    public static InitialCrustResult Build(GlobeGrid grid, GlobeGeometry geo, TectonicsSettings t, PlateSettings p, ulong seed)
    {
        var n = grid.CellCount;
        var rng = new Xoshiro256StarStar(seed);
        var target = (int)Math.Round((long)p.ContinentalFractionBp * n / 10_000.0);
        var cratons = Math.Clamp(t.CratonCount, 1, Math.Max(1, n));
        var fraction = Math.Max(1e-6, p.ContinentalFractionBp / 10_000.0);
        // Craton radius r from its share of the sphere: f·4π/K = π r².
        var cratonRadius = Math.Sqrt(4 * fraction / cratons);
        var seeds = new List<int>(cratons);
        double minSep;
        Vector3d centre = geo.Centre(rng.NextInt(n));

        if (t.StartArrangement == 0)
        {
            minSep = 0;
            // Seeds inside the cap that holds the continental share: cos θ = 1 − 2f.
            var capCos = 1 - 2 * fraction;
            var inCap = Enumerable.Range(0, n).Where(c => Vector3d.Dot(geo.Centre(c), centre) >= capCos).ToArray();
            for (var k = 0; k < cratons && inCap.Length > 0; k++) seeds.Add(inCap[rng.NextInt(inCap.Length)]);
        }
        else
        {
            minSep = 2 * cratonRadius;
            while (seeds.Count < cratons)
            {
                for (var attempt = 0; attempt < 2_000 && seeds.Count < cratons; attempt++)
                {
                    var c = rng.NextInt(n);
                    if (seeds.All(s => Angle(geo, s, c) >= minSep)) seeds.Add(c);
                }

                if (seeds.Count < cratons) minSep *= 0.9;
            }
        }

        var owner = new int[n];
        Array.Fill(owner, -1);
        // Priority: supercontinent grows outward from the centre; scattered cratons grow around their own seeds.
        var queue = new PriorityQueue<(int Cell, int Craton), (double Key, int Cell)>(
            Comparer<(double Key, int Cell)>.Create((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Cell.CompareTo(b.Cell)));
        double Key(int cell, int craton)
        {
            var anchor = t.StartArrangement == 0 ? centre : geo.Centre(seeds[craton]);
            return Math.Acos(Math.Clamp(Vector3d.Dot(geo.Centre(cell), anchor), -1, 1)) + 0.5 * cratonRadius * Unit(rng.NextULong());
        }

        var claimed = 0;
        for (var k = 0; k < seeds.Count; k++)
            queue.Enqueue((seeds[k], k), (0, seeds[k]));
        while (claimed < target && queue.TryDequeue(out var item, out _))
        {
            if (owner[item.Cell] >= 0) continue;
            owner[item.Cell] = item.Craton;
            claimed++;
            foreach (var nb in grid.Neighbours(item.Cell))
                if (owner[nb] < 0) queue.Enqueue((nb, item.Craton), (Key(nb, item.Craton), nb));
        }

        var r = new CrustRecord(n);
        var cratonAge = new ushort[seeds.Count];
        for (var k = 0; k < cratonAge.Length; k++)
            cratonAge[k] = (ushort)Math.Min(CrustRecord.Never - 1, t.SimulatedMy + 500 + rng.NextInt(1_500));
        for (var c = 0; c < n; c++)
        {
            var s = r.Allocate();
            if (owner[c] >= 0)
            {
                r.Kind[s] = CrustKind.Continental;
                r.ThicknessDam[s] = CrustRecord.ContinentalThicknessDam;
                r.FormedAgeMy[s] = cratonAge[owner[c]];
            }
            else
            {
                r.Kind[s] = CrustKind.Oceanic;
                r.ThicknessDam[s] = CrustRecord.OceanicThicknessDam;
                r.FormedAgeMy[s] = (ushort)rng.NextInt(201);
            }
        }

        return new InitialCrustResult(r, seeds, minSep);
    }

    private static double Angle(GlobeGeometry geo, int a, int b) =>
        Math.Acos(Math.Clamp(Vector3d.Dot(geo.Centre(a), geo.Centre(b)), -1, 1));

    private static double Unit(ulong v) => (v >> 11) * (1.0 / ((1UL << 53) - 1));
}
