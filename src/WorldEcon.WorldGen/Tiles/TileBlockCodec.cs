using System.Buffers.Binary;
using System.IO.Compression;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Binary form of a <see cref="TileBlock"/> for the <c>tile_blocks</c> table. Lives in WorldGen so the database
/// store and any in-memory source share one implementation without referencing each other.
/// <para>
/// Layout: byte 0 = format version; bytes 1–4 = length of the uncompressed payload (uint32, little-endian);
/// then the Brotli-compressed payload. Payload: globe cell (zigzag varint), tile count (varint), tile ids
/// (varint deltas, first id from 0), elevation (int16 little-endian column), landform, biome, water (byte columns), lake ids
/// (zigzag varint of the difference to the previous tile's id, starting from −1), downstream (int8 column), flow
/// (unsigned varint, non-negative), temperature (int16 little-endian column), precipitation (uint16 little-endian column). Varints are
/// unsigned LEB128 (little-endian base 128).
/// </para>
/// </summary>
public static class TileBlockCodec
{
    public const byte Version = 1;

    /// <summary>Brotli quality chosen from the measurement in task-6a-report.md.</summary>
    public const int DefaultQuality = 5;

    private const int Window = 22;
    private const int HeaderSize = 5;
    private const int MaxPayloadBytes = 256 * 1024 * 1024;

    public static byte[] Encode(TileBlock block, int quality = DefaultQuality)
    {
        ArgumentNullException.ThrowIfNull(block);
        var n = block.Count;
        for (var i = 0; i < n; i++)
            if (block.Flow[i] < 0)
                throw new ArgumentException($"Flow must not be negative; tile {block.TileIds[i]} has {block.Flow[i]}.", nameof(block));

        // Worst case per tile: id 5, elevation 2, three byte columns 3, lake id 5, downstream 1, flow 10, temperature 2, precipitation 2.
        var payload = new byte[16 + n * 30];
        var pos = 0;
        WriteVarint(payload, ref pos, Zigzag(block.GlobeCell));
        WriteVarint(payload, ref pos, (ulong)n);

        var prev = 0;
        for (var i = 0; i < n; i++)
        {
            WriteVarint(payload, ref pos, (ulong)(uint)(block.TileIds[i] - prev));
            prev = block.TileIds[i];
        }

        for (var i = 0; i < n; i++, pos += 2)
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(pos), block.ElevationM[i]);
        block.Landform.AsSpan().CopyTo(payload.AsSpan(pos)); pos += n;
        block.Biome.AsSpan().CopyTo(payload.AsSpan(pos)); pos += n;
        block.Water.AsSpan().CopyTo(payload.AsSpan(pos)); pos += n;

        long prevLake = -1;
        for (var i = 0; i < n; i++)
        {
            WriteVarint(payload, ref pos, Zigzag(block.LakeId[i] - prevLake));
            prevLake = block.LakeId[i];
        }

