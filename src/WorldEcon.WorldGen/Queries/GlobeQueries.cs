using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Queries;

/// <summary>Pure read-only queries over <see cref="WorldLayers"/>, used by the CLI <c>globe</c> command.</summary>
public static class GlobeQueries
{
    /// <summary>Minimum composition share (basis points) of Basin, DryLakeBed or Lake for <see cref="WasatchCells"/>.</summary>
    public const int WasatchMinShareBp = 2000;

    /// <summary>
    /// Cells whose biome is <see cref="Biome.Mountain"/> and whose composition has Basin, DryLakeBed or Lake at
    /// <see cref="WasatchMinShareBp"/> or more (a mountain cell with a valley floor or lake, as at the Wasatch front).
    /// Basin floor cells (own <see cref="WaterBodyKind"/> Lake or DryLakeBed) are excluded: they are the lowland, not the
    /// mountain rim beside it. Returned in cell-id order.
    /// </summary>
    public static List<int> WasatchCells(WorldLayers l)
    {
        var result = new List<int>();
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            if (l.BiomeOf[c] != (byte)Biome.Mountain) continue;
            if (l.WaterBodyKind[c] is (byte)WaterBodyKind.Lake or (byte)WaterBodyKind.DryLakeBed) continue;
            foreach (var e in l.Composition[c])
            {
                if (e.ShareBp >= WasatchMinShareBp && e.Landform is Landform.Basin or Landform.DryLakeBed or Landform.Lake)
                {
                    result.Add(c);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Number of water bodies of <paramref name="kind"/>: connected components (over <c>Neighbours</c>) of cells whose
    /// <see cref="WorldLayers.WaterBodyKind"/> is <paramref name="kind"/>. One closed basin is one component.
    /// </summary>
    public static int WaterBodies(WorldLayers l, WaterBodyKind kind)
    {
        var n = l.Grid.CellCount;
        var seen = new bool[n];
        var stack = new Stack<int>();
        var count = 0;
        for (var c = 0; c < n; c++)
        {
            if (seen[c] || l.WaterBodyKind[c] != (byte)kind) continue;
            count++;
            seen[c] = true;
            stack.Push(c);
            while (stack.TryPop(out var cur))
            {
                foreach (var nb in l.Grid.Neighbours(cur))
                {
                    if (seen[nb] || l.WaterBodyKind[nb] != (byte)kind) continue;
                    seen[nb] = true;
                    stack.Push(nb);
                }
            }
        }

        return count;
    }

    /// <summary>Total river length in km: the sum over <see cref="WorldLayers.RiverEdges"/> of the great-circle distance between the two cell centres.</summary>
    public static double RiverLengthKm(WorldLayers l, double planetRadiusKm)
    {
        double total = 0;
        foreach (var e in l.RiverEdges)
            total += l.Geometry.GreatCircleKm(e.LowCell, e.HighCell, planetRadiusKm);
        return total;
    }

    /// <summary>Share of the sphere's surface area on non-ocean cells, 0–100, weighting each cell by its area.</summary>
    public static double LandPercent(WorldLayers l)
    {
        double land = 0, total = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var a = l.Geometry.CellAreaKm2(c, 1.0);
            total += a;
            if (!l.IsOcean(c)) land += a;
        }
        return total == 0 ? 0 : 100.0 * land / total;
    }

    /// <summary>Nearest-rank percentile (0–100) of <paramref name="values"/>; the input is not modified.</summary>
    public static int Percentile(IReadOnlyCollection<int> values, int percent)
    {
        if (values.Count == 0) throw new ArgumentException("No values.", nameof(values));
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
