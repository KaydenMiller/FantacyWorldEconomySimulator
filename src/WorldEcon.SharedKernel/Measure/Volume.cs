namespace WorldEcon.SharedKernel.Measure;

/// <summary>Volume as an integer count of cubic centimetres (1 cm³ = 1 mL). Units
/// (cm³/L/m³/in³/ft³) are a presentation concern (see MeasurementFormat) and never
/// used in simulation math.</summary>
public readonly record struct Volume(long CubicCentimeters)
{
    public static readonly Volume Zero = new(0);
    public bool IsNegative => CubicCentimeters < 0;

    public static Volume operator +(Volume a, Volume b) => new(a.CubicCentimeters + b.CubicCentimeters);
    public static Volume operator -(Volume a, Volume b) => new(a.CubicCentimeters - b.CubicCentimeters);
    public static Volume operator *(Volume a, long quantity) => new(a.CubicCentimeters * quantity);
}
