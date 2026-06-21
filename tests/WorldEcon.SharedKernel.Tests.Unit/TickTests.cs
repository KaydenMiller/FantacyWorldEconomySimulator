using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class TickTests
{
    [Test]
    public void Zero_IsZero() => Tick.Zero.Value.Should().Be(0);

    [Test]
    public void DefaultConstants_MatchStandardCalendar()
    {
        Tick.MinutesPerHour.Should().Be(60);
        Tick.DefaultMinutesPerDay.Should().Be(1_440);
        Tick.DefaultMinutesPerWeek.Should().Be(10_080);
    }

    [Test]
    public void AddMinutes_AdvancesValue()
        => new Tick(100).AddMinutes(10).Value.Should().Be(110);

    [Test]
    public void Comparisons_OrderByValue()
    {
        (new Tick(5) < new Tick(6)).Should().BeTrue();
        (new Tick(6) > new Tick(5)).Should().BeTrue();
        (new Tick(5) <= new Tick(5)).Should().BeTrue();
        (new Tick(5) >= new Tick(5)).Should().BeTrue();
        new Tick(5).CompareTo(new Tick(7)).Should().BeNegative();
    }
}
