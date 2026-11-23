namespace WorldEcon.WorldGen.Grid;

/// <summary>
/// Parent lookup between two nested grids whose frequencies divide (coarse m_c, fine m_f = k·m_c).
/// A fine lattice point (face; i, j) lies in the coarse lattice triangle with corners
/// (⌊i/k⌋, ⌊j/k⌋), … on the same face. Its parent is the corner whose coarse centre has the
/// largest dot product with the fine centre; ties go to the lowest coarse id. A fine point that
/// coincides with a coarse lattice point has that coarse point as parent.
/// </summary>
/// <remarks>
/// A fine point with remainder ri + rj = k lies on the diagonal shared by the coarse up triangle
/// (⌊i/k⌋, ⌊j/k⌋) and the down triangle next to it; it is assigned to the up triangle. The two
/// candidate sets differ only in the third corner. On the flat lattice a point on that diagonal is
/// at most 0.5 coarse edge from the nearer diagonal endpoint and at least 0.866 coarse edge from
/// either third corner, so the third corner does not win in either set and the choice does not
/// change the parent (checked against brute force for fine m = 12, coarse m = 4).
/// </remarks>
public static class GridHierarchy
{
    /// <summary>The coarse tile that contains fine tile <paramref name="fineId"/>.</summary>
    public static int Parent(TileGrid fine, TileGrid coarse, int fineId)
    {
        int k = Ratio(fine, coarse);
        return ParentCore(fine, coarse, k, fineId);
    }

    /// <summary>
    /// Parent of every fine tile, indexed by fine id. Filled in parallel; each index writes only its
    /// own slot, so the result does not depend on the thread count.
    /// </summary>
    public static int[] Parents(TileGrid fine, TileGrid coarse)
    {
        int k = Ratio(fine, coarse);
        var result = new int[fine.Count];
        const int chunk = 1 << 16;
        int chunks = (result.Length + chunk - 1) / chunk;
        Parallel.For(0, chunks, c =>
        {
            int end = Math.Min(result.Length, (c + 1) * chunk);
            for (int id = c * chunk; id < end; id++)
                result[id] = ParentCore(fine, coarse, k, id);
        });
        return result;
    }

    private static int Ratio(TileGrid fine, TileGrid coarse)
    {
        ArgumentNullException.ThrowIfNull(fine);
        ArgumentNullException.ThrowIfNull(coarse);
        if (fine.Frequency % coarse.Frequency != 0)
            throw new ArgumentException(
                $"Coarse frequency {coarse.Frequency} does not divide fine frequency {fine.Frequency}.", nameof(coarse));
        return fine.Frequency / coarse.Frequency;
    }

    private static int ParentCore(TileGrid fine, TileGrid coarse, int k, int fineId)
    {
        var (f, i, j) = fine.Lattice(fineId);
        int ci = i / k, cj = j / k, ri = i - ci * k, rj = j - cj * k;
        if (ri == 0 && rj == 0) return coarse.Id(f, ci, cj);

        // Up triangle (ci, cj), (ci+1, cj), (ci, cj+1) when ri + rj ≤ k; otherwise the down triangle
        // (ci+1, cj), (ci+1, cj+1), (ci, cj+1).
        (int I, int J) p0, p1, p2;
        if (ri + rj <= k) { p0 = (ci, cj); p1 = (ci + 1, cj); p2 = (ci, cj + 1); }
        else { p0 = (ci + 1, cj); p1 = (ci + 1, cj + 1); p2 = (ci, cj + 1); }

        var target = fine.Centre(fineId);
        int best = -1;
        double bestDot = double.NegativeInfinity;
        Consider(p0); Consider(p1); Consider(p2);
        return best;

        void Consider((int I, int J) p)
        {
            int id = coarse.Id(f, p.I, p.J);
            double d = Vector3d.Dot(coarse.Centre(id), target);
            if (d > bestDot || (d == bestDot && id < best)) { best = id; bestDot = d; }
        }
    }
}