        for (var i = 0; i < n; i++)
            payload[pos++] = (byte)block.Downstream[i];
        for (var i = 0; i < n; i++)
            WriteVarint(payload, ref pos, (ulong)block.Flow[i]);
        for (var i = 0; i < n; i++, pos += 2)
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(pos), block.TemperatureDeciC[i]);
        for (var i = 0; i < n; i++, pos += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(pos), block.PrecipitationMm[i]);

        var result = new byte[HeaderSize + BrotliEncoder.GetMaxCompressedLength(pos)];
        result[0] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1), (uint)pos);
        if (!BrotliEncoder.TryCompress(payload.AsSpan(0, pos), result.AsSpan(HeaderSize), out var written, quality, Window))
            throw new InvalidOperationException("Brotli compression failed.");
        return result.AsSpan(0, HeaderSize + written).ToArray();
    }

    /// <summary>
    /// Decodes a block whose row records <paramref name="expectedTileCount"/> tiles: a declared payload longer than the
    /// encoder's worst case for that count (16 + 30 bytes per tile) is rejected before the buffer is allocated, so a
    /// corrupt header cannot allocate up to 256 MiB (mebibytes).
    /// </summary>
    /// <exception cref="InvalidDataException">As <see cref="Decode(byte[])"/>, or a payload length too large for the tile count.</exception>
    public static TileBlock Decode(byte[] data, int expectedTileCount)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length >= HeaderSize && expectedTileCount >= 0)
        {
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
            var worst = 16 + ((long)expectedTileCount * 30);
            if (declared > worst)
                throw new InvalidDataException($"Tile block declares {declared} payload bytes; {expectedTileCount} tiles encode to at most {worst}.");
        }

        return Decode(data);
    }

    /// <exception cref="InvalidDataException">Unknown version, truncated or corrupt data.</exception>
    public static TileBlock Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize)
            throw new InvalidDataException($"Tile block is truncated: {data.Length} bytes, header needs {HeaderSize}.");
        if (data[0] != Version)
            throw new InvalidDataException($"Unsupported tile block version {data[0]}; this build reads version {Version}.");
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
        if (payloadLength > MaxPayloadBytes)
            throw new InvalidDataException($"Tile block declares an implausible payload of {payloadLength} bytes.");

        var payload = new byte[payloadLength];
        bool ok;
        int consumed, produced;
        try
        {
            using var decoder = new BrotliDecoder();
            var status = decoder.Decompress(data.AsSpan(HeaderSize), payload, out consumed, out produced);
            ok = status == System.Buffers.OperationStatus.Done;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Tile block compressed data is corrupt.", e);
        }

        if (!ok || produced != payloadLength || consumed != data.Length - HeaderSize)
            throw new InvalidDataException("Tile block is truncated or corrupt: compressed data does not decode to the declared length.");

        var r = new Reader(payload);
        var globeCell = checked((int)Unzigzag(r.ReadVarint()));
        var count = r.ReadVarint();
        // Each tile needs at least 13 bytes: one varint byte each for id, lake id and flow, plus 2+1+1+1+1+2+2 fixed.
        if (count > (ulong)payload.Length / 13)
            throw new InvalidDataException($"Tile block declares {count} tiles, more than its payload can hold.");
        var n = (int)count;

        var ids = new int[n];
        long prev = 0;
        for (var i = 0; i < n; i++)
        {
            var delta = r.ReadVarint();
            if (delta > int.MaxValue || (prev += (long)delta) > int.MaxValue)
                throw new InvalidDataException("Tile block id exceeds the int range.");
            ids[i] = (int)prev;
        }

        var elevation = new short[n];
        var span = r.Take(checked(n * 2));
        for (var i = 0; i < n; i++)
            elevation[i] = BinaryPrimitives.ReadInt16LittleEndian(span[(i * 2)..]);
        var landform = r.Take(n).ToArray();
        var biome = r.Take(n).ToArray();
        var water = r.Take(n).ToArray();

        var lakeId = new int[n];
        long prevLake = -1;
        for (var i = 0; i < n; i++)
        {
            var lakeDelta = Unzigzag(r.ReadVarint());
            if (lakeDelta is < -(1L << 33) or > 1L << 33 || (prevLake += lakeDelta) is < int.MinValue or > int.MaxValue)
                throw new InvalidDataException("Tile block lake id exceeds the int range.");
            lakeId[i] = (int)prevLake;
        }

        var downstream = new sbyte[n];
        var ds = r.Take(n);
        for (var i = 0; i < n; i++)
            downstream[i] = (sbyte)ds[i];
        var flow = new long[n];
        for (var i = 0; i < n; i++)
        {
            var f = r.ReadVarint();
            if (f > long.MaxValue)
                throw new InvalidDataException("Tile block flow exceeds the long range.");
            flow[i] = (long)f;
        }

        var temperature = new short[n];
        span = r.Take(checked(n * 2));
        for (var i = 0; i < n; i++)
            temperature[i] = BinaryPrimitives.ReadInt16LittleEndian(span[(i * 2)..]);
        var precipitation = new ushort[n];
        span = r.Take(checked(n * 2));
        for (var i = 0; i < n; i++)
            precipitation[i] = BinaryPrimitives.ReadUInt16LittleEndian(span[(i * 2)..]);

        if (!r.AtEnd)
            throw new InvalidDataException("Tile block payload has trailing bytes.");

        try
        {
            return new TileBlock(globeCell, ids, elevation, landform, biome, water, lakeId, downstream, flow, temperature, precipitation);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Tile block content is invalid: " + e.Message, e);
        }
    }

    private static ulong Zigzag(long v) => (ulong)((v << 1) ^ (v >> 63));

    private static long Unzigzag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    private static void WriteVarint(byte[] buf, ref int pos, ulong v)
    {
        while (v >= 0x80)
        {
            buf[pos++] = (byte)(v | 0x80);
            v >>= 7;
        }

        buf[pos++] = (byte)v;
    }

    private ref struct Reader(byte[] buf)
    {
        private readonly byte[] _buf = buf;
        private int _pos;

        public readonly bool AtEnd => _pos == _buf.Length;

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (_pos >= _buf.Length)
                    throw new InvalidDataException("Tile block is truncated inside a varint.");
                var b = _buf[_pos++];
                if (shift == 63 && b > 1)
                    throw new InvalidDataException("Tile block varint exceeds 64 bits.");
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
            }

            throw new InvalidDataException("Tile block varint is longer than 10 bytes.");
        }

        public ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > _buf.Length - _pos)
                throw new InvalidDataException("Tile block is truncated inside a column.");
            var s = new ReadOnlySpan<byte>(_buf, _pos, length);
            _pos += length;
            return s;
        }
    }
}
