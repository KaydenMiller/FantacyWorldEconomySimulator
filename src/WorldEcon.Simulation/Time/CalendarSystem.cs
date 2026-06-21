using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Simulation.Time;

/// <summary>
/// Deterministic, integer-only converter between <see cref="Tick"/> and <see cref="CalendarDate"/>
/// for a given <see cref="CalendarDefinition"/> (spec Build §5.3). No NodaTime / DateTime / float.
/// Assumes uniform years (LeapRule.None); other leap rules extend the year-length logic.
/// </summary>
public sealed class CalendarSystem
{
    private readonly CalendarDefinition _def;
    private readonly long _minutesPerDay;
    private readonly int _daysPerYear;
    private readonly int[] _monthStartDay; // 0-based cumulative day offset at the start of each month
    private readonly long _epochAbsMinute;
    private readonly long _epochAbsDay;

    public CalendarSystem(CalendarDefinition def)
    {
        if (def.LeapRule != LeapRule.None)
            throw new NotSupportedException("Only LeapRule.None is supported in Plan 1a.");

        _def = def;
        _minutesPerDay = (long)def.HoursPerDay * def.MinutesPerHour;

        _monthStartDay = new int[def.Months.Count];
        int acc = 0;
        for (int i = 0; i < def.Months.Count; i++)
        {
            _monthStartDay[i] = acc;
            acc += def.Months[i].Days;
        }
        _daysPerYear = acc;

        _epochAbsMinute = AbsMinute(def.Epoch);
        _epochAbsDay = FixedMath.DivFloor(_epochAbsMinute, _minutesPerDay);
    }

    public Tick ToTick(CalendarDate date) => new(AbsMinute(date) - _epochAbsMinute);

    public CalendarDate ToDate(Tick tick)
    {
        long absMinute = tick.Value + _epochAbsMinute;
        long day = FixedMath.DivFloor(absMinute, _minutesPerDay);
        long minuteOfDay = absMinute - day * _minutesPerDay;
        int hour = (int)(minuteOfDay / _def.MinutesPerHour);
        int minute = (int)(minuteOfDay % _def.MinutesPerHour);

        long year = FixedMath.DivFloor(day, _daysPerYear);
        int dayOfYear = (int)(day - year * _daysPerYear);

        int month = _monthStartDay.Length; // default last month; corrected below
        for (int i = 0; i < _monthStartDay.Length; i++)
        {
            int start = _monthStartDay[i];
            int end = start + _def.Months[i].Days;
            if (dayOfYear >= start && dayOfYear < end)
            {
                month = i + 1;
                break;
            }
        }
        int dayOfMonth = dayOfYear - _monthStartDay[month - 1] + 1;

        return new CalendarDate((int)year, month, dayOfMonth, hour, minute);
    }

    public int WeekdayIndex(Tick tick)
    {
        long absMinute = tick.Value + _epochAbsMinute;
        long day = FixedMath.DivFloor(absMinute, _minutesPerDay);
        return (int)FixedMath.FloorMod(day - _epochAbsDay, _def.Weekdays.Count);
    }

    public SeasonDef SeasonAt(Tick tick)
    {
        if (_def.Seasons.Count == 0)
            throw new InvalidOperationException("Calendar defines no seasons.");

        CalendarDate d = ToDate(tick);
        foreach (SeasonDef s in _def.Seasons)
            if (InSeason(d.Month, d.Day, s))
                return s;

        return _def.Seasons[0];
    }

    private long AbsMinute(CalendarDate d)
        => AbsDay(d.Year, d.Month, d.Day) * _minutesPerDay
           + (long)d.Hour * _def.MinutesPerHour
           + d.Minute;

    private long AbsDay(int year, int month, int day)
        => (long)year * _daysPerYear + _monthStartDay[month - 1] + (day - 1);

    private static bool InSeason(int month, int day, SeasonDef s)
    {
        int v = month * 100 + day;
        int start = s.StartMonth * 100 + s.StartDay;
        int end = s.EndMonth * 100 + s.EndDay;
        return start <= end ? (v >= start && v <= end) : (v >= start || v <= end);
    }
}
