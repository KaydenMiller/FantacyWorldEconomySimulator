using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>
/// Stage 6: temperature and precipitation. Settings: <see cref="ClimateSettings"/>; the numbers below are its defaults.
/// <para>
/// Temperature (tenths of °C): −250 + 550 × cos(latitude'), giving +300 at the equator and −250 at the
/// poles, where latitude' is the cell's latitude shifted by <see cref="ClimateSettings.LatitudeNoiseAmplitudeDeg"/>
/// (15°) × fractal noise in [−1, 1] (<see cref="PerturbedLatitude"/>; seeded with the Stage 5 seed, so with equal
/// noise settings isotherms and wind band edges move together). Land loses 65 tenths per km above sea level. Ocean cells add their own current anomaly; land with
/// at least one ocean neighbour adds half of the strongest-magnitude anomaly among its ocean neighbours
/// (tie: lower neighbour id). Rounded to nearest, clamped to <see cref="short"/>.
/// </para>
/// <para>
/// Precipitation: moisture is advected for 2 × <see cref="ClimateSettings.MoistureIterations"/> passes, in
/// integer units, so the per-pass sums are exact. Each pass reads the previous pass's buffer and writes a
/// fresh one (double buffering), so the order cells are visited in does not change the result. Rain is
/// counted only over the second half of the passes: the first MoistureIterations passes are a warm-up that
/// lets the moisture front reach every cell it can reach before anything is counted.
/// </para>
/// <para>
/// Saturation depends on temperature: reference × 2^((T − 15 °C) / 10 °C), computed in integers by
/// <see cref="Saturation"/>, and is at least <see cref="ClimateSettings.SaturationFloorBp"/> (35 %) of the reference,
/// so cold air can carry moisture inland. At the start of each pass every ocean cell is reset to its own saturation.
/// Each cell sends (1 − <see cref="ClimateSettings.MoistureSpreadBp"/>) of its moisture to the wind slot
/// (<see cref="WorldLayers.WindDir"/>) and half of the spread to each of the two slots next to it
/// (slot ± 1 modulo the ring length, 5 on pentagons). Without the spread, each cell sends to one neighbour
/// only, and 28 % of land at m = 64 had no path from the ocean.
/// </para>
/// <para>
/// On each step from c to n, the fraction <see cref="ClimateSettings.MoistureBaseLossBp"/> +
/// <see cref="ClimateSettings.MoistureOrographicLossBpPerKm"/> × max(0, height gain above sea level) / 1000 m,
/// capped at <see cref="ClimateSettings.MoistureMaxLossBp"/> (15 %), falls as rain on n. The remainder goes into n's
/// next-pass buffer, summed over all senders. Descending steps lose only the base fraction, which produces a rain
/// shadow behind ridges. After each pass, a land cell holding more than its own saturation rains out
/// <see cref="ClimateSettings.SaturationRainOutBp"/> (20 %) of the excess and carries the rest on, so the excess
/// falls over several downwind cells. This happens when air cools inland or uphill, or where flows converge.
/// </para>
/// <para>
/// Scale: a mean per-pass rainfall of <see cref="ClimateSettings.MoistureFullScaleBp"/> of reference
/// saturation is 4000 mm/year on a linear scale, rounded to the nearest mm (so a cell with at least half a mm is not
/// 0). Values are linear up to <see cref="ClimateSettings.PrecipitationSoftKneeMm"/> (1,500 mm); above it they are
/// compressed toward 4000 by <see cref="SoftKnee"/>. Wind strength is not used: scaling the carried amount by strength / 160 left the
/// median land cell at 0 mm/year at m = 64.
/// </para>
/// </summary>
public static class ClimateStage
{
    // Fixed-point unit of moisture: reference saturation. Precipitation is normalised by it (fullScale), so it sets
    // rounding resolution only.
    private const long ReferenceSaturationUnits = 1_000_000;
    private const int EdgesPerCell = 3;

