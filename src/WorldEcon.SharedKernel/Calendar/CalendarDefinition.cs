namespace WorldEcon.SharedKernel.Calendar;

public sealed record MonthDef(string Name, int Days);

public sealed record SeasonDef(string Name, int StartMonth, int StartDay, int EndMonth, int EndDay);

public enum LeapRule { None }

/// <summary>
/// Data-driven calendar configuration (spec Build §5.2). Gregorian is expressible by
/// supplying real month lengths + a leap rule; the default below is this world's 12×30 calendar.
/// </summary>
public sealed record CalendarDefinition(
    int MinutesPerHour,
    int HoursPerDay,
    IReadOnlyList<MonthDef> Months,
    IReadOnlyList<string> Weekdays,
    CalendarDate Epoch,
    string EraLabel,
    LeapRule LeapRule,
    IReadOnlyList<SeasonDef> Seasons)
{
    /// <summary>This world's calendar: 12 months × 30 days (360-day year), 7-day week, placeholder names.</summary>
    public static CalendarDefinition Default { get; } = CreateDefault();

    private static CalendarDefinition CreateDefault()
    {
        var months = Enumerable.Range(1, 12)
            .Select(i => new MonthDef($"Month {i}", 30))
            .ToArray();

        var weekdays = Enumerable.Range(1, 7)
            .Select(i => $"Day {i}")
            .ToArray();

        var seasons = new[]
        {
            new SeasonDef("Spring", 1, 1, 3, 30),
            new SeasonDef("Summer", 4, 1, 6, 30),
            new SeasonDef("Autumn", 7, 1, 9, 30),
            new SeasonDef("Winter", 10, 1, 12, 30),
        };

        return new CalendarDefinition(
            MinutesPerHour: 60,
            HoursPerDay: 24,
            Months: months,
            Weekdays: weekdays,
            Epoch: new CalendarDate(1, 1, 1, 0, 0),
            EraLabel: "",
            LeapRule: LeapRule.None,
            Seasons: seasons);
    }
}
