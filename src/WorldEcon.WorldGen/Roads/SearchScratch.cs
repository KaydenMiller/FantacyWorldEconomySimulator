namespace WorldEcon.WorldGen.Roads;

/// <summary>
/// Reusable per-tile arrays for graph searches over a tile grid: cost so far, parent and two stamps (seen, closed). A new
/// search bumps the epoch instead of clearing the arrays, so a search costs only the tiles it touches.
/// </summary>
internal sealed class SearchScratch(int count)
{
    public readonly long[] G = new long[count];
    public readonly int[] Parent = new int[count];
    private readonly int[] _seen = new int[count];
    private readonly int[] _closed = new int[count];
    private int _epoch;

    public void Begin()
    {
        if (++_epoch == int.MaxValue)
        {
            Array.Clear(_seen);
            Array.Clear(_closed);
            _epoch = 1;
        }
    }

    public bool Seen(int t) => _seen[t] == _epoch;
    public void See(int t) => _seen[t] = _epoch;
    public bool Closed(int t) => _closed[t] == _epoch;
    public void Close(int t) => _closed[t] = _epoch;
}
