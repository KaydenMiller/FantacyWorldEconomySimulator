using System.IO.Compression;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// The water path of a voyage (a Sea route between two ports), kept as one compressed row instead of one row per tile:
/// tile ids delta-encoded as zig-zag varints, then Deflate. Key <see cref="RouteId"/>.
/// </summary>
public sealed class VoyagePath
{
    public RouteId RouteId { get; private set; }
    public byte[] Tiles { get; private set; } = [];

    private VoyagePath() { } // EF

    public VoyagePath(RouteId routeId, IReadOnlyList<int> tiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tiles.Count, 2);
        RouteId = routeId;
        Tiles = Encode(tiles);
    }

    public int[] Decode() => Decode(Tiles);

    public static byte[] Encode(IReadOnlyList<int> tiles)
    {
        using var raw = new MemoryStream();
        long prev = 0;
        foreach (int t in tiles)
        {
            long d = t - prev;
            prev = t;
            ulong z = (ulong)((d << 1) ^ (d >> 63));
            while (z >= 0x80) { raw.WriteByte((byte)(z | 0x80)); z >>= 7; }
            raw.WriteByte((byte)z);
        }
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(deflate);
        return packed.ToArray();
    }

    public static int[] Decode(byte[] data)
    {
        using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        deflate.CopyTo(raw);
        var bytes = raw.GetBuffer();
        int len = (int)raw.Length, i = 0;
        var tiles = new List<int>();
        long prev = 0;
        while (i < len)
        {
            ulong z = 0;
            int shift = 0;
            byte b;
            do { b = bytes[i++]; z |= (ulong)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
            long d = (long)(z >> 1) ^ -(long)(z & 1);
            prev += d;
            tiles.Add((int)prev);
        }
        return tiles.ToArray();
    }
}
