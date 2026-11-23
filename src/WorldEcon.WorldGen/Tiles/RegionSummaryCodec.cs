using System.Buffers.Binary;
using System.IO.Compression;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Binary form of a <see cref="RegionSummary"/> for the <c>region_level</c> table. The grid and geometry are not stored:
/// <see cref="Decode"/> takes them from the caller, who rebuilds them from the recorded settings (Frequency ×
/// RegionMultiplier), and checks the stored frequency and cell count against them.
/// <para>
/// Layout: byte 0 = format version; bytes 1–4 = length of the uncompressed payload (uint32, little-endian), which must
/// equal <see cref="PayloadBytes"/> of the grid; then the Brotli-compressed payload, which must decode to exactly that
/// length with no compressed bytes left over. Payload (little-endian, fixed width, written by
/// <see cref="BinaryWriter"/>): region frequency (int32), cell count n (int32), then one column per array in this order:
/// GlobeParent (int32 × n), TileCount (int32 × n), Kind (byte × n), Biome (byte × n), ElevationM (int16 × n),
/// TemperatureDeciC (int16 × n), PrecipitationMm (uint16 × n), MountainShareBp (uint16 × n), Flow (int64 × n),
/// Downstream (int32 × n), LakeId (int32 × n), InLargeLake (byte 0/1 × n; rule in TileSummaryStage). Fixed-width columns compress well under
/// Brotli because neighbouring cells have similar values; at the default preset (368,642 region cells) the raw payload is
/// 12.9 MB.
/// </para>
/// </summary>
public static class RegionSummaryCodec
{
    public const byte Version = 1;

    private const int Quality = 5;
    private const int Window = 22;
    private const int HeaderSize = 5;

    // Bytes per cell over all columns: 4 + 4 + 1 + 1 + 2 + 2 + 2 + 2 + 8 + 4 + 4 + 1.
    private const int BytesPerCell = 35;

