using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// A rotation about a unit axis by a whole number of micro-radians. Plate motion accumulates integer angles, so the
/// position of a plate after any number of steps depends only on the integer sum, not on the order of floating-point
/// additions. The rotation matrix comes from one <see cref="Math.Sin"/>/<see cref="Math.Cos"/> call site
/// (<see cref="Matrix"/>).
/// </summary>
public readonly record struct QuantisedRotation(Vector3d Axis, long AngleMicroRad)
{
    /// <summary>The angle per step for a plate moving <paramref name="speedMmPerYr"/> at its fastest point (1 mm/year = 1 km per million years).</summary>
    public static long StepMicroRad(double speedMmPerYr, int stepMy, double radiusKm) =>
        (long)Math.Round(speedMmPerYr * stepMy / radiusKm * 1e6);

    public QuantisedRotation Advance(long deltaMicroRad) => this with { AngleMicroRad = AngleMicroRad + deltaMicroRad };

    public Vector3d Apply(Vector3d p) => Matrix().Apply(p);

    public Vector3d ApplyInverse(Vector3d p) => Matrix().ApplyTransposed(p);

    /// <summary>The rotation matrix (Rodrigues' formula).</summary>
    public Rotation3 Matrix()
    {
        var t = AngleMicroRad / 1e6;
        var c = Math.Cos(t);
        var s = Math.Sin(t);
        var k = Axis;
        var v = 1 - c;
        return new Rotation3(
            c + k.X * k.X * v, k.X * k.Y * v - k.Z * s, k.X * k.Z * v + k.Y * s,
            k.Y * k.X * v + k.Z * s, c + k.Y * k.Y * v, k.Y * k.Z * v - k.X * s,
            k.Z * k.X * v - k.Y * s, k.Z * k.Y * v + k.X * s, c + k.Z * k.Z * v);
    }
}

/// <summary>A 3×3 rotation matrix, row-major.</summary>
public readonly record struct Rotation3(double M00, double M01, double M02, double M10, double M11, double M12, double M20, double M21, double M22)
{
    public Vector3d Apply(Vector3d p) => new(
        M00 * p.X + M01 * p.Y + M02 * p.Z,
        M10 * p.X + M11 * p.Y + M12 * p.Z,
        M20 * p.X + M21 * p.Y + M22 * p.Z);

    /// <summary>The inverse rotation (the transpose).</summary>
    public Vector3d ApplyTransposed(Vector3d p) => new(
        M00 * p.X + M10 * p.Y + M20 * p.Z,
        M01 * p.X + M11 * p.Y + M21 * p.Z,
        M02 * p.X + M12 * p.Y + M22 * p.Z);
}
