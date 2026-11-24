using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Noise;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// Elevation from the crust record (design section 6, Airy isostasy). Continental and accreted-arc crust stands at
/// <see cref="ContinentalBaseM"/> at 35 km thickness and rises <see cref="AiryFactor"/> m per m of extra crust (mountain
/// roots about 5–6 times their height). Oceanic crust deepens with age (2,600 m + 350 m × √age, Parsons–Sclater).
/// Continental crust within <see cref="MarginCells"/> cells of oceanic crust drops toward the ocean, so the continental
/// share above the land share is flooded at the margins (shelves), not through interiors. Noise of
/// <see cref="TectonicsSettings.TopographyNoiseM"/> stays below the margin drop.
/// </summary>
public static class Isostasy
{
    public const int ContinentalBaseM = 500;

    /// <summary>(ρ_mantle − ρ_crust) / ρ_mantle with 3,300 and 2,800 kg/m³.</summary>
    public const double AiryFactor = 0.15;

    public const int MarginCells = 2;

    /// <summary>Drop of continental crust next to oceanic crust (margin distance 1), metres; half of it at distance 2.</summary>
    public const int MarginDropM = 800;

    public static int[] Elevation(GlobeGrid grid, GlobeGeometry geo, CrustRecord perCell, TectonicsSettings t, ElevationSettings e, ulong seed)
    {
        var n = grid.CellCount;
        var dist = DistanceToOcean(grid, perCell);
        var result = new int[n];
        double frequency = e.NoiseFrequency;
        var persistence = e.NoisePersistenceBp / 10_000.0;
        var lacunarity = e.NoiseLacunarityBp / 10_000.0;
        for (var c = 0; c < n; c++)
        {
            double h;
            var thicknessM = perCell.ThicknessDam[c] * 10.0;
            if (perCell.Kind[c] == CrustKind.Oceanic)
            {
                var age = perCell.FormedAgeMy[c] == CrustRecord.Never ? 200 : Math.Min(200, (int)perCell.FormedAgeMy[c]);
                h = -(2_600 + 350 * Math.Sqrt(age)) + (thicknessM - CrustRecord.OceanicThicknessDam * 10.0) * AiryFactor;
            }
            else
            {
                h = ContinentalBaseM + (thicknessM - CrustRecord.ContinentalThicknessDam * 10.0) * AiryFactor;
                if (dist[c] >= 1 && dist[c] <= MarginCells) h -= MarginDropM * (MarginCells - dist[c] + 1) / (double)MarginCells;
            }

            if (t.TopographyNoiseM > 0 && e.NoiseOctaves > 0)
                h += SphereNoise.Sample(geo.Centre(c), seed, e.NoiseOctaves, frequency, persistence, lacunarity) * t.TopographyNoiseM;
            result[c] = Math.Clamp((int)Math.Round(h), e.MinElevationM, e.MaxElevationM);
        }

        return result;
    }

    /// <summary>Steps from each cell to the nearest oceanic-crust cell (0 on oceanic crust; <see cref="int.MaxValue"/> when there is none).</summary>
    public static int[] DistanceToOcean(GlobeGrid grid, CrustRecord perCell)
    {
        var n = grid.CellCount;
        var d = new int[n];
        Array.Fill(d, int.MaxValue);
        var q = new Queue<int>();
        for (var c = 0; c < n; c++)
            if (perCell.Kind[c] == CrustKind.Oceanic) { d[c] = 0; q.Enqueue(c); }
        while (q.TryDequeue(out var c))
            foreach (var nb in grid.Neighbours(c))
                if (d[nb] == int.MaxValue) { d[nb] = d[c] + 1; q.Enqueue(nb); }
        return d;
    }
}
