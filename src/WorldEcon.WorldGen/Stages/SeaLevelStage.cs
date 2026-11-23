using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>
/// Stage 3: picks the sea level from the area-weighted elevation percentile for
/// <see cref="SeaLevelSettings.LandFractionBp"/> (basis points, 10,000 = 100 %). The cut is the first cell,
/// in ascending (elevation, cell id) order, at which cumulative ocean area reaches the ocean target, so
/// when no elevation tie straddles the cut the land share is at most the target, short by less than one
/// cell's area. Cells tied at the cut elevation all count as land, which can push the land share above the
/// target by the tied block's area.
/// </summary>
public static class SeaLevelStage
{
    public static void Run(WorldLayers layers, WorldGenSettings settings) =>
        layers.SeaLevelM = Compute(layers, layers.ElevationM, settings);

    /// <summary>
    /// The sea level for <paramref name="elevation"/> (see the class comment). Stage 2 calls this for the continental
    /// shelves; raising ocean cells below the result does not change it, so Stage 3 returns the same value.
    /// </summary>
    internal static int Compute(WorldLayers layers, int[] elevation, WorldGenSettings settings)
    {
        var n = layers.Grid.CellCount;
        var radius = settings.Grid.PlanetRadiusKm;

        var order = new int[n];
        var area = new double[n];
        var total = 0.0;
        for (var c = 0; c < n; c++)
        {
            order[c] = c;
            area[c] = layers.Geometry.CellAreaKm2(c, radius);
            total += area[c];
        }

        // Ascending by (elevation, cell id).
        Array.Sort(order, (a, b) =>
        {
            var cmp = elevation[a].CompareTo(elevation[b]);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        var landBp = Math.Clamp(settings.SeaLevel.LandFractionBp, 0, 10_000);
        if (landBp == 0)
            return elevation[order[n - 1]] + 1;

        var targetOcean = total * (10_000 - landBp) / 10_000.0;
        var cumulative = 0.0;
        for (var i = 0; i < n; i++)
        {
            if (cumulative >= targetOcean)
                return elevation[order[i]];

            cumulative += area[order[i]];
        }

        return elevation[order[n - 1]] + 1;
    }

    public static ulong Hash(WorldLayers l) => LayerHash.Of([l.SeaLevelM]);
}
