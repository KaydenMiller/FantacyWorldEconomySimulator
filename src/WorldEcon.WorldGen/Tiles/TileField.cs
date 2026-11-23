using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// The three coarse cells and lattice-space barycentric weights of one fine tile. Weights are integers with
/// <see cref="WA"/> + <see cref="WB"/> + <see cref="WC"/> = <see cref="Scale"/>, the frequency ratio k (1 for a tile on a
/// coarse lattice point), so a sample is (WA·v[A] + WB·v[B] + WC·v[C]) / Scale, evaluated in that order.
/// </summary>
public readonly record struct TileWeights(int A, int B, int C, int WA, int WB, int WC, int Scale)
{
    public double Apply(int[] values) => ((double)WA * values[A] + (double)WB * values[B] + (double)WC * values[C]) / Scale;

    public double Apply(short[] values) => ((double)WA * values[A] + (double)WB * values[B] + (double)WC * values[C]) / Scale;

    public double Apply(double[] values) => (WA * values[A] + WB * values[B] + WC * values[C]) / Scale;
}

/// <summary>
/// Interpolation of a coarse-grid value (one per coarse cell) at a fine tile, for nested grids whose frequencies divide
/// (coarse G, fine F = k·G). The fine lattice point (face; i, j) lies in the coarse lattice triangle at
/// (⌊i/k⌋, ⌊j/k⌋) on the same face, with remainders (ri, rj):
/// <list type="bullet">
/// <item>up triangle when ri + rj ≤ k: corners (ci, cj), (ci+1, cj), (ci, cj+1) with weights (k−ri−rj, ri, rj);</item>
/// <item>down triangle otherwise: corners (ci+1, cj), (ci+1, cj+1), (ci, cj+1) with weights (k−rj, ri+rj−k, k−ri).</item>
/// </list>
/// The weights are linear in lattice coordinates and, on a shared triangle edge, only the two edge corners carry weight
/// with the same values from both sides, so the field is continuous across coarse triangle edges and icosahedron face
/// edges (an edge tile gives the same value from either face; tested). A tile on a coarse lattice point takes that
/// cell's value exactly. Integer arithmetic only for the topology; allocation-free.
/// </summary>
public sealed class TileField
{
    private readonly TileGrid _fine;
    private readonly TileGrid _coarse;
    private readonly int _k;

    public TileField(TileGrid fine, TileGrid coarse)
    {
        ArgumentNullException.ThrowIfNull(fine);
        ArgumentNullException.ThrowIfNull(coarse);
        if (fine.Frequency % coarse.Frequency != 0)
            throw new ArgumentException(
                $"Coarse frequency {coarse.Frequency} does not divide fine frequency {fine.Frequency}.", nameof(coarse));
        _fine = fine;
        _coarse = coarse;
        _k = fine.Frequency / coarse.Frequency;
    }

    /// <summary>Frequency ratio k = fine / coarse.</summary>
    public int Ratio => _k;

    /// <summary>Weights of tile <paramref name="tileId"/> (its canonical lattice position).</summary>
    public TileWeights At(int tileId)
    {
        var (f, i, j) = _fine.Lattice(tileId);
        return At(f, i, j);
    }

    /// <summary>Weights of the fine lattice point (face; i, j), any face representation of it.</summary>
    public TileWeights At(int face, int i, int j)
    {
        int k = _k;
        int ci = i / k, cj = j / k, ri = i - ci * k, rj = j - cj * k;
        if (ri == 0 && rj == 0)
        {
            int id = _coarse.Id(face, ci, cj);
            // Scale 1 so the sample is the cell value exactly ((k·v) / k can differ from v in the last bit).
            return new TileWeights(id, id, id, 1, 0, 0, 1);
        }
        if (ri + rj <= k)
            return new TileWeights(
                _coarse.Id(face, ci, cj), _coarse.Id(face, ci + 1, cj), _coarse.Id(face, ci, cj + 1),
                k - ri - rj, ri, rj, k);
        return new TileWeights(
            _coarse.Id(face, ci + 1, cj), _coarse.Id(face, ci + 1, cj + 1), _coarse.Id(face, ci, cj + 1),
            k - rj, ri + rj - k, k - ri, k);
    }
}
