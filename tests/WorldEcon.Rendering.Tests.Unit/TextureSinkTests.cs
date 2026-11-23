using System.Numerics;
using FluentAssertions;
using SkiaSharp;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>The textured triangle filler behind the continuous map imagery.</summary>
public class TextureSinkTests
{
    private static readonly uint Red = 0xFFFF0000, Green = 0xFF00FF00, Blue = 0xFF0000FF, White = 0xFFFFFFFF;

    private static SKBitmap Blank(int size)
    {
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bmp.Erase(SKColors.Black);
        return bmp;
    }

    /// <summary>A 40 × 40 square of two triangles mapped onto a 2 × 2 texture stored at offset 3 of a larger array.</summary>
    private static void Square(TextureSink sink, uint[] texels, float shade = 1, bool tint = false)
    {
        SKPoint p00 = new(0, 0), p10 = new(40, 0), p01 = new(0, 40), p11 = new(40, 40);
        Vector2 t00 = new(0, 0), t10 = new(2, 0), t01 = new(0, 2), t11 = new(2, 2);
        sink.Tri(p00, p10, p11, t00, t10, t11, texels, 3, 2, shade, tint);
        sink.Tri(p00, p11, p01, t00, t11, t01, texels, 3, 2, shade, tint);
    }

    [Test]
    public void Each_quadrant_shows_its_texel()
    {
        var sink = new TextureSink();
        uint[] texels = [0, 0, 0, Red, Green, Blue, White];
        Square(sink, texels);
        using var bmp = Blank(40);
        sink.Fill(bmp);
        bmp.GetPixel(10, 10).Should().Be(new SKColor(Red));
        bmp.GetPixel(30, 10).Should().Be(new SKColor(Green));
        bmp.GetPixel(10, 30).Should().Be(new SKColor(Blue));
        bmp.GetPixel(30, 30).Should().Be(new SKColor(White));
    }

    [Test]
    public void Shade_scales_and_tint_mixes_toward_gold()
    {
        uint[] texels = [0, 0, 0, White, White, White, White];
        var shaded = new TextureSink();
        Square(shaded, texels, shade: 0.5f);
        using var a = Blank(40);
        shaded.Fill(a);
        a.GetPixel(20, 20).Red.Should().BeInRange(126, 128);

        var tinted = new TextureSink();
        Square(tinted, [0, 0, 0, Blue, Blue, Blue, Blue], tint: true);
        using var b = Blank(40);
        tinted.Fill(b);
        b.GetPixel(20, 20).Should().Be(MapRenderer.HighlightTint(new SKColor(Blue)));
    }

    [Test]
    public void Texture_coordinates_outside_the_texture_clamp_to_its_edge()
    {
        var sink = new TextureSink();
        uint[] texels = [0, 0, 0, Red, Green, Blue, White];
        // The same square mapped to −1 … 3: the outer ring clamps to the edge texels.
        SKPoint p00 = new(0, 0), p10 = new(40, 0), p01 = new(0, 40), p11 = new(40, 40);
        Vector2 t00 = new(-1, -1), t10 = new(3, -1), t01 = new(-1, 3), t11 = new(3, 3);
        sink.Tri(p00, p10, p11, t00, t10, t11, texels, 3, 2, 1, false);
        sink.Tri(p00, p11, p01, t00, t11, t01, texels, 3, 2, 1, false);
        using var bmp = Blank(40);
        sink.Fill(bmp);
        bmp.GetPixel(1, 1).Should().Be(new SKColor(Red));
        bmp.GetPixel(38, 38).Should().Be(new SKColor(White));
    }
}
