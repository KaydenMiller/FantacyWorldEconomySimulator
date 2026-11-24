using System.Buffers.Binary;
using System.IO.Compression;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// Binary form of a per-cell <see cref="CrustRecord"/> plus the two present-day boundary layers, for checkpoints and the
/// world database (<c>geology</c> table). Layout: version byte; cell count (int32 little-endian); length of the
/// uncompressed payload (uint32); the Brotli-compressed payload: each record field as a little-endian column in
/// declaration order, then collision strength and boundary distance (uint16 columns).
/// </summary>
public static class CrustRecordCodec
{
    public const byte Version = 1;

    private const int Header = 9;

    public static byte[] Encode(CrustRecord r, ushort[] strength, ushort[] distance)
    {
        ArgumentNullException.ThrowIfNull(r);
        var n = r.Count;
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            for (var c = 0; c < n; c++) w.Write((byte)r.Kind[c]);
            Write(w, r.ThicknessDam, n);
            Write(w, r.FormedAgeMy, n);
            Write(w, r.LastOrogenyAgeMy, n);
            for (var c = 0; c < n; c++) w.Write((byte)r.Orogeny[c]);
            for (var c = 0; c < n; c++) w.Write(r.OrogenyCount[c]);
            Write(w, r.ArcMagmaAgeMy, n);
            for (var c = 0; c < n; c++) w.Write((byte)r.Flags[c]);
            Write(w, r.RiftAgeMy, n);
            Write(w, r.PassiveMarginAgeMy, n);
            Write(w, r.SedimentDam, n);
            Write(w, r.MaxBurialDam, n);
            Write(w, r.CoalExposureMy, n);
            Write(w, r.EvaporiteExposureMy, n);
            WriteOptional(w, strength, n);
            WriteOptional(w, distance, n);
        }

        var raw = ms.ToArray();
        var output = new byte[Header + BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        output[0] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(1), n);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(5), (uint)raw.Length);
        if (!BrotliEncoder.TryCompress(raw, output.AsSpan(Header), out var written, 5, 22))
            throw new InvalidOperationException("Brotli compression of the crust record failed.");
        return output.AsSpan(0, Header + written).ToArray();
    }

    /// <summary>Decodes a record of <paramref name="cellCount"/> cells; throws <see cref="FormatException"/> on any mismatch or corruption.</summary>
    public static (CrustRecord Crust, ushort[] Strength, ushort[] Distance) Decode(ReadOnlySpan<byte> data, int cellCount)
    {
        if (data.Length < Header || data[0] != Version)
            throw new FormatException($"crust record version {(data.Length > 0 ? data[0] : 0)} is not version {Version}");
        var n = BinaryPrimitives.ReadInt32LittleEndian(data[1..]);
        if (n != cellCount) throw new FormatException($"crust record has {n} cells; the grid has {cellCount} cells");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data[5..]);
        var raw = new byte[length];
        if (!BrotliDecoder.TryDecompress(data[Header..], raw, out var got) || got != length)
            throw new FormatException("crust record payload is truncated or corrupt");
        using var rd = new BinaryReader(new MemoryStream(raw));
        try
        {
            var r = new CrustRecord(n);
            for (var c = 0; c < n; c++) r.Allocate();
            for (var c = 0; c < n; c++) r.Kind[c] = (CrustKind)rd.ReadByte();
            Read(rd, r.ThicknessDam, n);
            Read(rd, r.FormedAgeMy, n);
            Read(rd, r.LastOrogenyAgeMy, n);
            for (var c = 0; c < n; c++) r.Orogeny[c] = (OrogenyKind)rd.ReadByte();
            for (var c = 0; c < n; c++) r.OrogenyCount[c] = rd.ReadByte();
            Read(rd, r.ArcMagmaAgeMy, n);
            for (var c = 0; c < n; c++) r.Flags[c] = (CrustFlags)rd.ReadByte();
            Read(rd, r.RiftAgeMy, n);
            Read(rd, r.PassiveMarginAgeMy, n);
            Read(rd, r.SedimentDam, n);
            Read(rd, r.MaxBurialDam, n);
            Read(rd, r.CoalExposureMy, n);
            Read(rd, r.EvaporiteExposureMy, n);
            var strength = ReadOptional(rd, n);
            var distance = ReadOptional(rd, n);
            return (r, strength, distance);
        }
        catch (EndOfStreamException ex)
        {
            throw new FormatException("crust record payload is truncated", ex);
        }
    }

    private static void Write(BinaryWriter w, ushort[] a, int n)
    {
        for (var c = 0; c < n; c++) w.Write(a[c]);
    }

    private static void Read(BinaryReader r, ushort[] a, int n)
    {
        for (var c = 0; c < n; c++) a[c] = r.ReadUInt16();
    }

    private static void WriteOptional(BinaryWriter w, ushort[] a, int n)
    {
        w.Write(a.Length == n);
        if (a.Length == n) Write(w, a, n);
    }

    private static ushort[] ReadOptional(BinaryReader r, int n)
    {
        if (!r.ReadBoolean()) return [];
        var a = new ushort[n];
        Read(r, a, n);
        return a;
    }
}