    public static byte[] Encode(RegionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var n = summary.Count;
        var payload = new byte[PayloadBytes(n)];
        using (var w = new BinaryWriter(new MemoryStream(payload, writable: true)))
        {
            w.Write(summary.Grid.Frequency);
            w.Write(n);
            foreach (var v in summary.GlobeParent) w.Write(v);
            foreach (var v in summary.TileCount) w.Write(v);
            w.Write(summary.Kind);
            w.Write(summary.Biome);
            foreach (var v in summary.ElevationM) w.Write(v);
            foreach (var v in summary.TemperatureDeciC) w.Write(v);
            foreach (var v in summary.PrecipitationMm) w.Write(v);
            foreach (var v in summary.MountainShareBp) w.Write(v);
            foreach (var v in summary.Flow) w.Write(v);
            foreach (var v in summary.Downstream) w.Write(v);
            foreach (var v in summary.LakeId) w.Write(v);
            foreach (var v in summary.InLargeLake) w.Write(v);
        }

        var result = new byte[HeaderSize + BrotliEncoder.GetMaxCompressedLength(payload.Length)];
        result[0] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1), (uint)payload.Length);
        if (!BrotliEncoder.TryCompress(payload, result.AsSpan(HeaderSize), out var written, Quality, Window))
            throw new InvalidOperationException("Brotli compression failed.");
        return result.AsSpan(0, HeaderSize + written).ToArray();
    }

    /// <summary>
    /// Decodes a payload written by <see cref="Encode"/> onto <paramref name="grid"/> and <paramref name="geometry"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">Unknown version; frequency or cell count different from the grid; truncated, corrupt or trailing data; Kind, Biome, TileCount, LakeId (below −1) or Downstream (not −1 or a neighbour) out of range.</exception>
    public static RegionSummary Decode(byte[] data, GlobeGrid grid, GlobeGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(geometry);
        if (data.Length < HeaderSize)
            throw new InvalidDataException($"Region level is truncated: {data.Length} bytes, header needs {HeaderSize}.");
        if (data[0] != Version)
            throw new InvalidDataException($"Unsupported region level version {data[0]}; this build reads version {Version}.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
        if (length != PayloadBytes(grid.CellCount))
            throw new InvalidDataException(
                $"Region level declares {length} payload bytes; frequency {grid.Frequency} with {grid.CellCount} cells needs {PayloadBytes(grid.CellCount)}.");

        var payload = new byte[length];
        bool done;
        int consumed, produced;
        try
        {
            using var decoder = new BrotliDecoder();
            done = decoder.Decompress(data.AsSpan(HeaderSize), payload, out consumed, out produced) == System.Buffers.OperationStatus.Done;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Region level compressed data is corrupt.", e);
        }

        if (!done || produced != payload.Length || consumed != data.Length - HeaderSize)
            throw new InvalidDataException("Region level is truncated or corrupt: compressed data does not decode to the declared length.");

        using var r = new BinaryReader(new MemoryStream(payload, writable: false));
        var frequency = r.ReadInt32();
        var n = r.ReadInt32();
        if (frequency != grid.Frequency || n != grid.CellCount)
            throw new InvalidDataException(
                $"Region level holds frequency {frequency} with {n} cells; the settings give frequency {grid.Frequency} with {grid.CellCount} cells.");

        var globeParent = new int[n];
        for (var i = 0; i < n; i++) globeParent[i] = r.ReadInt32();
        var s = new RegionSummary(grid, geometry, globeParent);
        for (var i = 0; i < n; i++) s.TileCount[i] = r.ReadInt32();
        r.Read(s.Kind, 0, n);
        r.Read(s.Biome, 0, n);
        for (var i = 0; i < n; i++) s.ElevationM[i] = r.ReadInt16();
        for (var i = 0; i < n; i++) s.TemperatureDeciC[i] = r.ReadInt16();
        for (var i = 0; i < n; i++) s.PrecipitationMm[i] = r.ReadUInt16();
        for (var i = 0; i < n; i++) s.MountainShareBp[i] = r.ReadUInt16();
        for (var i = 0; i < n; i++) s.Flow[i] = r.ReadInt64();
        for (var i = 0; i < n; i++) s.Downstream[i] = r.ReadInt32();
        for (var i = 0; i < n; i++) s.LakeId[i] = r.ReadInt32();
        for (var i = 0; i < n; i++)
        {
            var b = r.ReadByte();
            if (b > 1) throw new InvalidDataException($"Region level InLargeLake of cell {i} is {b}; expected 0 or 1.");
            s.InLargeLake[i] = b == 1;
        }

        // Range checks, so a corrupt payload fails here and not as an index error in the renderer or the details panel.
        // LakeId is checked against the lakes table by the caller (GlobeStore.LoadAsync).
        var landforms = Enum.GetValues<Pipeline.Landform>().Length;
        var biomes = Enum.GetValues<Pipeline.Biome>().Length;
        for (var i = 0; i < n; i++)
        {
            if (s.Kind[i] >= landforms) throw new InvalidDataException($"Region level Kind of cell {i} is {s.Kind[i]}; expected below {landforms}.");
            if (s.Biome[i] >= biomes) throw new InvalidDataException($"Region level Biome of cell {i} is {s.Biome[i]}; expected below {biomes}.");
            if (s.TileCount[i] < 0) throw new InvalidDataException($"Region level TileCount of cell {i} is {s.TileCount[i]}.");
            if (s.LakeId[i] < -1) throw new InvalidDataException($"Region level LakeId of cell {i} is {s.LakeId[i]}.");
            var d = s.Downstream[i];
            if (d != -1 && (d < 0 || d >= n || !grid.Neighbours(i).Contains(d)))
                throw new InvalidDataException($"Region level Downstream of cell {i} is {d}; expected −1 or a neighbouring cell.");
        }

        // The declared length equals the column sizes for n cells, so the reader ends exactly at the payload end.
        return s;
    }

    /// <summary>Uncompressed payload size in bytes for <paramref name="cellCount"/> region cells.</summary>
    public static long PayloadBytes(int cellCount) => 8 + (long)cellCount * BytesPerCell;
}
