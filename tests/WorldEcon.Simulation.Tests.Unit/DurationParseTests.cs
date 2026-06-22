using FluentAssertions;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Simulation.Tests.Unit;

/// <summary>
/// Tests for <see cref="CalendarSystem.TryParseDurationToTicks"/>.
/// All expected values use the default calendar: 60 min/h, 24 h/day, 7-day week, 30-day month, 12-month year.
/// minutesPerDay = 1440, minutesPerWeek = 10080, minutesPerMonth = 43200, minutesPerYear = 518400.
/// </summary>
public class DurationParseTests
{
    private static readonly CalendarSystem Sut = new(CalendarDefinition.Default);

    // ---- unit suffix conversions (default calendar) ----

    [Test]
    public void OneMinute_Returns1()
    {
        Sut.TryParseDurationToTicks("1m", out var t).Should().BeTrue();
        t.Should().Be(1);
    }

    [Test]
    public void OneHour_Returns60()
    {
        Sut.TryParseDurationToTicks("1h", out var t).Should().BeTrue();
        t.Should().Be(60);
    }

    [Test]
    public void OneDay_Returns1440()
    {
        Sut.TryParseDurationToTicks("1d", out var t).Should().BeTrue();
        t.Should().Be(1_440);
    }

    [Test]
    public void OneWeek_Returns10080()
    {
        Sut.TryParseDurationToTicks("1w", out var t).Should().BeTrue();
        t.Should().Be(10_080);
    }

    [Test]
    public void OneMonth_Returns43200()
    {
        Sut.TryParseDurationToTicks("1M", out var t).Should().BeTrue();
        t.Should().Be(43_200);
    }

    [Test]
    public void OneYear_Returns518400()
    {
        Sut.TryParseDurationToTicks("1y", out var t).Should().BeTrue();
        t.Should().Be(518_400);
    }

    // ---- bare integer = raw ticks ----

    [Test]
    public void BareInteger_90_Returns90()
    {
        Sut.TryParseDurationToTicks("90", out var t).Should().BeTrue();
        t.Should().Be(90);
    }

    [Test]
    public void BareInteger_WithLeadingPlus_Returns1440()
    {
        // long.TryParse handles + prefix
        Sut.TryParseDurationToTicks("+1440", out var t).Should().BeTrue();
        t.Should().Be(1_440);
    }

    // ---- multi-unit scaling ----

    [Test]
    public void TwoDays_Returns2880()
    {
        Sut.TryParseDurationToTicks("2d", out var t).Should().BeTrue();
        t.Should().Be(2_880);
    }

    [Test]
    public void ThreeWeeks_Returns30240()
    {
        Sut.TryParseDurationToTicks("3w", out var t).Should().BeTrue();
        t.Should().Be(30_240);
    }

    // ---- case sensitivity: m != M ----

    [Test]
    public void LowercaseM_IsMinute_Not_Month()
    {
        Sut.TryParseDurationToTicks("1m", out var m).Should().BeTrue();
        Sut.TryParseDurationToTicks("1M", out var bigM).Should().BeTrue();
        m.Should().Be(1);
        bigM.Should().Be(43_200);
        m.Should().NotBe(bigM);
    }

    // ---- invalid inputs → false ----

    [Test]
    public void UnknownUnit_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("1x", out _).Should().BeFalse();
    }

    [Test]
    public void GarbageString_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("abc", out _).Should().BeFalse();
    }

    [Test]
    public void MultipleUnits_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("1d2h", out _).Should().BeFalse();
    }

    [Test]
    public void EmptyString_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("", out _).Should().BeFalse();
    }

    [Test]
    public void WhitespaceOnly_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("   ", out _).Should().BeFalse();
    }

    [Test]
    public void NullInput_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks(null, out _).Should().BeFalse();
    }

    [Test]
    public void ZeroTicks_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("0", out _).Should().BeFalse();
    }

    [Test]
    public void ZeroWithUnit_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("0d", out _).Should().BeFalse();
    }

    [Test]
    public void NegativeInteger_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("-5", out _).Should().BeFalse();
    }

    [Test]
    public void NegativeWithUnit_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("-1d", out _).Should().BeFalse();
    }

    [Test]
    public void UppercaseD_ReturnsFalse()
    {
        // Only lowercase 'd' is days; 'D' is not a defined unit.
        Sut.TryParseDurationToTicks("1D", out _).Should().BeFalse();
    }

    [Test]
    public void UppercaseH_ReturnsFalse()
    {
        Sut.TryParseDurationToTicks("1H", out _).Should().BeFalse();
    }

    // ---- whitespace trimming ----

    [Test]
    public void WithSurroundingWhitespace_ParsesCorrectly()
    {
        Sut.TryParseDurationToTicks("  1d  ", out var t).Should().BeTrue();
        t.Should().Be(1_440);
    }
}
