using FluentAssertions;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>
/// Checks carried over from the removed globe renderer tests (palette, label declutter, PNG output) and ported to
/// <see cref="MapRenderer"/>; plus the fill path on translated and scaled canvases (a window places the map control
/// with a translation; a 2× high-DPI window adds a scale).
/// </summary>
public class PaletteAndLabelTests
{
    private static readonly WorldGenSettings Settings8 = new() { Grid = new GridSettings { Frequency = 8, RegionMultiplier = 3, TileMultiplier = 4 } };
    private static readonly WorldGenSettings Settings32 = new() { Grid = new GridSettings { Frequency = 32, RegionMultiplier = 1, TileMultiplier = 2 } };
    private static readonly Lazy<GeneratedWorld> Gen8 = new(() => WorldGenerator.Generate(42, Settings8));
    private static readonly Lazy<GeneratedWorld> Gen32 = new(() => WorldGenerator.Generate(42, Settings32));

    private static WorldLayers World8 => Gen8.Value.Layers;

    private static readonly MapDrawOptions Plain = new()
    {
        Layer = GlobeLayer.Temperature, Rivers = false, Lakes = false, MountainSymbols = false, Settlements = false, Arrows = false, Equator = false, Borders = true,
    };

    // Sum of absolute RGB channel differences, 0–765.
    private static int Distance(uint a, uint b) =>
        Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF))
        + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF))
        + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));

    private const int MinDistinct = 90;

    // ---- labels ----

    [Test]
    public void A_label_overlapping_an_earlier_one_moves_and_one_with_no_free_slot_is_skipped()
    {
        var size = new SKSize(60, 14);
        var anchors = Enumerable.Repeat((Anchor: new SKPoint(100, 100), Size: size), 6).ToList();

        var placed = LabelLayout.Place(anchors, gapPx: 6);

        placed.Should().HaveCount(6);
        placed[0].Should().Be(new SKRect(106, 93, 166, 107), "the first label sits right of its anchor, centred vertically");
        var shown = placed.Where(p => p is not null).Select(p => p!.Value).ToList();
        shown.Should().HaveCountGreaterThan(1).And.HaveCountLessThan(6);
        for (var i = 0; i < shown.Count; i++)
            for (var j = i + 1; j < shown.Count; j++)
                shown[i].IntersectsWith(shown[j]).Should().BeFalse($"labels {i} and {j}");
        placed[^1].Should().BeNull("every slot around the shared anchor is taken");
    }

    [Test]
    [Arguments(MapLevel.Globe)]
    [Arguments(MapLevel.Region)]
    [Arguments(MapLevel.Detail)]
    public async Task Labels_of_neighbouring_settlements_do_not_overlap_at_each_level(MapLevel level)
    {
        var w = MapWorld.FromGenerated(Gen8.Value, Settings8, withImagery: false);
        using var r = new MapRenderer(w);
        var c = 300;
        var nb = World8.Grid.Neighbours(c).ToArray();
        var markers = new List<MapMarker>
        {
            new(c, -1, "Hammerfell"), new(nb[0], -1, "Greenfield"), new(nb[1], -1, "Riverwood"), new(nb[2], -1, "Karak Drum"),
        };
        var scale = level switch
        {
            MapLevel.Globe => 0.6 * 0.45 * 400,
            MapLevel.Region => w.ScaleForRegionPx(40),
            _ => w.ScaleForTilePx(24),
        };
        var view = w.View(World8.Geometry.Centre(c), scale, 500, 400);
        r.EffectiveLevel(view).Should().Be(level);
        await r.LoadVisibleAsync(view);
        using var bmp = new SKBitmap(500, 400);
        using var canvas = new SKCanvas(bmp);

        r.Draw(canvas, view, new MapDrawOptions { Markers = markers });

        if (SKTypeface.Default is null) return; // no fonts: labels are skipped
        var rects = r.LastLabelRects;
        rects.Should().NotBeEmpty();
        for (var i = 0; i < rects.Count; i++)
            for (var j = i + 1; j < rects.Count; j++)
                rects[i].IntersectsWith(rects[j]).Should().BeFalse($"labels {i} and {j}");
    }

    // ---- palette ----

    [Test]
    public void ColorOf_is_opaque_and_deterministic_for_every_layer()
    {
        foreach (var layer in Enum.GetValues<GlobeLayer>())
        {
            for (var c = 0; c < World8.Grid.CellCount; c += 11)
            {
                var a = LayerPalette.ColorOf(World8, layer, c);
                a.Should().Be(LayerPalette.ColorOf(World8, layer, c));
                (a >> 24).Should().Be(0xFFu);
            }
        }
    }

    [Test]
    public void Plate_colours_differ_between_plates()
    {
        var colours = Enumerable.Range(0, World8.Grid.CellCount)
            .GroupBy(c => World8.PlateOf[c])
            .Select(g => LayerPalette.ColorOf(World8, GlobeLayer.Plates, g.First()))
            .Distinct().Count();
        colours.Should().BeGreaterThan(1);
    }

    [Test]
    public void Hydrology_colours_of_river_lake_and_dry_lake_bed_differ_from_biome_colours_and_from_each_other()
    {
        var l = Gen32.Value.Layers;
        int[] Cells(WaterBodyKind k) => Enumerable.Range(0, l.Grid.CellCount).Where(c => l.WaterBodyKind[c] == (byte)k).ToArray();
        var river = Cells(WaterBodyKind.River);
        var lake = Cells(WaterBodyKind.Lake);
        var dry = Cells(WaterBodyKind.DryLakeBed);
        river.Should().NotBeEmpty();
        lake.Should().NotBeEmpty();
        dry.Should().NotBeEmpty();

        foreach (var c in river.Concat(lake).Concat(dry))
            Distance(LayerPalette.ColorOf(l, GlobeLayer.Hydrology, c), LayerPalette.ColorOf(l, GlobeLayer.Biome, c))
                .Should().BeGreaterThanOrEqualTo(MinDistinct, $"cell {c} ({(WaterBodyKind)l.WaterBodyKind[c]})");

        var lakeColour = LayerPalette.ColorOf(l, GlobeLayer.Hydrology, lake[0]);
        var dryColour = LayerPalette.ColorOf(l, GlobeLayer.Hydrology, dry[0]);
        Distance(lakeColour, dryColour).Should().BeGreaterThanOrEqualTo(MinDistinct);
        foreach (var c in river)
        {
            var rc = LayerPalette.ColorOf(l, GlobeLayer.Hydrology, c);
            Distance(rc, lakeColour).Should().BeGreaterThanOrEqualTo(MinDistinct, $"river cell {c} vs lake");
            Distance(rc, dryColour).Should().BeGreaterThanOrEqualTo(MinDistinct, $"river cell {c} vs dry lake bed");
        }

        foreach (var b in Enum.GetValues<Biome>())
        {
            var probe = Enumerable.Range(0, l.Grid.CellCount).FirstOrDefault(c => l.BiomeOf[c] == (byte)b && l.WaterBodyKind[c] == 0, -1);
            if (probe < 0) continue;
            var bc = LayerPalette.ColorOf(l, GlobeLayer.Biome, probe);
            Distance(bc, dryColour).Should().BeGreaterThanOrEqualTo(MinDistinct, $"biome {b} vs dry lake bed");
            Distance(bc, lakeColour).Should().BeGreaterThanOrEqualTo(MinDistinct / 2, $"biome {b} vs lake");
        }
    }

    // ---- PNG ----

    [Test]
    [Arguments(GlobeLayer.Biome)]
    [Arguments(GlobeLayer.Hydrology)]
    [Arguments(GlobeLayer.Areas)]
    [Arguments(GlobeLayer.Settlements)]
    [Arguments(GlobeLayer.Landform)]
    [Arguments(GlobeLayer.Winds)]
    public void Png_has_the_requested_size_background_corners_and_renders_every_layer_with_markers(GlobeLayer layer)
    {
        var w = MapWorld.FromGenerated(Gen8.Value, Settings8, withImagery: false);
        var markers = new List<MapMarker> { new(0, -1, "North"), new(5, -1, "Five") };

        using var bmp = SKBitmap.Decode(GlobePngRenderer.RenderMap(w, 300, 200, 10, 20, 1, new MapDrawOptions { Layer = layer, Markers = markers }));

        bmp.Width.Should().Be(300);
        bmp.Height.Should().Be(200);
        bmp.GetPixel(0, 0).Should().Be(MapRenderer.BackgroundColor);
        bmp.GetPixel(299, 199).Should().Be(MapRenderer.BackgroundColor);
    }

    [Test]
    public void Nearest_cell_to_the_view_direction_is_drawn_at_the_image_centre()
    {
        var w = MapWorld.FromGenerated(Gen8.Value, Settings8, withImagery: false);
        using var r = new MapRenderer(w);
        var view = w.ViewFromYawPitch(400, 400, 25, 15);
        var expected = GlobePngRenderer.NearestCell(World8, 25, 15);

        r.Pick(view, 200, 200)!.Value.Id.Should().Be(expected);
    }

    // ---- fill path on translated and scaled canvases ----

    [Test]
    public void Pixel_alignment_accepts_identity_and_integer_translations_only()
    {
        MapRenderer.IsPixelAligned(SKMatrix.Identity).Should().BeTrue();
        MapRenderer.IsPixelAligned(SKMatrix.CreateTranslation(12, 40)).Should().BeTrue();
        MapRenderer.IsPixelAligned(SKMatrix.CreateTranslation(12.5f, 40)).Should().BeFalse();
        MapRenderer.IsPixelAligned(SKMatrix.CreateScale(2, 2)).Should().BeFalse();
        MapRenderer.IsPixelAligned(SKMatrix.CreateScale(2, 2).PostConcat(SKMatrix.CreateTranslation(24, 80))).Should().BeFalse();
    }

    [Test]
    [Arguments(MapLevel.Globe)]
    [Arguments(MapLevel.Region)]
    [Arguments(MapLevel.Detail)]
    public async Task A_translated_canvas_shows_the_same_pixels_and_a_2x_canvas_shows_the_same_cells(MapLevel level)
    {
        var w = MapWorld.FromGenerated(Gen8.Value, Settings8, withImagery: false);
        using var r = new MapRenderer(w);
        var centre = World8.Geometry.Centre(300);
        var scale = level switch
        {
            MapLevel.Globe => 0.45 * 200,
            MapLevel.Region => w.ScaleForRegionPx(45),
            _ => w.ScaleForTilePx(26),
        };
        var view = w.View(centre, scale, 320, 200);
        r.EffectiveLevel(view).Should().Be(level);
        await r.LoadVisibleAsync(view);

        using var plain = new SKBitmap(320, 200);
        using (var c = new SKCanvas(plain)) r.Draw(c, view, Plain);

        // 1×, control placed at (30, 17) inside a window: same pixels, shifted.
        using var shifted = new SKBitmap(400, 260);
        using (var c = new SKCanvas(shifted))
        {
            c.Translate(30, 17);
            c.ClipRect(new SKRect(0, 0, 320, 200));
            r.Draw(c, view, Plain);
        }

        var differing = 0;
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 320; x++)
                if (plain.GetPixel(x, y) != shifted.GetPixel(x + 30, y + 17)) differing++;
        differing.Should().Be(0, "an integer translation keeps the span fill and its exact pixels");

        // 2× (high-DPI): the vertex path; the pixel at each sampled cell centre has the 1× colour there.
        using var doubled = new SKBitmap(640, 400);
        using (var c = new SKCanvas(doubled))
        {
            c.Scale(2);
            r.Draw(c, view, Plain);
        }

        var samples = 0;
        var same = 0;
        for (var y = 10; y < 200; y += 19)
            for (var x = 10; x < 320; x += 23)
            {
                if (r.Pick(view, x, y) is not { } pick) continue;
                var p = Centre(w, r, view, pick);
                if (p is not { } q || q.X < 2 || q.Y < 2 || q.X > 317 || q.Y > 197) continue;
                samples++;
                if (plain.GetPixel((int)q.X, (int)q.Y) == doubled.GetPixel((int)(q.X * 2), (int)(q.Y * 2))) same++;
            }

        samples.Should().BeGreaterThan(20);
        same.Should().BeGreaterThanOrEqualTo((int)(samples * 0.95), "a 2× canvas draws the same cells at twice the size");
    }

    /// <summary>Drawn centre of a picked item (globe cell, region cell or tile).</summary>
    private static (double X, double Y)? Centre(MapWorld w, MapRenderer r, MapViewState view, MapPick pick) => pick.Level switch
    {
        MapLevel.Globe => view.Project(w.Globe.Geometry.Centre(pick.Id)),
        MapLevel.Region => r.ProjectInGroup(view, w.Regions!.Geometry.Centre(pick.Id), pick.GlobeCell),
        _ => r.ProjectInGroup(view, w.Tiles!.Centre(pick.Id), pick.GlobeCell),
    };
}
