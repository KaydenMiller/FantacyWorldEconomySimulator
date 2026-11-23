using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

/// <summary>
/// Deterministic synthetic tile blocks. <see cref="Realistic"/> mimics generated data: 70 % of cells are ocean
/// (constant landform and biome, flow 0, near-flat elevation), land cells have a smooth elevation walk, landforms
/// derived from elevation, rivers with geometric flow values on 6 % of tiles, lakes in 1 cell of 25, and
/// temperature and precipitation that vary slowly. Linked into the Persistence test project.
/// </summary>
public static class SyntheticTileBlocks
{
    public static TileBlock Realistic(int cell, int count)
    {
        var rng = new Random(cell * 7919 + 13);
        var ocean = cell % 10 < 7;
        var hasLake = !ocean && cell % 25 == 3;
        var ids = new int[count];
        var elevation = new short[count];
        var landform = new byte[count];
        var biome = new byte[count];
        var water = new byte[count];
        var lakeId = new int[count];
        var downstream = new sbyte[count];
        var flow = new long[count];
        var temp = new short[count];
        var precip = new ushort[count];

        var id = cell * 600;
        double h = ocean ? -2500 - rng.Next(1500) : rng.Next(-50, 2500);
        var baseTemp = 300 - Math.Abs(cell % 181 - 90) * 4 + rng.Next(-30, 30);
        var basePrecip = 200 + rng.Next(2500);
        for (var i = 0; i < count; i++)
        {
            id += rng.Next(100) < 90 ? 1 : rng.Next(2, 40);
            ids[i] = id;
            h += ocean ? rng.Next(-2, 3) : rng.Next(-9, 10);
            elevation[i] = (short)h;
            temp[i] = (short)(baseTemp - (ocean ? 0 : Math.Max(0, (int)h) / 6) + rng.Next(-3, 4));
            precip[i] = (ushort)Math.Clamp(basePrecip + rng.Next(-60, 61) + i / 4, 0, ushort.MaxValue);
            lakeId[i] = -1;
            downstream[i] = -1;
            if (ocean)
            {
                landform[i] = 0; biome[i] = 0; water[i] = 4;
                continue;
            }

            landform[i] = h > 1800 ? (byte)3 : h > 700 ? (byte)4 : h < 5 ? (byte)8 : (byte)5;
            biome[i] = (byte)(temp[i] < 0 ? 3 : temp[i] < 150 ? 8 : 12 + (basePrecip > 1500 ? 1 : 0));
            downstream[i] = (sbyte)rng.Next(6);
            if (hasLake && i % 9 < 2)
            {
                water[i] = 2; lakeId[i] = 1000 + cell / 25; landform[i] = 1; biome[i] = 1; downstream[i] = -1;
            }
            else if (rng.Next(100) < 6)
            {
                water[i] = 1;
                flow[i] = 1L << Math.Min(40, (int)(-Math.Log2(rng.NextDouble() + 1e-9) * 3 + 8));
            }
        }

        return new TileBlock(cell, ids, elevation, landform, biome, water, lakeId, downstream, flow, temp, precip);
    }

    /// <summary>Random values over the full range of every field (ids still ascending).</summary>
    public static TileBlock Extremes(int cell, int count, int seed)
    {
        var rng = new Random(seed);
        var ids = new int[count];
        var next = rng.Next(0, 1000);
        for (var i = 0; i < count; i++)
        {
            ids[i] = next;
            next += rng.Next(1, 3_000_000);
            if (next < 0) throw new InvalidOperationException("Extremes: id overflow; use a smaller count.");
        }

        var elevation = new short[count];
        var landform = new byte[count];
        var biome = new byte[count];
        var water = new byte[count];
        var lakeId = new int[count];
        var downstream = new sbyte[count];
        var flow = new long[count];
        var temp = new short[count];
        var precip = new ushort[count];
        var buf = new byte[8];
        for (var i = 0; i < count; i++)
        {
            rng.NextBytes(buf);
            elevation[i] = (i % 5) switch { 0 => short.MinValue, 1 => short.MaxValue, _ => BitConverter.ToInt16(buf, 0) };
            landform[i] = buf[2];
            biome[i] = buf[3];
            water[i] = buf[4];
            lakeId[i] = (i % 6) switch { 0 => -1, 1 => int.MaxValue, 2 => int.MinValue, 3 => 0, _ => rng.Next(int.MinValue, int.MaxValue) };
            downstream[i] = (i % 4) switch { 0 => sbyte.MinValue, 1 => sbyte.MaxValue, 2 => -1, _ => (sbyte)buf[5] };
            flow[i] = (i % 4) switch { 0 => long.MaxValue, 1 => 0, 2 => 1, _ => rng.NextInt64(0, long.MaxValue) };
            temp[i] = (i % 3) switch { 0 => short.MinValue, 1 => short.MaxValue, _ => BitConverter.ToInt16(buf, 6) };
            precip[i] = (i % 3) switch { 0 => ushort.MinValue, 1 => ushort.MaxValue, _ => BitConverter.ToUInt16(buf, 0) };
        }

        return new TileBlock(cell, ids, elevation, landform, biome, water, lakeId, downstream, flow, temp, precip);
    }

    public static void AssertEqual(TileBlock expected, TileBlock actual)
    {
        if (expected.GlobeCell != actual.GlobeCell) throw new InvalidOperationException($"GlobeCell {expected.GlobeCell} != {actual.GlobeCell}");
        if (expected.Count != actual.Count) throw new InvalidOperationException($"Count {expected.Count} != {actual.Count}");
        Check(nameof(expected.TileIds), expected.TileIds, actual.TileIds);
        Check(nameof(expected.ElevationM), expected.ElevationM, actual.ElevationM);
        Check(nameof(expected.Landform), expected.Landform, actual.Landform);
        Check(nameof(expected.Biome), expected.Biome, actual.Biome);
        Check(nameof(expected.Water), expected.Water, actual.Water);
        Check(nameof(expected.LakeId), expected.LakeId, actual.LakeId);
        Check(nameof(expected.Downstream), expected.Downstream, actual.Downstream);
        Check(nameof(expected.Flow), expected.Flow, actual.Flow);
        Check(nameof(expected.TemperatureDeciC), expected.TemperatureDeciC, actual.TemperatureDeciC);
        Check(nameof(expected.PrecipitationMm), expected.PrecipitationMm, actual.PrecipitationMm);
    }

    private static void Check<T>(string name, T[] expected, T[] actual) where T : IEquatable<T>
    {
        for (var i = 0; i < expected.Length; i++)
            if (!expected[i].Equals(actual[i]))
                throw new InvalidOperationException($"{name}[{i}]: expected {expected[i]}, got {actual[i]}");
    }
}
