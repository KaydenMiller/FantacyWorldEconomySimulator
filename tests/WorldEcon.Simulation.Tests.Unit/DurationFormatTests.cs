using FluentAssertions;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Simulation.Tests.Unit;

/// <summary>
/// Tests for <see cref="CalendarSystem.FormatDuration"/>.
/// All expected values use the default calendar: 60 min/h, 24 h/day, 7-day week, 30-day month, 12-month year.
/// minutesPerDay=1440, minutesPerWeek=10080, minutesPerMonth=43200, minutesPerYear=518400.
/// </summary>
public class DurationFormatTests
{
    private static readonly CalendarSystem Sut = new(CalendarDefinition.Default);

    [Test]
    public void Zero_Returns_0m()
        => Sut.FormatDuration(0).Should().Be("0m");

    [Test]
    public void Negative_Returns_0m()
        => Sut.FormatDuration(-500).Should().Be("0m");

    [Test]
    public void OneMinute_Returns_1m()
        => Sut.FormatDuration(1).Should().Be("1m");

    [Test]
    public void OneHour_Returns_1h()
        => Sut.FormatDuration(60).Should().Be("1h");

    [Test]
    public void TwelveHours_Returns_12h()
        => Sut.FormatDuration(720).Should().Be("12h");

    [Test]
    public void OneHour30Min_Returns_1h_30m()
        => Sut.FormatDuration(90).Should().Be("1h 30m");

    [Test]
    public void OneDay_Returns_1d()
        => Sut.FormatDuration(1440).Should().Be("1d");

    [Test]
    public void ThreeDays_Returns_3d()
        => Sut.FormatDuration(4320).Should().Be("3d");

    [Test]
    public void OneWeek_Returns_1w()
        => Sut.FormatDuration(10080).Should().Be("1w");

    [Test]
    public void OneDayAndOneHour_Returns_1d_1h()
        => Sut.FormatDuration(1440 + 60).Should().Be("1d 1h");

    [Test]
    public void TwoComponents_DropThird_EvenIfNonZero()
    {
        // 1d 1h 30m: only two largest non-zero units shown → "1d 1h"
        Sut.FormatDuration(1440 + 60 + 30).Should().Be("1d 1h");
    }

    [Test]
    public void OneMonth_Returns_1M()
        => Sut.FormatDuration(43200).Should().Be("1M");

    [Test]
    public void OneYear_Returns_1y()
        => Sut.FormatDuration(518400).Should().Be("1y");
}
