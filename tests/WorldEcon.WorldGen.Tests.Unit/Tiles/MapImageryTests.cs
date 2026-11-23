using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

/// <summary><see cref="CellImagery"/> and <see cref="RiverCourses"/> on a small generated world (16 × 3 × 4, seed 42).</summary>
public class MapImageryTests
{
    private static readonly WorldGenSettings Small = new()
    {
        Grid = new GridSettings { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 },
    };

    private static readonly Lazy<GeneratedWorld> World = new(() => WorldGenerator.Generate(42, Small));

    [Test]
    public void Every_texel_holds_the_fields_of_the_tile_nearest_its_centre()
    {
        var w = World.Value;
        var img = w.Imagery!;
        var t = w.Tiles!;
        img.CellCount.Should().Be(w.Layers.Grid.CellCount);
        img.Biome.Should().HaveCount(img.CellCount * CellImagery.TexelsPerCell);
        foreach (var cell in new[] { 0, 7, 300, img.CellCount - 1 })
        {
            var chart = CellChart.For(w.Layers.Grid, w.Layers.Geometry, cell);
            for (var j = 0; j < CellImagery.Size; j += 3)
                for (var i = 0; i < CellImagery.Size; i += 2)
                {
                    var tile = t.Grid.Nearest(chart.TexelCentre(i, j, CellImagery.Size));
                    var k = CellImagery.Index(cell, i, j);
                    img.Biome[k].Should().Be(t.Biome[tile]);
                    img.Landform[k].Should().Be(t.Landform[tile]);
                    img.Water[k].Should().Be(t.Ocean[tile] ? (byte)TileWaterKind.Ocean : t.WaterKind[tile]);
                    img.ElevationM[k].Should().Be(t.ElevationM[tile]);
                    img.TemperatureDeciC[k].Should().Be(t.TemperatureDeciC[tile]);
                    img.PrecipitationMm[k].Should().Be(t.PrecipitationMm[tile]);
                    img.FlowLevel[k].Should().Be(CellImagery.FlowToLevel(t.FlowAccum[tile]));
                }
        }
    }

    [Test]
    public void A_coastal_cell_mixes_water_and_land_texels()
    {
        var img = World.Value.Imagery!;
        var mixed = Enumerable.Range(0, img.CellCount).Count(c =>
        {
            var water = 0;
            for (var k = 0; k < CellImagery.TexelsPerCell; k++)
                if (img.Water[(c * CellImagery.TexelsPerCell) + k] == (byte)TileWaterKind.Ocean) water++;
            return water > 0 && water < CellImagery.TexelsPerCell;
        });
        mixed.Should().BeGreaterThan(0, "coast cells carry their land and water parts instead of one majority colour");
    }

    [Test]
    public void Flow_level_round_trips_within_one_sixteenth_of_a_decade()
    {
        foreach (var flow in new long[] { 0, 1, 999, 600_000, 50_000_000, 26_910_907, 9_000_000_000 })
        {
            var back = CellImagery.LevelToFlow(CellImagery.FlowToLevel(flow));
            Math.Abs(Math.Log10(back + 1) - Math.Log10(flow + 1)).Should().BeLessThanOrEqualTo(1.0 / 32 + 1e-9);
        }
    }

    [Test]
    public void River_courses_are_every_major_land_tile_edge()
    {
        var w = World.Value;
        var t = w.Tiles!;
        var c = w.RiverCourses!;
        var expected = new List<(int, int)>();
        static bool Water(TileLayers t, int x) => t.Ocean[x] || t.WaterKind[x] == (byte)TileWaterKind.Lake;
        for (var i = 0; i < t.Count; i++)
        {
            if (t.Ocean[i]) continue;
            var d = t.DownstreamOf(i);
            if (d < 0) continue;
            var isLake = t.WaterKind[i] == (byte)TileWaterKind.Lake;
            if ((isLake ? t.Lakes[t.LakeId[i]].Outflow : t.FlowAccum[i]) < Small.Tiles.MajorRiverFlow) continue;
            // Land tiles, and lake tiles draining onto land (a lake's outlet: the river starts at the shore).
            if (isLake && (Water(t, d) || t.Lakes[t.LakeId[i]].OutletTile != i)) continue;
            expected.Add((i, d));
        }

        expected.Should().NotBeEmpty();
        c.From.Zip(c.To).Should().Equal(expected);
        for (var k = 0; k < c.From.Length; k++)
        {
            // A lake outlet carries what leaves the lake after evaporation: the downstream tile's flow.
            c.Flow[k].Should().Be(c.FromWater[k] ? t.Lakes[t.LakeId[c.From[k]]].Outflow : t.FlowAccum[c.From[k]]);
            c.ToWater[k].Should().Be(t.Ocean[c.To[k]] || t.WaterKind[c.To[k]] == (byte)TileWaterKind.Lake);
            c.FromWater[k].Should().Be(t.WaterKind[c.From[k]] == (byte)TileWaterKind.Lake);
        }

        c.FromWater.Should().Contain(true, "some major rivers leave a lake");

        c.ToWater.Should().Contain(true, "some major rivers reach the sea or a lake");
    }
}

