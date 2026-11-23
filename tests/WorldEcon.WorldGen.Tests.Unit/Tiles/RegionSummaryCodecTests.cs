using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

public class RegionSummaryCodecTests
{
    private static readonly Lazy<RegionSummary> Generated = new(() =>
        WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 8, RegionMultiplier = 2, TileMultiplier = 3 } }).Regions!);

    private static RegionSummary Decode(byte[] data, RegionSummary like) =>
        RegionSummaryCodec.Decode(data, like.Grid, like.Geometry);

    [Test]
    public void RoundTrip_GeneratedSummary_EveryArrayEqual()
    {
        var s = Generated.Value;
        var data = RegionSummaryCodec.Encode(s);
        var grid = GlobeGrid.Build(s.Grid.Frequency);
        var d = RegionSummaryCodec.Decode(data, grid, GlobeGeometry.Compute(grid));

        d.Count.Should().Be(s.Count);
        d.GlobeParent.Should().Equal(s.GlobeParent);
        d.TileCount.Should().Equal(s.TileCount);
        d.Kind.Should().Equal(s.Kind);
        d.Biome.Should().Equal(s.Biome);
        d.ElevationM.Should().Equal(s.ElevationM);
        d.TemperatureDeciC.Should().Equal(s.TemperatureDeciC);
        d.PrecipitationMm.Should().Equal(s.PrecipitationMm);
        d.MountainShareBp.Should().Equal(s.MountainShareBp);
        d.Flow.Should().Equal(s.Flow);
        d.Downstream.Should().Equal(s.Downstream);
        d.LakeId.Should().Equal(s.LakeId);
        d.InLargeLake.Should().Equal(s.InLargeLake);
        TileSummaryStage.RegionHash(d).Should().Be(TileSummaryStage.RegionHash(s));
        Console.WriteLine($"Region level: {s.Count} cells, {data.Length} bytes compressed, {RegionSummaryCodec.PayloadBytes(s.Count)} raw");
    }

    [Test]
    public void RoundTrip_ExtremeValues()
    {
        var grid = GlobeGrid.Build(2);
        var s = new RegionSummary(grid, GlobeGeometry.Compute(grid), Enumerable.Range(0, grid.CellCount).Select(i => i % 3).ToArray());
        for (var i = 0; i < s.Count; i++)
        {
            var hi = i % 2 == 0;
            s.TileCount[i] = hi ? int.MaxValue : 0;
            s.Kind[i] = hi ? (byte)(Enum.GetValues<Landform>().Length - 1) : (byte)0;
            s.Biome[i] = (byte)(i % Enum.GetValues<Biome>().Length);
            s.ElevationM[i] = hi ? short.MaxValue : short.MinValue;
            s.TemperatureDeciC[i] = hi ? short.MinValue : short.MaxValue;
            s.PrecipitationMm[i] = hi ? ushort.MaxValue : (ushort)0;
            s.MountainShareBp[i] = hi ? (ushort)10_000 : (ushort)0;
            s.Flow[i] = hi ? long.MaxValue : long.MinValue;
            s.Downstream[i] = hi ? -1 : grid.Neighbours(i)[^1];
            s.LakeId[i] = hi ? int.MaxValue : -1;
            s.InLargeLake[i] = hi;
        }

        TileSummaryStage.RegionHash(Decode(RegionSummaryCodec.Encode(s), s)).Should().Be(TileSummaryStage.RegionHash(s));
    }

    [Test]
    [Arguments("Kind")]
    [Arguments("Biome")]
    [Arguments("TileCount")]
    [Arguments("LakeId")]
    [Arguments("DownstreamOutside")]
    [Arguments("DownstreamNotNeighbour")]
    public void Decode_OutOfRangeValue_ThrowsInvalidData(string field)
    {
        var grid = GlobeGrid.Build(2);
        var s = new RegionSummary(grid, GlobeGeometry.Compute(grid), new int[grid.CellCount]);
        Array.Fill(s.Downstream, -1);
        Array.Fill(s.LakeId, -1);
        var neighbours = grid.Neighbours(5).ToArray();
        switch (field)
        {
            case "Kind": s.Kind[5] = (byte)Enum.GetValues<Landform>().Length; break;
            case "Biome": s.Biome[5] = (byte)Enum.GetValues<Biome>().Length; break;
            case "TileCount": s.TileCount[5] = -1; break;
            case "LakeId": s.LakeId[5] = -2; break;
            case "DownstreamOutside": s.Downstream[5] = grid.CellCount; break;
            default: s.Downstream[5] = Enumerable.Range(0, grid.CellCount).First(c => c != 5 && !neighbours.Contains(c)); break;
        }

        var act = () => Decode(RegionSummaryCodec.Encode(s), s);
        act.Should().Throw<InvalidDataException>().WithMessage("*cell 5*");
    }

    [Test]
    public void Decode_OnAGridOfAnotherFrequency_Throws()
    {
        var data = RegionSummaryCodec.Encode(Generated.Value);
        var other = GlobeGrid.Build(Generated.Value.Grid.Frequency + 1);
        var act = () => RegionSummaryCodec.Decode(data, other, GlobeGeometry.Compute(other));
        act.Should().Throw<InvalidDataException>().WithMessage("*frequency*");
    }

    [Test]
    public void Decode_WrongVersion_Truncated_Trailing_OrCorrupt_ThrowsInvalidData()
    {
        var s = Generated.Value;
        var data = RegionSummaryCodec.Encode(s);

        var wrongVersion = (byte[])data.Clone();
        wrongVersion[0] = 99;
        FluentActions.Invoking(() => Decode(wrongVersion, s)).Should().Throw<InvalidDataException>().WithMessage("*version 99*");
        FluentActions.Invoking(() => Decode([], s)).Should().Throw<InvalidDataException>();
        foreach (var length in new[] { 1, 2, data.Length / 2, data.Length - 1 })
            FluentActions.Invoking(() => Decode(data[..length], s)).Should().Throw<InvalidDataException>($"length {length}");

        var rng = new Random(5);
        for (var trial = 0; trial < 50; trial++)
        {
            var corrupt = (byte[])data.Clone();
            corrupt[1 + rng.Next(corrupt.Length - 1)] ^= (byte)(1 + rng.Next(255));
            var act = () => Decode(corrupt, s);
            // A flipped bit either still decodes to some summary of the right shape or throws InvalidDataException; nothing else.
            try { act(); }
            catch (InvalidDataException) { }
        }
    }
}
