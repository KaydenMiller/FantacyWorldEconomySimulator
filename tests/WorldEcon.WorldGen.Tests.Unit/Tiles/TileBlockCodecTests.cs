using System.Diagnostics;
using FluentAssertions;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

public class TileBlockCodecTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(5)]
    [Arguments(576)]
    [Arguments(540)] // pentagon-sized: a pentagonal globe cell has 5/6 of a hexagon's tiles
    [Arguments(1000)]
    public void RoundTrip_ExtremeValues_EveryFieldEqual(int count)
    {
        for (var seed = 0; seed < 5; seed++)
        {
            var block = SyntheticTileBlocks.Extremes(cell: 40_961 - seed, count: Math.Min(count, 700), seed: seed);
            var decoded = TileBlockCodec.Decode(TileBlockCodec.Encode(block));
            SyntheticTileBlocks.AssertEqual(block, decoded);
        }
    }

    [Test]
    public void RoundTrip_RealisticBlocks_EveryFieldEqual()
    {
        for (var cell = 0; cell < 60; cell++)
        {
            var block = SyntheticTileBlocks.Realistic(cell, cell % 12 == 0 ? 480 : 576);
            SyntheticTileBlocks.AssertEqual(block, TileBlockCodec.Decode(TileBlockCodec.Encode(block)));
        }
    }

    [Test]
    public void RoundTrip_FieldBoundaries()
    {
        var block = new TileBlock(
            globeCell: int.MaxValue, tileIds: [0, int.MaxValue],
            elevationM: [short.MinValue, short.MaxValue], landform: [0, 255], biome: [255, 0], water: [0, 255],
            lakeId: [int.MinValue, int.MaxValue], downstream: [sbyte.MinValue, sbyte.MaxValue], flow: [long.MaxValue, 0],
            temperatureDeciC: [short.MinValue, short.MaxValue], precipitationMm: [ushort.MaxValue, 0]);
        SyntheticTileBlocks.AssertEqual(block, TileBlockCodec.Decode(TileBlockCodec.Encode(block)));
    }

    [Test]
    public void RoundTrip_EmptyBlock_KeepsGlobeCell()
    {
        var block = new TileBlock(17, [], [], [], [], [], [], [], [], [], []);
        var decoded = TileBlockCodec.Decode(TileBlockCodec.Encode(block));
        decoded.Count.Should().Be(0);
        decoded.GlobeCell.Should().Be(17);
    }

    [Test]
    public void Decode_WrongVersion_Throws()
    {
        var bytes = TileBlockCodec.Encode(SyntheticTileBlocks.Realistic(1, 50));
        bytes[0] = (byte)(TileBlockCodec.Version + 1);
        var act = () => TileBlockCodec.Decode(bytes);
        act.Should().Throw<InvalidDataException>().WithMessage("*version*");
    }

    [Test]
    public void Decode_Truncated_ThrowsAtEveryLength()
    {
        var bytes = TileBlockCodec.Encode(SyntheticTileBlocks.Realistic(1, 100));
        for (var length = 0; length < bytes.Length; length++)
        {
            var cut = bytes.AsSpan(0, length).ToArray();
            var act = () => TileBlockCodec.Decode(cut);
            act.Should().Throw<InvalidDataException>($"data cut to {length} of {bytes.Length} bytes");
        }
    }

    [Test]
    public void Decode_WithTileCount_RejectsADeclaredPayloadTooLargeForTheCount_BeforeAllocating()
    {
        var data = TileBlockCodec.Encode(SyntheticTileBlocks.Realistic(7, 10));
        TileBlockCodec.Decode(data, 10).Count.Should().Be(10);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), 200u * 1024 * 1024);
        var act = () => TileBlockCodec.Decode(data, 10);
        act.Should().Throw<InvalidDataException>().WithMessage("*10 tiles encode to at most 316*");
    }

    [Test]
    public void Decode_TrailingGarbage_Throws()
    {
        var bytes = TileBlockCodec.Encode(SyntheticTileBlocks.Realistic(1, 100));
        var act = () => TileBlockCodec.Decode([.. bytes, 0, 1, 2]);
        act.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void Decode_CorruptedBytes_ThrowsInvalidDataOnly()
    {
        var good = TileBlockCodec.Encode(SyntheticTileBlocks.Realistic(1, 100));
        var rng = new Random(3);
        for (var trial = 0; trial < 300; trial++)
        {
            var bytes = (byte[])good.Clone();
            bytes[rng.Next(1, bytes.Length)] ^= (byte)rng.Next(1, 256);
            try { TileBlockCodec.Decode(bytes); }
            catch (InvalidDataException) { }
        }
    }

    [Test]
    public void Encode_NegativeFlow_Throws()
    {
        var b = new TileBlock(0, [1], [0], [0], [0], [0], [-1], [-1], [-1], [0], [0]);
        var act = () => TileBlockCodec.Encode(b);
        act.Should().Throw<ArgumentException>().WithMessage("*Flow*");
    }

    [Test]
    public void TileBlock_Validation()
    {
        var act = () => new TileBlock(0, [1, 2], [0], [0, 0], [0, 0], [0, 0], [-1, -1], [-1, -1], [0, 0], [0, 0], [0, 0]);
        act.Should().Throw<ArgumentException>().WithMessage("*elevationM*");
        var act2 = () => new TileBlock(0, [2, 2], [0, 0], [0, 0], [0, 0], [0, 0], [-1, -1], [-1, -1], [0, 0], [0, 0], [0, 0]);
        act2.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    [Test]
    [Category("Slow")]
    public void Measure_BrotliQuality_SizeAndSpeed()
    {
        var blocks = Enumerable.Range(0, 400).Select(c => SyntheticTileBlocks.Realistic(c, 576)).ToArray();
        long raw = blocks.Sum(b => (long)b.Count * (4 + 2 + 1 + 1 + 1 + 4 + 1 + 8 + 2 + 2));
        foreach (var q in new[] { 1, 3, 4, 5, 6, 9, 11 })
        {
            TileBlockCodec.Encode(blocks[0], q);
            var sw = Stopwatch.StartNew();
            long size = 0;
            foreach (var b in blocks) size += TileBlockCodec.Encode(b, q).Length;
            var enc = sw.Elapsed;
            var encoded = blocks.Select(b => TileBlockCodec.Encode(b, q)).ToArray();
            sw.Restart();
            foreach (var e in encoded) TileBlockCodec.Decode(e);
            var dec = sw.Elapsed;
            Console.WriteLine($"quality {q,2}: {size / blocks.Length,6} B/block (raw {raw / blocks.Length}), encode {enc.TotalMilliseconds / blocks.Length:F3} ms/block, decode {dec.TotalMilliseconds / blocks.Length:F3} ms/block");
        }
    }
}
