namespace WorldEcon.Domain.Geography;

/// <summary>One tile of a route's path, in order from NodeA (Sequence 0) to NodeB. Key (RouteId, Sequence).</summary>
public sealed class RouteTile
{
    public RouteId RouteId { get; private set; }
    public int Sequence { get; private set; }
    public int TileId { get; private set; }

    private RouteTile() { } // EF

    public RouteTile(RouteId routeId, int sequence, int tileId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(tileId);
        RouteId = routeId;
        Sequence = sequence;
        TileId = tileId;
    }

    /// <summary>Rows for <paramref name="tiles"/> in order.</summary>
    public static IEnumerable<RouteTile> For(RouteId routeId, IReadOnlyList<int> tiles)
    {
        for (int i = 0; i < tiles.Count; i++)
            yield return new RouteTile(routeId, i, tiles[i]);
    }
}
