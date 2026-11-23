namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Tiles grouped by a coarser cell (globe or region): <see cref="Tiles"/> holds every tile id sorted by (cell, tile id),
/// and the tiles of cell c are <c>Tiles[Start[c] .. Start[c + 1])</c>. Built by one counting sort over a parent table
/// (two sequential passes, O(tiles + cells)); two <see cref="int"/> arrays, no per-cell objects.
/// </summary>
public sealed class TileCellIndex
{
    private TileCellIndex(int[] start, int[] tiles)
    {
        Start = start;
        Tiles = tiles;
    }

    /// <summary>Offsets into <see cref="Tiles"/>, length cell count + 1.</summary>
    public int[] Start { get; }

    /// <summary>Tile ids, ascending within each cell.</summary>
    public int[] Tiles { get; }

    public int CellCount => Start.Length - 1;

    /// <summary>Number of tiles of <paramref name="cell"/>.</summary>
    public int CountOf(int cell) => Start[cell + 1] - Start[cell];

    /// <summary>The ascending tile ids of <paramref name="cell"/>.</summary>
    public ReadOnlySpan<int> TilesOf(int cell) => Tiles.AsSpan(Start[cell], Start[cell + 1] - Start[cell]);

    /// <summary>Groups tile ids 0 … parent.Length − 1 by <paramref name="parent"/> (values in [0, <paramref name="cellCount"/>)).</summary>
    public static TileCellIndex Build(int[] parent, int cellCount)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var start = new int[cellCount + 1];
        foreach (var p in parent)
        {
            if ((uint)p >= (uint)cellCount) throw new ArgumentException($"Parent {p} is outside [0, {cellCount}).", nameof(parent));
            start[p + 1]++;
        }
        for (int c = 0; c < cellCount; c++) start[c + 1] += start[c];
        var next = (int[])start.Clone();
        var tiles = new int[parent.Length];
        for (int t = 0; t < parent.Length; t++) tiles[next[parent[t]]++] = t; // ascending t keeps each cell's ids ascending
        return new TileCellIndex(start, tiles);
    }
}