    // Overflow guard: moisture a land cell keeps above its saturation after rain-out is capped at this many saturations
    // (carried ≤ 9 × saturation). Not a tuning value: at default settings the output is identical for 8 and 16 on seeds
    // 42, 1, 2, 3 and 7 (m = 64), so the cap does not bind.
    private const long RetainedExcessCapSaturations = 8;

    // Linear precipitation is clamped here before the soft knee so span × over fits a long. For any input at or above
    // ceiling² the soft knee already returns ceiling − 1, so the clamp does not change output.
    private const long LinearClampMm = 1_000_000_000_000;
    private const int BpFull = 10_000;

    /// <summary>Runs the stage; <paramref name="noiseSeed"/> seeds the latitude perturbation noise (the Stage 5 seed, so equal noise settings move wind bands and isotherms together).</summary>
    public static void Run(WorldLayers layers, WorldGenSettings settings, ulong noiseSeed) => Run(layers, settings, noiseSeed, descending: false);

    /// <summary>Runs the stage visiting cells in descending id order within each pass when <paramref name="descending"/> is true (test hook for double buffering).</summary>
    internal static void Run(WorldLayers layers, WorldGenSettings settings, ulong noiseSeed, bool descending)
    {
        layers.TemperatureDeciC = Temperature(layers, settings.Climate, noiseSeed);
        layers.PrecipitationMm = Precipitation(layers, settings, layers.TemperatureDeciC, descending);
    }

    /// <summary>
    /// Latitude in degrees shifted by <paramref name="amplitudeDeg"/> × fractal noise in [−1, 1] (persistence 0.5,
    /// lacunarity 2), clamped to ±90. Amplitude 0 or 0 octaves returns the geometric latitude.
    /// </summary>
    internal static double PerturbedLatitude(WorldLayers layers, int cell, ulong seed, int amplitudeDeg, int frequency, int octaves)
    {
        var lat = layers.Geometry.LatLon(cell).LatDeg;
        if (amplitudeDeg == 0 || octaves == 0) return lat;
        var noise = Noise.SphereNoise.Sample(layers.Geometry.Centre(cell), seed, octaves, frequency);
        return Math.Clamp(lat + (amplitudeDeg * noise), -90.0, 90.0);
    }

