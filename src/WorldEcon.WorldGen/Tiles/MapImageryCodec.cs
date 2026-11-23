using System.IO.Compression;
using System.Runtime.InteropServices;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Byte payloads for <see cref="CellImagery"/> and <see cref="RiverCourses"/> (table <c>map_imagery</c>): one format byte
/// ('I' imagery, 'R' rivers), one version byte (1), then a Brotli stream (quality 5) of the arrays in planar order,
/// little-endian, each preceded by the element count where the count is not implied. Decoding checks every length and
/// throws <see cref="InvalidDataException"/> on a truncated or foreign payload.
/// </summary>
public static class MapImageryCodec
{
    /// <summary>Format version written: 2 adds the river courses' FromWater flags. Version 1 payloads still decode.</summary>
    private const byte Version = 2;

    public static byte[] EncodeImagery(CellImagery img)
    {
        ArgumentNullException.ThrowIfNull(img);
        return Pack((byte)'I', w =>
        {
            w.Write(img.CellCount);
            Bytes(w, img.Biome);
            Bytes(w, img.Landform);
            Bytes(w, img.Water);
            Bytes(w, MemoryMarshal.AsBytes(img.ElevationM.AsSpan()));
            Bytes(w, MemoryMarshal.AsBytes(img.TemperatureDeciC.AsSpan()));
            Bytes(w, MemoryMarshal.AsBytes(img.PrecipitationMm.AsSpan()));
            Bytes(w, img.FlowLevel);
        });
    }

    /// <summary>Decodes imagery written for a grid of <paramref name="expectedCellCount"/> globe cells.</summary>
    public static CellImagery DecodeImagery(byte[] data, int expectedCellCount)
    {
        using var r = Unpack(data, (byte)'I');
        var cells = ReadInt(r);
        if (cells != expectedCellCount) throw new InvalidDataException($"Map imagery holds {cells} globe cells; the world has {expectedCellCount}.");
        var img = new CellImagery(cells);
        Read(r, img.Biome);
        Read(r, img.Landform);
        Read(r, img.Water);
        Read(r, MemoryMarshal.AsBytes(img.ElevationM.AsSpan()));
        Read(r, MemoryMarshal.AsBytes(img.TemperatureDeciC.AsSpan()));
        Read(r, MemoryMarshal.AsBytes(img.PrecipitationMm.AsSpan()));
        Read(r, img.FlowLevel);
        End(r);
        return img;
    }

    public static byte[] EncodeRiverCourses(RiverCourses c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Pack((byte)'R', w =>
        {
            w.Write(c.Count);
            Bytes(w, MemoryMarshal.AsBytes(c.From.AsSpan()));
            Bytes(w, MemoryMarshal.AsBytes(c.To.AsSpan()));
            Bytes(w, MemoryMarshal.AsBytes(c.Flow.AsSpan()));
            Bytes(w, MemoryMarshal.AsBytes(c.ToWater.AsSpan()));
            Bytes(w, MemoryMarshal.AsBytes(c.FromWater.AsSpan()));
        });
    }

    /// <summary>Decodes river courses holding at most <paramref name="maxEdges"/> edges (a world has at most one per tile).</summary>
    public static RiverCourses DecodeRiverCourses(byte[] data, int maxEdges)
    {
        using var r = Unpack(data, (byte)'R');
        var version = data[1];
        var n = ReadInt(r);
        if (n < 0 || n > maxEdges) throw new InvalidDataException($"River course count {n} is outside 0 … {maxEdges}.");
        var from = new int[n];
        var to = new int[n];
        var flow = new long[n];
        var waterBytes = new byte[n];
        Read(r, MemoryMarshal.AsBytes(from.AsSpan()));
        Read(r, MemoryMarshal.AsBytes(to.AsSpan()));
        Read(r, MemoryMarshal.AsBytes(flow.AsSpan()));
        Read(r, waterBytes);
        var fromBytes = new byte[n];
        if (version >= 2) Read(r, fromBytes);
        End(r);
        return new RiverCourses(from, to, flow, Flags(waterBytes, "water"), Flags(fromBytes, "from-water"));
    }

    private static bool[] Flags(byte[] bytes, string name)
    {
        var flags = new bool[bytes.Length];
        for (var k = 0; k < bytes.Length; k++)
        {
            if (bytes[k] > 1) throw new InvalidDataException($"River course {k} has {name} flag {bytes[k]} (expected 0 or 1).");
            flags[k] = bytes[k] == 1;
        }

        return flags;
    }

    private static byte[] Pack(byte format, Action<BinaryWriter> body)
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true)) body(w);
        using var output = new MemoryStream();
        output.WriteByte(format);
        output.WriteByte(Version);
        using (var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = 5 }, leaveOpen: true))
            raw.WriteTo(brotli);
        return output.ToArray();
    }

    /// <summary>A reader straight over the Brotli stream: reads stop at the arrays' lengths, so a payload that inflates past them is never fully decompressed.</summary>
    private static BinaryReader Unpack(byte[] data, byte format)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 2 || data[0] != format) throw new InvalidDataException($"Not a '{(char)format}' map imagery payload.");
        if (data[1] is < 1 or > Version) throw new InvalidDataException($"Map imagery format version {data[1]} is not supported (1 … {Version}).");
        return new BinaryReader(new BrotliStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress));
    }

    private static int ReadInt(BinaryReader r)
    {
        Span<byte> four = stackalloc byte[4];
        Read(r, four);
        return BitConverter.ToInt32(four);
    }

    private static void Bytes(BinaryWriter w, ReadOnlySpan<byte> s) => w.Write(s);

    private static void Read(BinaryReader r, Span<byte> into)
    {
        try
        {
            r.BaseStream.ReadExactly(into);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            throw new InvalidDataException("Map imagery payload is truncated or corrupt.", ex);
        }
    }

    private static void End(BinaryReader r)
    {
        int next;
        try
        {
            next = r.BaseStream.ReadByte();
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("Map imagery payload is corrupt.", ex);
        }

        if (next != -1) throw new InvalidDataException("Map imagery payload has trailing data.");
    }
}
