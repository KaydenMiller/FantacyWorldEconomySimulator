using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Pipeline.Checkpoints;

/// <summary>
/// Binary form of a <see cref="WorldGenCheckpoint"/> (<c>.wgckpt</c> files).
/// <para>
/// Layout: the magic bytes <c>WGCKPT</c>; one format-version byte; the length of the uncompressed payload (uint32,
/// little-endian); the Brotli-compressed payload. Payload, written with <see cref="BinaryWriter"/> (little-endian):
/// world seed, generator version, last stage, fingerprint; the stage count and per stage its id, code version, section
/// count, (section, hash) pairs and output hash; the settings JSON (JavaScript Object Notation) string; the layers; the
/// frame count and per frame its stage, index, count, optional label and layers. Layers: one named column per globe layer
/// (name, length, values), then the crust record (<see cref="Tectonics.CrustRecordCodec"/>) when present (format 2). The grid and
/// geometry are rebuilt from the settings' grid frequency on read.
/// </para>
/// </summary>
public static class CheckpointCodec
{
    /// <summary>Current format version; files with any other version are refused.</summary>
    public const byte FormatVersion = 2;

    /// <summary>The first bytes of every checkpoint.</summary>
    public static ReadOnlySpan<byte> Magic => "WGCKPT"u8;

    private const int Quality = 5;
    private const int Window = 22;
    private const int MaxPayloadBytes = 1024 * 1024 * 1024;

    public static byte[] Encode(WorldGenCheckpoint c)
    {
        ArgumentNullException.ThrowIfNull(c);
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(c.WorldSeed);
            w.Write(c.GeneratorVersion);
            w.Write((int)c.LastStage);
            w.Write(c.Fingerprint);
            w.Write(c.Stages.Count);
            foreach (var s in c.Stages)
            {
                w.Write((int)s.Print.Stage);
                w.Write(s.Print.CodeVersion);
                w.Write(s.Print.SectionHashes.Count);
                foreach (var kv in s.Print.SectionHashes.OrderBy(kv => kv.Key))
                {
                    w.Write((int)kv.Key);
                    w.Write(kv.Value);
                }

                w.Write(s.OutputHash);
            }

            w.Write(WorldGenSettingsJson.Serialize(c.Settings));
            WriteLayers(w, c.Layers);
            w.Write(c.Frames.Count);
            foreach (var f in c.Frames)
            {
                w.Write((int)f.Stage);
                w.Write(f.FrameIndex);
                w.Write(f.FrameCount);
                w.Write(f.Label is not null);
                if (f.Label is not null) w.Write(f.Label);
                WriteLayers(w, f.Layers);
            }
        }