public class MapImageryCodecTests
{
    private static readonly Lazy<GeneratedWorld> World = new(() => WorldGenerator.Generate(7, new WorldGenSettings
    {
        Grid = new GridSettings { Frequency = 8, RegionMultiplier = 3, TileMultiplier = 4 },
    }));

    [Test]
    public void Imagery_round_trips()
    {
        var img = World.Value.Imagery!;
        var back = MapImageryCodec.DecodeImagery(MapImageryCodec.EncodeImagery(img), img.CellCount);
        back.Biome.Should().Equal(img.Biome);
        back.Landform.Should().Equal(img.Landform);
        back.Water.Should().Equal(img.Water);
        back.ElevationM.Should().Equal(img.ElevationM);
        back.TemperatureDeciC.Should().Equal(img.TemperatureDeciC);
        back.PrecipitationMm.Should().Equal(img.PrecipitationMm);
        back.FlowLevel.Should().Equal(img.FlowLevel);
    }

    [Test]
    public void Imagery_for_another_grid_is_rejected()
    {
        var img = World.Value.Imagery!;
        var act = () => MapImageryCodec.DecodeImagery(MapImageryCodec.EncodeImagery(img), img.CellCount + 1);
        act.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void River_courses_round_trip()
    {
        var c = World.Value.RiverCourses!;
        var back = MapImageryCodec.DecodeRiverCourses(MapImageryCodec.EncodeRiverCourses(c), maxEdges: c.Count);
        back.From.Should().Equal(c.From);
        back.To.Should().Equal(c.To);
        back.Flow.Should().Equal(c.Flow);
        back.ToWater.Should().Equal(c.ToWater);
        back.FromWater.Should().Equal(c.FromWater);
    }

    [Test]
    public void Version_1_river_courses_decode_with_no_lake_outlets()
    {
        // Version 1 payload: count, from, to, flow, toWater (no fromWater).
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(1);
            w.Write(5);
            w.Write(6);
            w.Write(100L);
            w.Write((byte)1);
        }

        using var output = new MemoryStream();
        output.WriteByte((byte)'R');
        output.WriteByte(1);
        using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(brotli);
        var back = MapImageryCodec.DecodeRiverCourses(output.ToArray(), maxEdges: 10);
        back.From.Should().Equal(5);
        back.ToWater.Should().Equal(true);
        back.FromWater.Should().Equal(false);
    }

    [Test]
    public void River_courses_beyond_the_tile_count_are_rejected()
    {
        var c = World.Value.RiverCourses!;
        var act = () => MapImageryCodec.DecodeRiverCourses(MapImageryCodec.EncodeRiverCourses(c), maxEdges: c.Count - 1);
        act.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void Non_boolean_water_flags_are_rejected()
    {
        // A one-edge payload whose ToWater byte is 2.
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(1);
            w.Write(5);
            w.Write(6);
            w.Write(100L);
            w.Write((byte)2);
        }

        using var output = new MemoryStream();
        output.WriteByte((byte)'R');
        output.WriteByte(1);
        using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(brotli);
        var act = () => MapImageryCodec.DecodeRiverCourses(output.ToArray(), maxEdges: 10);
        act.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void A_payload_that_inflates_past_its_arrays_is_rejected_without_reading_it_all()
    {
        var bytes = MapImageryCodec.EncodeRiverCourses(World.Value.RiverCourses!);
        // Trailing data after the arrays.
        var body = new MemoryStream();
        using (var b = new System.IO.Compression.BrotliStream(new MemoryStream(bytes, 2, bytes.Length - 2), System.IO.Compression.CompressionMode.Decompress)) b.CopyTo(body);
        body.Write(new byte[1_000_000]);
        using var output = new MemoryStream();
        output.WriteByte((byte)'R');
        output.WriteByte(1);
        using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) body.WriteTo(brotli);
        var act = () => MapImageryCodec.DecodeRiverCourses(output.ToArray(), maxEdges: 1_000_000);
        act.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void Truncated_payloads_are_rejected()
    {
        var bytes = MapImageryCodec.EncodeRiverCourses(World.Value.RiverCourses!);
        var act = () => MapImageryCodec.DecodeRiverCourses(bytes[..(bytes.Length / 2)], maxEdges: 1_000_000);
        act.Should().Throw<InvalidDataException>();
    }
}
