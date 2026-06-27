using FluentAssertions;
using WorldEcon.Simulation.Random;

namespace WorldEcon.Simulation.Tests.Unit;

public class DeterministicHashTests
{
    [Test]
    public void RangeInclusive_IsStable_ForSameInputs()
    {
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var first = DeterministicHash.RangeInclusive(42UL, a, b, 1000, 80, 120);
        var again = DeterministicHash.RangeInclusive(42UL, a, b, 1000, 80, 120);
        again.Should().Be(first);
    }

    [Test]
    public void RangeInclusive_StaysWithinBounds()
    {
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        for (long tick = 0; tick < 500; tick++)
        {
            var v = DeterministicHash.RangeInclusive(7UL, a, b, tick, 80, 120);
            v.Should().BeGreaterThanOrEqualTo(80).And.BeLessThanOrEqualTo(120);
        }
    }

    [Test]
    public void RangeInclusive_VariesAcrossTicks()
    {
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var distinct = new HashSet<long>();
        for (long tick = 0; tick < 100; tick++)
            distinct.Add(DeterministicHash.RangeInclusive(7UL, a, b, tick, 1, 1000));
        distinct.Count.Should().BeGreaterThan(50); // a wide band should produce many distinct draws
    }

    [Test]
    public void RangeInclusive_DegenerateBand_ReturnsTheBound()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        DeterministicHash.RangeInclusive(7UL, a, b, 1, 200, 200).Should().Be(200);
    }
}
