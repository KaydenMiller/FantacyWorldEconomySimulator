using System.Globalization;
using System.Text;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Queries;

/// <summary>
/// Measurements of a generated globe for judging the plate history results (design 2026-11-24, section 9). Reported, not
/// checked: the user sets targets. Landmasses are land components (globe cells at or above sea level) over 1% of land.
/// A narrow-land (isthmus) cell is a land cell whose neighbour ring has water in two or more separate runs. A high belt
/// is a component of land cells at or above 2,000 m; its width is 2 × (largest steps from a cell to the belt edge) − 1.
/// </summary>
public sealed record WorldMetrics(
    int Landmasses,
    double LargestLandShare,
    IReadOnlyList<double> NearestNeighbourGapKm,
    double LandAbove2000mShare,
    IReadOnlyList<int> BeltWidthsCells,
    double IsthmusCellsPer1000Land,
    int LandmassesSplitByIsthmuses,
    double CellSpacingKm)
{
    public static WorldMetrics Compute(WorldLayers l, double planetRadiusKm)
    {
        var grid = l.Grid;
        var n = grid.CellCount;
        var land = new bool[n];
        var landCount = 0;
        for (var c = 0; c < n; c++)
            if (!l.IsOcean(c)) { land[c] = true; landCount++; }
        var spacing = Math.Sqrt(4 * Math.PI * planetRadiusKm * planetRadiusKm / n);
        if (landCount == 0) return new WorldMetrics(0, 0, [], 0, [], 0, 0, spacing);

        var (label, sizes) = Components(grid, land, null);
        var big = Enumerable.Range(0, sizes.Count).Where(k => sizes[k] > 0.01 * landCount).ToList();
        var largest = sizes.Max() / (double)landCount;

        // Ocean distance between landmasses: multi-source search from every big landmass over water.
        var owner = new int[n];
        var dist = new int[n];
        Array.Fill(owner, -1);
        var q = new Queue<int>();
        foreach (var k in big)
            for (var c = 0; c < n; c++)
                if (label[c] == k) { owner[c] = k; q.Enqueue(c); }
        var gap = big.ToDictionary(k => k, _ => int.MaxValue);
        while (q.TryDequeue(out var c))
        {
            foreach (var nb in grid.Neighbours(c))
            {
                if (owner[nb] >= 0 && owner[nb] != owner[c])
                {
                    var steps = dist[c] + dist[nb] + 1;
                    gap[owner[c]] = Math.Min(gap[owner[c]], steps);
                    gap[owner[nb]] = Math.Min(gap[owner[nb]], steps);
                    continue;
                }

                if (owner[nb] >= 0 || land[nb]) continue;
                owner[nb] = owner[c];
                dist[nb] = dist[c] + 1;
                q.Enqueue(nb);
            }
        }

        var gaps = big.Where(k => gap[k] != int.MaxValue).Select(k => (gap[k] - 1) * spacing).ToList();

        // High belts and their widths.
        var high = new bool[n];
        var highCount = 0;
        for (var c = 0; c < n; c++)
            if (land[c] && l.ElevationM[c] >= 2_000) { high[c] = true; highCount++; }
        var (beltLabel, beltSizes) = Components(grid, high, null);
        var edgeDist = new int[n];
        Array.Fill(edgeDist, -1);
        var bq = new Queue<int>();
        for (var c = 0; c < n; c++)
        {
            if (!high[c]) continue;
            foreach (var nb in grid.Neighbours(c))
                if (!high[nb]) { edgeDist[c] = 1; bq.Enqueue(c); break; }
        }

        while (bq.TryDequeue(out var c))
            foreach (var nb in grid.Neighbours(c))
                if (high[nb] && edgeDist[nb] < 0) { edgeDist[nb] = edgeDist[c] + 1; bq.Enqueue(nb); }
        var widths = new int[beltSizes.Count];
        for (var c = 0; c < n; c++)
            if (high[c]) widths[beltLabel[c]] = Math.Max(widths[beltLabel[c]], 2 * Math.Max(1, edgeDist[c]) - 1);

        // Narrow land: water in two or more separate runs around a land cell.
        var isthmus = new bool[n];
        var isthmusCount = 0;
        for (var c = 0; c < n; c++)
        {
            if (!land[c]) continue;
            var ring = grid.Neighbours(c);
            var runs = 0;
            for (var k = 0; k < ring.Length; k++)
                if (!land[ring[k]] && land[ring[(k + ring.Length - 1) % ring.Length]]) runs++;
            if (runs >= 2) { isthmus[c] = true; isthmusCount++; }
        }

        var withoutNecks = new bool[n];
        for (var c = 0; c < n; c++) withoutNecks[c] = land[c] && !isthmus[c];
        var (partLabel, partSizes) = Components(grid, withoutNecks, null);
        var split = 0;
        foreach (var k in big)
        {
            var parts = new HashSet<int>();
            for (var c = 0; c < n; c++)
                if (label[c] == k && partLabel[c] >= 0 && partSizes[partLabel[c]] >= Math.Max(2, 0.001 * landCount)) parts.Add(partLabel[c]);
            if (parts.Count >= 2) split++;
        }

        return new WorldMetrics(big.Count, largest, gaps, highCount / (double)landCount,
            widths.Where(w => w > 0).OrderBy(w => w).ToList(), 1000.0 * isthmusCount / landCount, split, spacing);
    }

    /// <summary>The metrics with the research reference values beside them.</summary>
    public string Format()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(inv, $"Landmasses (over 1% of land): {Landmasses}; largest {LargestLandShare:P0} of land"));
        sb.AppendLine("Ocean gap to the nearest other landmass: " + (NearestNeighbourGapKm.Count == 0 ? "none"
            : string.Join(", ", NearestNeighbourGapKm.Select(g => string.Create(inv, $"{g:N0} km")))));
        sb.AppendLine(string.Create(inv, $"Land at or above 2,000 m: {LandAbove2000mShare:P1}"));
        var widthsKm = BeltWidthsCells.Select(w => w * CellSpacingKm).ToList();
        sb.AppendLine(string.Create(inv, $"High belt widths: {(widthsKm.Count == 0 ? "none" : string.Join(", ", widthsKm.Select(w => $"{w:N0} km")))} (cell spacing {CellSpacingKm:N0} km)"));
        sb.AppendLine("  reference: Himalaya 151–350 km, Alps about 200 km, Tibet about 1,000 km");
        sb.AppendLine(string.Create(inv, $"Narrow-land cells: {IsthmusCellsPer1000Land:F1} per 1,000 land cells; landmasses that split without them: {LandmassesSplitByIsthmuses}"));
        return sb.ToString();
    }

    private static (int[] Label, List<int> Sizes) Components(Grid.GlobeGrid grid, bool[] member, int[]? _)
    {
        var n = grid.CellCount;
        var label = new int[n];
        Array.Fill(label, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (var c = 0; c < n; c++)
        {
            if (!member[c] || label[c] >= 0) continue;
            var k = sizes.Count;
            sizes.Add(0);
            label[c] = k;
            stack.Push(c);
            while (stack.TryPop(out var x))
            {
                sizes[k]++;
                foreach (var nb in grid.Neighbours(x))
                    if (member[nb] && label[nb] < 0) { label[nb] = k; stack.Push(nb); }
            }
        }

        return (label, sizes);
    }
}
