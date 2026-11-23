namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// <see cref="ITileBlockSource"/> over generated <see cref="TileLayers"/> (tests, PNG (Portable Network Graphics) output, persistence): the block of a
/// globe cell holds the tiles whose <see cref="TileLayers.GlobeParent"/> is the cell, in ascending id order. Each call
/// copies the columns, so a returned block does not share arrays with the layers.
/// </summary>
public sealed class InMemoryTileBlockSource : ITileBlockSource
{
    private readonly TileLayers _tiles;
    private readonly TileCellIndex _index;

    public InMemoryTileBlockSource(TileLayers tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Landform.Length != tiles.Count)
            throw new ArgumentException("Tile layers must have completed TileLandform.", nameof(tiles));
        _tiles = tiles;
        _index = tiles.GlobeCells ?? TileCellIndex.Build(tiles.GlobeParent, tiles.GlobeTiles.Count);
    }

    /// <summary>Number of globe cells (valid arguments are 0 … CellCount − 1).</summary>
    public int CellCount => _index.CellCount;

    /// <summary>The block of <paramref name="globeCell"/>; null for a cell id outside the grid.</summary>
    public TileBlock? Get(int globeCell)
    {
        if ((uint)globeCell >= (uint)_index.CellCount) return null;
        var ids = _index.TilesOf(globeCell).ToArray();
        int n = ids.Length;
        var t = _tiles;
        var elevation = new short[n];
        var landform = new byte[n];
        var biome = new byte[n];
        var water = new byte[n];
        var lake = new int[n];
        var down = new sbyte[n];
        var flow = new long[n];
        var temperature = new short[n];
        var precipitation = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            int id = ids[i];
            elevation[i] = t.ElevationM[id];
            landform[i] = t.Landform[id];
            biome[i] = t.Biome[id];
            water[i] = t.WaterKind[id];
            lake[i] = t.LakeId[id];
            down[i] = t.DownstreamSlot[id];
            flow[i] = t.FlowAccum[id];
            temperature[i] = t.TemperatureDeciC[id];
            precipitation[i] = t.PrecipitationMm[id];
        }
        return new TileBlock(globeCell, ids, elevation, landform, biome, water, lake, down, flow, temperature, precipitation);
    }

    /// <summary>Blocks of every globe cell in ascending cell order, created lazily (one block in memory at a time unless the caller keeps them).</summary>
    public IEnumerable<TileBlock> All()
    {
        for (int c = 0; c < _index.CellCount; c++) yield return Get(c)!;
    }

    public Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Get(globeCell));
    }
}
