using FluentAssertions;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Simulation.Tests.Unit;

public class CalendarSystemTests
{
    private static readonly CalendarSystem Sut = new(CalendarDefinition.Default);

    [Test]
    public void Epoch_MapsToTickZero()
        => Sut.ToTick(new CalendarDate(1, 1, 1, 0, 0)).Should().Be(Tick.Zero);

    [Test]
    public void ToDate_TickZero_IsEpoch()
        => Sut.ToDate(Tick.Zero).Should().Be(new CalendarDate(1, 1, 1, 0, 0));

    [Test]
    public void OneDay_Is1440Ticks_AndAdvancesTheDay()
        => Sut.ToDate(new Tick(1_440)).Should().Be(new CalendarDate(1, 1, 2, 0, 0));

    [Test]
    public void MonthRollsOverAfter30Days()
        => Sut.ToDate(new Tick(30 * 1_440)).Should().Be(new CalendarDate(1, 2, 1, 0, 0));

    [Test]
    public void YearRollsOverAfter360Days()
        => Sut.ToDate(new Tick(360 * 1_440)).Should().Be(new CalendarDate(2, 1, 1, 0, 0));

    [Test]
    public void IntradayMinutesAndHours()
        => Sut.ToDate(new Tick(90)).Should().Be(new CalendarDate(1, 1, 1, 1, 30)); // 90 min = 01:30

    [Test]
    public void RoundTrip_HoldsAcrossMultiYearRange()
    {
        for (long t = -360L * 1_440 * 3; t < 360L * 1_440 * 3; t += 777) // ~±3 years, irregular stride
        {
            var tick = new Tick(t);
            Sut.ToTick(Sut.ToDate(tick)).Should().Be(tick);
        }
    }

    [Test]
    public void Weekday_CyclesEverySevenDays()
    {
        Sut.WeekdayIndex(Tick.Zero).Should().Be(0);
        Sut.WeekdayIndex(new Tick(1_440)).Should().Be(1);
        Sut.WeekdayIndex(new Tick(7 * 1_440)).Should().Be(0);
    }

    [Test]
    public void SeasonAt_MapsMonthToSeason()
    {
        Sut.SeasonAt(Sut.ToTick(new CalendarDate(1, 1, 15, 0, 0))).Name.Should().Be("Spring");
        Sut.SeasonAt(Sut.ToTick(new CalendarDate(1, 5, 15, 0, 0))).Name.Should().Be("Summer");
        Sut.SeasonAt(Sut.ToTick(new CalendarDate(1, 11, 15, 0, 0))).Name.Should().Be("Winter");
    }

    [Test]
    public void ToDate_NegativeOneTick_IsLastMinuteOfPreEpochDay()
        => Sut.ToDate(new Tick(-1)).Should().Be(new CalendarDate(0, 12, 30, 23, 59));

    [Test]
    public void Weekday_HandlesNegativeTicks()
        => Sut.WeekdayIndex(new Tick(-1_440)).Should().Be(6);

    [Test]
    public void ToDate_LastMinuteOfDay_Is2359()
        => Sut.ToDate(new Tick(1_439)).Should().Be(new CalendarDate(1, 1, 1, 23, 59));

    [Test]
    public void ToDate_DayBoundary_RollsToNextDayMidnight()
        => Sut.ToDate(new Tick(1_440)).Should().Be(new CalendarDate(1, 1, 2, 0, 0));

    [Test]
    public void SeasonAt_HandlesWrapAroundSeason()
    {
        var def = CalendarDefinition.Default with
        {
            Seasons = new[]
            {
                new SeasonDef("Cold", 12, 1, 2, 30),   // wraps year boundary
                new SeasonDef("Warm", 3, 1, 11, 30),
            },
        };
        var sut = new CalendarSystem(def);
        sut.SeasonAt(sut.ToTick(new CalendarDate(1, 1, 15, 0, 0))).Name.Should().Be("Cold");
        sut.SeasonAt(sut.ToTick(new CalendarDate(1, 12, 15, 0, 0))).Name.Should().Be("Cold");
        sut.SeasonAt(sut.ToTick(new CalendarDate(1, 6, 15, 0, 0))).Name.Should().Be("Warm");
    }
}
