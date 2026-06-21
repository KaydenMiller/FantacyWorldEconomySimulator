using FluentAssertions;
using WorldEcon.Simulation.Random;

namespace WorldEcon.Simulation.Tests.Unit;

public class RngTests
{
    [Test]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var a = new Xoshiro256StarStar(42UL);
        var b = new Xoshiro256StarStar(42UL);
        var seqA = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var seqB = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        seqA.Should().Equal(seqB);
    }

    [Test]
    public void DifferentSeeds_DivergeQuickly()
    {
        var a = new Xoshiro256StarStar(1UL);
        var b = new Xoshiro256StarStar(2UL);
        a.NextULong().Should().NotBe(b.NextULong());
    }

    [Test]
    public void CaptureAndRestore_ResumesSameSequence()
    {
        var rng = new Xoshiro256StarStar(7UL);
        rng.NextULong(); // advance a bit
        var state = rng.Capture();
        var expected = Enumerable.Range(0, 8).Select(_ => rng.NextULong()).ToArray();

        var restored = new Xoshiro256StarStar(state);
        var actual = Enumerable.Range(0, 8).Select(_ => restored.NextULong()).ToArray();
        actual.Should().Equal(expected);
    }

    [Test]
    public void NextInt_StaysInRange()
    {
        var rng = new Xoshiro256StarStar(123UL);
        for (int i = 0; i < 10_000; i++)
            rng.NextInt(6).Should().BeInRange(0, 5);
    }

    [Test]
    public void NextInt_Throws_OnNonPositiveBound()
    {
        var rng = new Xoshiro256StarStar(1UL);
        var act = () => rng.NextInt(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void GoldenVector_Seed42_MatchesCanonicalReference()
    {
        // Pinned against the canonical public-domain xoshiro256**/SplitMix64
        // reference (Blackman & Vigna). DO NOT EDIT these values to match new
        // output — a mismatch means the algorithm changed and replay is broken.
        var rng = new Xoshiro256StarStar(42UL);
        var actual = Enumerable.Range(0, 8).Select(_ => rng.NextULong()).ToArray();
        actual.Should().Equal(
            0x15780B2E0C2EC716UL,
            0x6104D9866D113A7EUL,
            0xAE17533239E499A1UL,
            0xECB8AD4703B360A1UL,
            0xFDE6DC7FE2EC5E64UL,
            0xC50DA53101795238UL,
            0xB82154855A65DDB2UL,
            0xD99A2743EBE60087UL);
    }
}
