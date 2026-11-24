using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Noise;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Tile stage 1 (design spec §6 step 4): tile elevation in metres. Settings: <see cref="TilesSettings"/>.
/// <para>
/// elevation = base + detail + ridge, rounded to nearest (midpoint away from zero) and clamped to <see cref="short"/>:
/// </para>
/// <list type="bullet">
/// <item>base: globe elevation interpolated with <see cref="TileField"/> (continuous across globe cells).</item>
/// <item>detail: <see cref="SphereNoise"/> in [−1, 1] at the tile centre (<see cref="TilesSettings.DetailOctaves"/>,
/// <see cref="TilesSettings.DetailFrequency"/>, persistence 0.5, lacunarity 2) × <see cref="TilesSettings.DetailAmplitudeM"/>
/// × relief scale / 10,000. The relief scale of a globe cell is (largest − smallest elevation of the cell and its
/// neighbours) × 10 bp per metre, clamped to [<see cref="TilesSettings.ReliefScaleMinBp"/>,
/// <see cref="TilesSettings.ReliefScaleMaxBp"/>]; it is interpolated with <see cref="TileField"/> like the base, so the
/// amplitude has no step at globe-cell borders.</item>
/// <item>ridge: <see cref="TilesSettings.RidgeAmplitudeM"/> × (1 − |n|)² × ridge weight, with n a second noise field (same
/// octaves and frequency, own seed). (1 − |n|)² is 1 on the zero lines of n, which gives narrow crests. The ridge weight of a
/// globe cell is 1 for a convergent boundary cell at or above the globe sea level, else 0, interpolated with
/// <see cref="TileField"/> (falls to 0 over one globe cell).</item>
/// </list>
/// Neighbour bound: between neighbouring tiles |Δ elevation| ≤ D/k + A·S·G·d + A·ΔS + R·Gr·d + 1, where D is the largest
/// globe elevation difference between neighbouring cells, k the frequency ratio tile/globe, A the detail amplitude, S
/// the largest scale (ReliefScaleMaxBp / 10,000), ΔS the largest neighbour difference of the scale field, G the bound of
/// the noise gradient (<see cref="NoiseGradientBound"/>), d the chord between the tile centres, R the ridge amplitude
/// and Gr = 2·G (derivative of (1 − |n|)²); the + 1 is rounding. Tested in <c>TileStagesTests.Neighbouring_tile_elevations_stay_within_the_documented_bound</c>.
/// </summary>
public static class TileElevationStage
{
    internal const double Persistence = 0.5;
    internal const double Lacunarity = 2.0;

    public static void Run(TileLayers tiles, WorldLayers globe, WorldGenSettings settings, ulong seed, ParallelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(globe);
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Tiles;
        var scale = ReliefScaleBp(globe, s);
        var ridgeWeight = RidgeWeight(globe, s);
        var globeElevation = globe.ElevationM;
        var field = new TileField(tiles.Grid, tiles.GlobeTiles);
        var grid = tiles.Grid;
        var detailSeed = DeterministicHash.Combine(seed, 1, 0, 0);
        var ridgeSeed = DeterministicHash.Combine(seed, 2, 0, 0);
        int octaves = s.DetailOctaves;
        double frequency = s.DetailFrequency;
        double amplitude = s.DetailAmplitudeM;
        double ridgeAmplitude = s.RidgeAmplitudeM;
        var result = new short[grid.Count];

        TileParallel.For(grid.Count, options, (start, end) =>
        {
            for (int id = start; id < end; id++)
            {
                var w = field.At(id);
                double e = w.Apply(globeElevation);
                var p = grid.Centre(id);
                if (amplitude > 0 && octaves > 0)
                {
                    double sc = w.Apply(scale) / 10_000.0;
                    e += SphereNoise.Sample(p, detailSeed, octaves, frequency, Persistence, Lacunarity) * amplitude * sc;
                }
                if (ridgeAmplitude > 0 && octaves > 0)
                {
                    double rw = w.Apply(ridgeWeight);
                    if (rw > 0)
                    {
                        double n = SphereNoise.Sample(p, ridgeSeed, octaves, frequency, Persistence, Lacunarity);
                        double r = 1 - Math.Abs(n);
                        e += ridgeAmplitude * r * r * rw;
                    }
                }
                result[id] = (short)Math.Clamp(Math.Round(e, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
            }
        });

        tiles.ElevationM = result;
    }

    /// <summary>Relief scale per globe cell in bp (see the class comment).</summary>
    internal static double[] ReliefScaleBp(WorldLayers globe, TilesSettings s)
    {
        var n = globe.Grid.CellCount;
        var e = globe.ElevationM;
        var result = new double[n];
        for (int c = 0; c < n; c++)
        {
            int lo = e[c], hi = e[c];
            foreach (var nb in globe.Grid.Neighbours(c))
            {
                lo = Math.Min(lo, e[nb]);
                hi = Math.Max(hi, e[nb]);
            }
            result[c] = Math.Clamp((long)(hi - lo) * 10, s.ReliefScaleMinBp, s.ReliefScaleMaxBp);
        }
        return result;
    }

    /// <summary>
    /// Crest weight per globe cell, in [0, 1] (version 6, user decision): cells at or above the globe sea level whose last
    /// mountain building (<see cref="Tectonics.CrustRecord.LastOrogenyAgeMy"/>) is younger than
    /// <see cref="TilesSettings.RidgeMaxOrogenyAgeMy"/> get kind weight × (1 − age / limit), with Himalayan 1.0, Andean
    /// 0.8 and arc 0.5; others 0. Old belts and sutures inside plates get crests, and belts wider than one cell get them
    /// across their width. A world without a crust record falls back to convergent boundary cells (weight 1).
    /// </summary>
    internal static double[] RidgeWeight(WorldLayers globe, TilesSettings s)
    {
        var n = globe.Grid.CellCount;
        var result = new double[n];
        var crust = globe.Crust;
        for (int c = 0; c < n; c++)
        {
            if (globe.ElevationM[c] < globe.SeaLevelM) continue;
            if (crust is null)
            {
                if (globe.BoundaryKind[c] == (byte)BoundaryKind.Convergent) result[c] = 1;
                continue;
            }

            var age = crust.LastOrogenyAgeMy[c];
            if (age == Tectonics.CrustRecord.Never || age >= s.RidgeMaxOrogenyAgeMy) continue;
            var kind = crust.Orogeny[c] switch
            {
                Tectonics.OrogenyKind.Himalayan => 1.0,
                Tectonics.OrogenyKind.Andean => 0.8,
                Tectonics.OrogenyKind.Arc => 0.5,
                _ => 0.0,
            };
            result[c] = kind * (1 - age / (double)s.RidgeMaxOrogenyAgeMy);
        }

        return result;
    }

    /// <summary>
    /// Upper bound of |∇n| on the unit sphere for the detail noise n (value noise, trilinear with smoothstep): each octave
    /// has partial derivatives ≤ 1.5 × 2 × frequency, so |∇| ≤ 3√3 × frequency; octaves are summed with their amplitude
    /// and divided by the total amplitude.
    /// </summary>
    public static double NoiseGradientBound(int octaves, double frequency)
    {
        double sum = 0, norm = 0, amp = 1, f = frequency;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * f;
            norm += amp;
            amp *= Persistence;
            f *= Lacunarity;
        }
        return norm == 0 ? 0 : 3 * Math.Sqrt(3) * sum / norm;
    }

    public static ulong Hash(TileLayers t) => LayerHash.Of(t.ElevationM);
}