    private static short[] Temperature(WorldLayers layers, ClimateSettings s, ulong noiseSeed)
    {
        double pole = s.PoleDeciC;
        double equatorMinusPole = s.EquatorDeciC - s.PoleDeciC;
        var grid = layers.Grid;
        var geo = layers.Geometry;
        var n = grid.CellCount;
        var result = new short[n];

        for (var c = 0; c < n; c++)
        {
            var latRad = PerturbedLatitude(layers, c, noiseSeed, s.LatitudeNoiseAmplitudeDeg, s.LatitudeNoiseFrequency, s.LatitudeNoiseOctaves) * Math.PI / 180;
            var t = pole + (equatorMinusPole * Math.Cos(latRad));

            if (layers.IsOcean(c))
            {
                t += layers.CurrentAnomalyDeciC[c];
            }
            else
            {
                var above = Math.Max(0, layers.ElevationM[c] - layers.SeaLevelM);
                t -= s.LapseDeciCPerKm * (above / 1000.0);

                var best = -1;
                var bestAnomaly = 0;
                foreach (var nb in grid.Neighbours(c))
                {
                    if (!layers.IsOcean(nb)) continue;
                    var a = (int)layers.CurrentAnomalyDeciC[nb];
                    var mag = Math.Abs(a);
                    var bestMag = Math.Abs(bestAnomaly);
                    if (best < 0 || mag > bestMag || (mag == bestMag && nb < best))
                    {
                        best = nb;
                        bestAnomaly = a;
                    }
                }

                if (best >= 0)
                    t += (long)bestAnomaly * s.CoastalAnomalyShareBp / (double)BpFull;
            }

            result[c] = (short)Math.Clamp(Math.Round(t, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
        }

        return result;
    }

    private static int[] Precipitation(WorldLayers layers, WorldGenSettings all, short[] temperature, bool descending)
    {
        var settings = all.Climate;
        var grid = layers.Grid;
        var n = grid.CellCount;
        var sideBp = Math.Clamp(settings.MoistureSpreadBp, 0, BpFull) / 2;
        var powers = PowersOfTwoPpm(settings.SaturationDoublingDegC);
        var maxLossBp = Math.Clamp(settings.MoistureMaxLossBp, 0, BpFull);

        // Per-cell constants, independent of the pass: saturation, and for each of the three outgoing edges
        // (wind slot, slot + 1, slot − 1) the target cell and loss in bp.
        var saturation = new long[n];
        var isOcean = new bool[n];
        var target = new int[n * EdgesPerCell];
        var lossBp = new long[n * EdgesPerCell];
        for (var c = 0; c < n; c++)
        {
            isOcean[c] = layers.IsOcean(c);
            saturation[c] = Math.Max(
                Saturation(temperature[c], settings.SaturationReferenceDeciC, settings.SaturationDoublingDegC, settings.SaturationMaxOctaves, powers),
                ReferenceSaturationUnits * settings.SaturationFloorBp / BpFull);
            var nb = grid.Neighbours(c);
            var k = nb.Length;
            var slot = (int)layers.WindDir[c];
            for (var e = 0; e < EdgesPerCell; e++)
            {
                var t = nb[e switch { 0 => slot, 1 => (slot + 1) % k, _ => (slot + k - 1) % k }];
                var gain = HeightAboveSea(layers, t) - HeightAboveSea(layers, c);
                var loss = settings.MoistureBaseLossBp + ((long)settings.MoistureOrographicLossBpPerKm * Math.Max(0, gain) / 1000);
                target[(c * EdgesPerCell) + e] = t;
                lossBp[(c * EdgesPerCell) + e] = Math.Clamp(loss, 0, maxLossBp);
            }
        }

        long rainOutBp = settings.SaturationRainOutBp;
        var current = new long[n];
        var next = new long[n];
        var rain = new long[n];
        var iterations = Math.Max(1, settings.MoistureIterations);

        // MoistureIterations warm-up passes bring the field to steady state; rain is counted over the next
        // MoistureIterations passes only.
        for (var pass = 0; pass < 2 * iterations; pass++)
        {
            var counting = pass >= iterations;
            for (var c = 0; c < n; c++)
                if (isOcean[c]) current[c] = saturation[c];
            Array.Clear(next);

            for (var i = 0; i < n; i++)
            {
                var c = descending ? n - 1 - i : i;
                var carried = current[c];
                if (carried == 0) continue;
                var side = carried * sideBp / BpFull;
                for (var e = 0; e < EdgesPerCell; e++)
                {
                    var amount = e == 0 ? carried - (2 * side) : side;
                    var edge = (c * EdgesPerCell) + e;
                    var dropped = amount * lossBp[edge] / BpFull;
                    if (counting) rain[target[edge]] += dropped;
                    next[target[edge]] += amount - dropped;
                }
            }

            // Land air above its own saturation (cooling inland or uphill, or converging flows) rains out
            // SaturationRainOutBp of the excess; the rest stays in the air and moves on, so a large excess falls over
            // several downwind cells instead of one.
            for (var c = 0; c < n; c++)
            {
                if (isOcean[c] || next[c] <= saturation[c]) continue;
                var excess = next[c] - saturation[c];
                var fall = Math.Max(excess * rainOutBp / BpFull, excess - (RetainedExcessCapSaturations * saturation[c]));
                if (counting) rain[c] += fall;
                next[c] -= fall;
            }

            (current, next) = (next, current);
        }

        // Fixed scale: a mean per-pass rainfall of MoistureFullScaleBp of reference saturation is the ceiling
        // (4000 mm/year by default) on the linear scale; values above the knee are compressed toward the ceiling.
        var result = new int[n];
        var fullScale = iterations * ReferenceSaturationUnits * settings.MoistureFullScaleBp / BpFull;
        if (fullScale <= 0) return result;
        var ceiling = settings.PrecipitationCeilingMm;
        var knee = Math.Clamp(settings.PrecipitationSoftKneeMm, 0, ceiling);
        for (var c = 0; c < n; c++)
        {
            // Rounded to nearest; Int128 because rain × ceiling can exceed a long at the largest settings.
            var linear = (((Int128)rain[c] * ceiling) + (fullScale / 2)) / fullScale;
            result[c] = SoftKnee((long)Int128.Min(linear, LinearClampMm), knee, ceiling);
        }
        return result;
    }

    /// <summary>
    /// Identity up to <paramref name="knee"/> mm; above it, knee + span × over / (over + span) with
    /// span = <paramref name="ceiling"/> − knee, which rises with slope 1 at the knee and approaches the ceiling without
    /// reaching it for finite input.
    /// </summary>
    internal static int SoftKnee(long linearMm, int knee, int ceiling = 4000)
    {
        if (linearMm <= knee) return (int)Math.Max(0, linearMm);
        long span = ceiling - knee;
        if (span == 0) return ceiling;
        var over = linearMm - knee;
        return (int)(knee + (span * over / (over + span)));
    }

    /// <summary>Saturation with the default <see cref="ClimateSettings"/> (reference 15 °C, doubling every 10 °C, ±8 octaves).</summary>
    internal static long Saturation(short temperatureDeciC)
    {
        var d = new ClimateSettings();
        return Saturation(temperatureDeciC, d.SaturationReferenceDeciC, d.SaturationDoublingDegC, d.SaturationMaxOctaves, PowersOfTwoPpm(d.SaturationDoublingDegC));
    }

    /// <summary>
    /// Saturation moisture for a temperature in tenths of °C: reference × 2^((T − reference) / doubling), evaluated in
    /// integers on whole degrees (floored) with a <paramref name="doublingDegC"/>-entry table (<see cref="PowersOfTwoPpm"/>)
    /// for the fractional power of two; the whole power of two is clamped to ± <paramref name="maxOctaves"/>.
    /// </summary>
    internal static long Saturation(short temperatureDeciC, int referenceDeciC, int doublingDegC, int maxOctaves, long[] powersPpm)
    {
        var degreesFromReference = (int)Math.Floor((temperatureDeciC - referenceDeciC) / 10.0);
        var octaves = (int)Math.Floor(degreesFromReference / (double)doublingDegC);
        var remainder = degreesFromReference - (octaves * doublingDegC);
        var value = ReferenceSaturationUnits * powersPpm[remainder] / 1_000_000;
        octaves = Math.Clamp(octaves, -maxOctaves, maxOctaves);
        return octaves >= 0 ? value << octaves : value >> -octaves;
    }

    /// <summary>round(2^(r / <paramref name="doublingDegC"/>) × 10^6) for r = 0 .. doublingDegC − 1.</summary>
    internal static long[] PowersOfTwoPpm(int doublingDegC)
    {
        var table = new long[doublingDegC];
        for (var r = 0; r < doublingDegC; r++)
            table[r] = (long)Math.Round(Math.Pow(2, r / (double)doublingDegC) * 1_000_000);
        return table;
    }

    private static int HeightAboveSea(WorldLayers layers, int c) => Math.Max(0, layers.ElevationM[c] - layers.SeaLevelM);

    public static ulong Hash(WorldLayers l) =>
        DeterministicHash.Combine(LayerHash.Of(l.TemperatureDeciC), LayerHash.Of(l.PrecipitationMm), 0, 0);
}
