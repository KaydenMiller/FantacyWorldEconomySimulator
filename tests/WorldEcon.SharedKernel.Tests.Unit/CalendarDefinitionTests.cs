using FluentAssertions;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class CalendarDefinitionTests
{
    [Test]
    public void Default_Has12MonthsOf30Days()
    {
        var def = CalendarDefinition.Default;
        def.Months.Should().HaveCount(12);
        def.Months.Should().OnlyContain(m => m.Days == 30);
    }

    [Test]
    public void Default_Has7Weekdays_24Hours_60Minutes()
    {
        var def = CalendarDefinition.Default;
        def.Weekdays.Should().HaveCount(7);
        def.HoursPerDay.Should().Be(24);
        def.MinutesPerHour.Should().Be(60);
    }

    [Test]
    public void Default_EpochIsYear1Month1Day1()
        => CalendarDefinition.Default.Epoch.Should().Be(new CalendarDate(1, 1, 1, 0, 0));

    [Test]
    public void Default_HasNoLeapRule()
        => CalendarDefinition.Default.LeapRule.Should().Be(LeapRule.None);

    [Test]
    public void Default_HasFourSeasonsCoveringAllMonths()
        => CalendarDefinition.Default.Seasons.Should().HaveCount(4);
}
