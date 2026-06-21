using FluentAssertions;
using WorldEcon.Simulation.Random;

namespace WorldEcon.Simulation.Tests.Unit;

public class RngStreamsTests
{
    [Test]
    public void StreamsFromSameSeed_AreIndependentOfEachOther()
    {
        // Drawing from one stream must not affect another's sequence.
        var streamsA = new RngStreams(99UL);
        streamsA.For(RngStream.Pricing).NextULong(); // perturb pricing only
        var tradeAfterPerturb = streamsA.For(RngStream.Trade).NextULong();

        var streamsB = new RngStreams(99UL);
        var tradeFresh = streamsB.For(RngStream.Trade).NextULong();

        tradeAfterPerturb.Should().Be(tradeFresh);
    }

    [Test]
    public void DifferentStreams_ProduceDifferentSequences()
    {
        var streams = new RngStreams(99UL);
        var pricing = streams.For(RngStream.Pricing).NextULong();
        var trade = streams.For(RngStream.Trade).NextULong();
        pricing.Should().NotBe(trade);
    }

    [Test]
    public void SameSeed_ReproducesAllStreams()
    {
        var a = new RngStreams(7UL);
        var b = new RngStreams(7UL);
        foreach (var s in Enum.GetValues<RngStream>())
            a.For(s).NextULong().Should().Be(b.For(s).NextULong());
    }

    [Test]
    public void CaptureAndRestore_ResumesAllStreams()
    {
        var original = new RngStreams(55UL);
        original.For(RngStream.Production).NextULong(); // advance one stream
        var captured = original.Capture();

        var expected = original.For(RngStream.Production).NextULong();

        var restored = new RngStreams(55UL, captured);
        restored.For(RngStream.Production).NextULong().Should().Be(expected);
    }
}
