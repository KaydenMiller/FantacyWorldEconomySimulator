namespace WorldEcon.SharedKernel;

/// <summary>
/// Money as an integer count of the smallest currency unit (e.g. copper).
/// Denominations (gp/sp/cp) are a presentation concern and never used in sim math.
/// </summary>
public readonly record struct Money(long Units)
{
    public static readonly Money Zero = new(0);

    public bool IsNegative => Units < 0;

    public static Money operator +(Money a, Money b) => new(a.Units + b.Units);
    public static Money operator -(Money a, Money b) => new(a.Units - b.Units);
    public static Money operator *(Money a, long quantity) => new(a.Units * quantity);
    public static Money operator -(Money a) => new(-a.Units);
}
