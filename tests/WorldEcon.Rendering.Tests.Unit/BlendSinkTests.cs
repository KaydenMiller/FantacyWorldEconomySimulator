using FluentAssertions;
using SkiaSharp;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>The tile-centre triangle filler that draws Detail View without hex edges.</summary>
public class BlendSinkTests
{
    private static readonly uint Sea = 0xFF1F4E8C, Forest = 0xFF2F7A34;

    private static SKBitmap Draw(Action<BlendSink> add)
    {
        var sink = new BlendSink();
        add(sink);
        var bmp = new SKBitmap(new SKImageInfo(60, 60, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bmp.Erase(SKColors.Black);
        sink.Fill(bmp);
        return bmp;
    }

    [Test]
    public void The_kind_holding_most_weight_wins_so_boundaries_are_straight_lines_not_hex_steps()
    {
        // Land at A (0, 0); sea at B (60, 0) and C (0, 60). Sea holds wb + wc = (x + y) / 60, land 1 − that.
        using var bmp = Draw(s => s.Tri(new(0, 0), new(60, 0), new(0, 60), Forest, 1, Sea, 2, Sea, 2, continuous: false, shade: 1, tint: false));
        bmp.GetPixel(10, 10).Should().Be(new SKColor(Forest), "x + y = 21 < 30: land holds more weight");
        bmp.GetPixel(20, 18).Should().Be(new SKColor(Sea), "x + y = 39 > 30: sea holds more weight");
        bmp.GetPixel(14, 14).Should().Be(new SKColor(Forest), "x + y = 29: still land");
        bmp.GetPixel(16, 15).Should().Be(new SKColor(Sea), "x + y = 32: sea");
    }

    [Test]
    public void Continuous_layers_blend_colours_within_the_winning_kind()
    {
        // Three land vertices (same key) with black, red and green: the centroid is their average.
        using var bmp = Draw(s => s.Tri(new(0, 0), new(60, 0), new(0, 60), 0xFF000000, 1, 0xFFFF0000, 1, 0xFF00FF00, 1, continuous: true, shade: 1, tint: false));
        var c = bmp.GetPixel(19, 19);
        c.Red.Should().BeInRange(80, 90);
        c.Green.Should().BeInRange(80, 90);
        c.Blue.Should().Be(0);
    }

    [Test]
    public void A_vertex_of_another_kind_does_not_tint_a_continuous_blend()
    {
        // Sea vertex C is a different key: inside the land region, only A and B blend.
        using var bmp = Draw(s => s.Tri(new(0, 0), new(60, 0), new(0, 60), 0xFF000000, 1, 0xFFFF0000, 1, Sea, 2, continuous: true, shade: 1, tint: false));
        var c = bmp.GetPixel(30, 5);
        c.Blue.Should().Be(0, "no sea blue mixes into land");
        c.Red.Should().BeGreaterThan(100);
    }
}
