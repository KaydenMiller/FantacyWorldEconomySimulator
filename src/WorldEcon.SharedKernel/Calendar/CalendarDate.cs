namespace WorldEcon.SharedKernel.Calendar;

/// <summary>A point on the in-world calendar. All fields are 1-based except Hour/Minute (0-based).</summary>
public readonly record struct CalendarDate(int Year, int Month, int Day, int Hour, int Minute);
