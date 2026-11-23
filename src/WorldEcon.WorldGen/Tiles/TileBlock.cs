namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// The tiles of one globe cell, stored as parallel columns (structure of arrays). Tile ids are ascending; every
/// column has <see cref="Count"/> elements and element i of each column describes tile <c>TileIds[i]</c>.
/// Landform, Biome and Water hold the byte values of <c>Landform</c>, <c>Biome</c> and <see cref="TileWaterKind"/>.
/// </summary>
public sealed class TileBlock
{
    public TileBlock(
        int globeCell, int[] tileIds, short[] elevationM, byte[] landform, byte[] biome, byte[] water,
        int[] lakeId, sbyte[] downstream, long[] flow, short[] temperatureDeciC, ushort[] precipitationMm)
    {
        ArgumentNullException.ThrowIfNull(tileIds);
        ArgumentNullException.ThrowIfNull(elevationM);
        ArgumentNullException.ThrowIfNull(landform);
        ArgumentNullException.ThrowIfNull(biome);
        ArgumentNullException.ThrowIfNull(water);
        ArgumentNullException.ThrowIfNull(lakeId);
        ArgumentNullException.ThrowIfNull(downstream);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(temperatureDeciC);
        ArgumentNullException.ThrowIfNull(precipitationMm);

        var count = tileIds.Length;
        CheckLength(nameof(elevationM), elevationM.Length, count);
        CheckLength(nameof(landform), landform.Length, count);
        CheckLength(nameof(biome), biome.Length, count);
        CheckLength(nameof(water), water.Length, count);
        CheckLength(nameof(lakeId), lakeId.Length, count);
        CheckLength(nameof(downstream), downstream.Length, count);
        CheckLength(nameof(flow), flow.Length, count);
        CheckLength(nameof(temperatureDeciC), temperatureDeciC.Length, count);
        CheckLength(nameof(precipitationMm), precipitationMm.Length, count);
        for (var i = 1; i < count; i++)
            if (tileIds[i] <= tileIds[i - 1])
                throw new ArgumentException($"Tile ids must be strictly ascending; index {i} has {tileIds[i]} after {tileIds[i - 1]}.", nameof(tileIds));
        if (count > 0 && tileIds[0] < 0)
            throw new ArgumentException($"Tile ids must not be negative; index 0 has {tileIds[0]}.", nameof(tileIds));

        GlobeCell = globeCell;
        TileIds = tileIds;
        ElevationM = elevationM;
        Landform = landform;
        Biome = biome;
        Water = water;
        LakeId = lakeId;
        Downstream = downstream;
        Flow = flow;
        TemperatureDeciC = temperatureDeciC;
        PrecipitationMm = precipitationMm;
    }

    public int GlobeCell { get; }

    /// <summary>Ascending tile ids.</summary>
    public int[] TileIds { get; }

    public short[] ElevationM { get; }

    public byte[] Landform { get; }

    public byte[] Biome { get; }

    /// <summary>Byte value of <see cref="TileWaterKind"/>.</summary>
    public byte[] Water { get; }

    /// <summary>Lake id, −1 when the tile is in no lake.</summary>
    public int[] LakeId { get; }

    /// <summary>Downstream neighbour slot, −1 when none.</summary>
    public sbyte[] Downstream { get; }

    /// <summary>Flow accumulation, mm·km²/yr.</summary>
    public long[] Flow { get; }

    public short[] TemperatureDeciC { get; }

    public ushort[] PrecipitationMm { get; }

    public int Count => TileIds.Length;

    private static void CheckLength(string name, int actual, int expected)
    {
        if (actual != expected)
            throw new ArgumentException($"Column {name} has {actual} elements; the block has {expected} tiles.", name);
    }
}

/// <summary>Source of the tile blocks of a world, one globe cell at a time (database store, or in memory from a generation).</summary>
public interface ITileBlockSource
{
    /// <summary>The block of <paramref name="globeCell"/>, or null when no block is stored for that cell.</summary>
    Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct);
}
