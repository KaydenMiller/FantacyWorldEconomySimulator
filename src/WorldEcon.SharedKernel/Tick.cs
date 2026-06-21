namespace WorldEcon.SharedKernel;

/// <summary>
/// In-world time as an integer count of minutes since the world epoch (spec Build §5.1).
/// Day/week length is calendar-defined; only minute arithmetic lives here.
/// </summary>
public readonly record struct Tick(long Value) : IComparable<Tick>
{
    public const long MinutesPerHour = 60;
    public const long DefaultMinutesPerDay = 1_440;   // 24h * 60m (standard calendar only)
    public const long DefaultMinutesPerWeek = 10_080; // 7 * 1440 (standard calendar only)

    public static readonly Tick Zero = new(0);

    public Tick AddMinutes(long minutes) => new(Value + minutes);

    public int CompareTo(Tick other) => Value.CompareTo(other.Value);

    public static bool operator <(Tick a, Tick b) => a.Value < b.Value;
    public static bool operator >(Tick a, Tick b) => a.Value > b.Value;
    public static bool operator <=(Tick a, Tick b) => a.Value <= b.Value;
    public static bool operator >=(Tick a, Tick b) => a.Value >= b.Value;
}
