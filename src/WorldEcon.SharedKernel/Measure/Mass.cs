namespace WorldEcon.SharedKernel.Measure;

/// <summary>Mass as an integer count of grams. Units (g/kg/oz/lb) are a presentation concern
/// (see MeasurementFormat) and never used in simulation math.</summary>
public readonly record struct Mass(long Grams)
{
    public static readonly Mass Zero = new(0);
    public bool IsNegative => Grams < 0;

    public static Mass operator +(Mass a, Mass b) => new(a.Grams + b.Grams);
    public static Mass operator -(Mass a, Mass b) => new(a.Grams - b.Grams);
    public static Mass operator *(Mass a, long quantity) => new(a.Grams * quantity);
}
