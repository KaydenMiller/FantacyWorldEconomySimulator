using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class FixedMathTests
{
    [Test]
    public void BpScale_Is10000() => FixedMath.BpScale.Should().Be(10_000);

    [Test]
    public void MulBp_AppliesPercentage()
        => FixedMath.MulBp(1000, 2500).Should().Be(250); // 25% of 1000

    [Test]
    public void MulBp_RoundsHalfToEven_Down()
        => FixedMath.MulBp(5, 5000).Should().Be(2); // 2.5 -> 2 (even)

    [Test]
    public void MulBp_RoundsHalfToEven_Up()
        => FixedMath.MulBp(3, 5000).Should().Be(2); // 1.5 -> 2 (even)

    [Test]
    public void MulDiv_UsesWideIntermediate_NoOverflow()
        => FixedMath.MulDiv(1_000_000_000L, 1_000_000_000L, 1_000_000_000L)
            .Should().Be(1_000_000_000L);

    [Test]
    public void DivFloor_RoundsTowardNegativeInfinity()
    {
        FixedMath.DivFloor(7, 2).Should().Be(3);
        FixedMath.DivFloor(-7, 2).Should().Be(-4);
    }

    [Test]
    public void DivRound_RoundsHalfToEven()
    {
        FixedMath.DivRound(5, 2).Should().Be(2);  // 2.5 -> 2
        FixedMath.DivRound(7, 2).Should().Be(4);  // 3.5 -> 4
        FixedMath.DivRound(8, 2).Should().Be(4);  // exact
    }

    [Test]
    public void FloorMod_IsAlwaysNonNegativeForPositiveModulus()
    {
        FixedMath.FloorMod(7, 3).Should().Be(1);
        FixedMath.FloorMod(-1, 3).Should().Be(2);
    }
}