        var raw = payload.ToArray();
        var bound = BrotliEncoder.GetMaxCompressedLength(raw.Length);
        var output = new byte[Magic.Length + 1 + 4 + bound];
        Magic.CopyTo(output);
        output[Magic.Length] = FormatVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(Magic.Length + 1), (uint)raw.Length);
        var header = Magic.Length + 1 + 4;
        if (!BrotliEncoder.TryCompress(raw, output.AsSpan(header), out var written, Quality, Window))
            throw new InvalidOperationException("Brotli compression of the checkpoint failed.");
        return output.AsSpan(0, header + written).ToArray();
    }

    public static WorldGenCheckpoint Decode(ReadOnlySpan<byte> data)
    {
        var header = Magic.Length + 1 + 4;
        if (data.Length < Magic.Length || !data[..Magic.Length].SequenceEqual(Magic))
            throw new CheckpointFormatException("not a checkpoint file (the magic bytes WGCKPT are missing).");
        if (data.Length < header)
            throw new CheckpointFormatException("the checkpoint is truncated (incomplete header).");
        var version = data[Magic.Length];
        if (version != FormatVersion)
            throw new CheckpointFormatException($"the checkpoint has format version {version}; this program reads format version {FormatVersion}.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(Magic.Length + 1, 4));
        if (length > MaxPayloadBytes)
            throw new CheckpointFormatException($"the checkpoint payload length {length} is larger than the {MaxPayloadBytes}-byte limit.");

        var raw = new byte[length];
        if (!BrotliDecoder.TryDecompress(data[header..], raw, out var decoded) || decoded != length)
            throw new CheckpointFormatException("the checkpoint is truncated or corrupt (the compressed payload does not decode to its stated length).");

        try
        {
            using var r = new BinaryReader(new MemoryStream(raw), Encoding.UTF8);
            var seed = r.ReadUInt64();
            var generatorVersion = r.ReadInt32();
            var lastStage = (WorldGenStage)r.ReadInt32();
            var fingerprint = r.ReadUInt64();
            var stageCount = Count(r);
            var stages = new List<CheckpointStage>(stageCount);
            for (var i = 0; i < stageCount; i++)
            {
                var stage = (WorldGenStage)r.ReadInt32();
                var codeVersion = r.ReadInt32();
                var sections = new Dictionary<SettingsSection, ulong>();
                var sectionCount = Count(r);
                for (var j = 0; j < sectionCount; j++)
                    sections[(SettingsSection)r.ReadInt32()] = r.ReadUInt64();
                stages.Add(new CheckpointStage(new StagePrint(stage, codeVersion, sections), r.ReadUInt64()));
            }

            var settings = WorldGenSettingsJson.Deserialize(r.ReadString());
            var grid = GlobeGrid.Build(settings.Grid.Frequency);
            var geometry = GlobeGeometry.Compute(grid);
            var layers = ReadLayers(r, grid, geometry);
            var frameCount = Count(r);
            var frames = new List<CheckpointFrame>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var stage = (WorldGenStage)r.ReadInt32();
                var index = r.ReadInt32();
                var count = r.ReadInt32();
                var label = r.ReadBoolean() ? r.ReadString() : null;
                frames.Add(new CheckpointFrame(stage, index, count, label, ReadLayers(r, grid, geometry)));
            }

            if (r.BaseStream.Position != r.BaseStream.Length)
                throw new CheckpointFormatException("the checkpoint is corrupt (unexpected data after the last frame).");
            return new WorldGenCheckpoint(seed, generatorVersion, lastStage, fingerprint, stages, settings, layers, frames);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            throw new CheckpointFormatException($"the checkpoint is truncated or corrupt ({ex.Message}).", ex);
        }
    }

    /// <summary>Writes to a temporary file beside <paramref name="path"/> and moves it over <paramref name="path"/>.</summary>
    public static void Write(string path, WorldGenCheckpoint c)
    {
        var bytes = Encode(c);
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Reads a checkpoint file; a <see cref="CheckpointFormatException"/> message starts with the path.</summary>
    public static WorldGenCheckpoint Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CheckpointFormatException($"{path}: cannot be read ({ex.Message}).", ex);
        }

        try
        {
            return Decode(bytes);
        }
        catch (CheckpointFormatException ex)
        {
            throw new CheckpointFormatException($"{path}: {ex.Message}", ex);
        }
    }

    private static int Count(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position + 1)
            throw new FormatException($"count {n} is out of range");
        return n;
    }

    private static void WriteLayers(BinaryWriter w, WorldLayers l)
    {
        Column(w, "PlateOf", l.PlateOf, w.Write);
        w.Write("Plates");
        w.Write(l.Plates.Length);
        foreach (var p in l.Plates)
        {
            w.Write(p.Id);
            w.Write(p.Continental);
            w.Write(p.RotationAxis.X);
            w.Write(p.RotationAxis.Y);
            w.Write(p.RotationAxis.Z);
            w.Write(p.AngularSpeed);
            w.Write(p.SeedCell);
        }

        Column(w, "ElevationM", l.ElevationM, w.Write);
        Column(w, "BoundaryKind", l.BoundaryKind, w.Write);
        w.Write("SeaLevelM");
        w.Write(l.SeaLevelM);
        Column(w, "CurrentDir", l.CurrentDir, w.Write);
        Column(w, "CurrentAnomalyDeciC", l.CurrentAnomalyDeciC, w.Write);
        Column(w, "WindDir", l.WindDir, w.Write);
        Column(w, "WindStrength", l.WindStrength, w.Write);
        Column(w, "TemperatureDeciC", l.TemperatureDeciC, w.Write);
        Column(w, "PrecipitationMm", l.PrecipitationMm, w.Write);
        w.Write("Crust");
        w.Write(l.Crust is not null);
        if (l.Crust is not null)
        {
            var bytes = Tectonics.CrustRecordCodec.Encode(l.Crust, l.CollisionStrengthPermille, l.BoundaryDistanceCells);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }

    private static WorldLayers ReadLayers(BinaryReader r, GlobeGrid grid, GlobeGeometry geometry)
    {
        var n = grid.CellCount;
        var l = new WorldLayers(grid, geometry) { PlateOf = ReadColumn(r, "PlateOf", n, r.ReadInt32) };
        Expect(r, "Plates");
        var plates = new PlateInfo[Count(r)];
        for (var i = 0; i < plates.Length; i++)
            plates[i] = new PlateInfo(r.ReadInt32(), r.ReadBoolean(), new Vector3d(r.ReadDouble(), r.ReadDouble(), r.ReadDouble()), r.ReadDouble(), r.ReadInt32());
        l.Plates = plates;
        l.ElevationM = ReadColumn(r, "ElevationM", n, r.ReadInt32);
        l.BoundaryKind = ReadColumn(r, "BoundaryKind", n, r.ReadByte);
        Expect(r, "SeaLevelM");
        l.SeaLevelM = r.ReadInt32();
        l.CurrentDir = ReadColumn(r, "CurrentDir", n, r.ReadSByte);
        l.CurrentAnomalyDeciC = ReadColumn(r, "CurrentAnomalyDeciC", n, r.ReadInt16);
        l.WindDir = ReadColumn(r, "WindDir", n, r.ReadSByte);
        l.WindStrength = ReadColumn(r, "WindStrength", n, r.ReadByte);
        l.TemperatureDeciC = ReadColumn(r, "TemperatureDeciC", n, r.ReadInt16);
        l.PrecipitationMm = ReadColumn(r, "PrecipitationMm", n, r.ReadInt32);
        Expect(r, "Crust");
        if (r.ReadBoolean())
        {
            var length = Count(r);
            (l.Crust, l.CollisionStrengthPermille, l.BoundaryDistanceCells) = Tectonics.CrustRecordCodec.Decode(r.ReadBytes(length), n);
        }

        return l;
    }

    private static void Column<T>(BinaryWriter w, string name, T[] values, Action<T> write)
    {
        w.Write(name);
        w.Write(values.Length);
        foreach (var v in values) write(v);
    }

    private static T[] ReadColumn<T>(BinaryReader r, string name, int cellCount, Func<T> read)
    {
        Expect(r, name);
        var length = r.ReadInt32();
        if (length != 0 && length != cellCount)
            throw new FormatException($"column {name} has {length} values; the grid has {cellCount} cells");
        var values = new T[length];
        for (var i = 0; i < length; i++) values[i] = read();
        return values;
    }

    private static void Expect(BinaryReader r, string name)
    {
        var actual = r.ReadString();
        if (actual != name) throw new FormatException($"expected column {name}, found '{actual}'");
    }
}
