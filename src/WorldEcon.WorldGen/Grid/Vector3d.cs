namespace WorldEcon.WorldGen.Grid;

/// <summary>Three-component double-precision vector used for globe geometry.</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(Dot(this, this));

    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vector3d Cross(Vector3d a, Vector3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public Vector3d Normalize()
    {
        double l = Length;
        return new Vector3d(X / l, Y / l, Z / l);
    }

    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vector3d operator *(double s, Vector3d a) => a * s;
}
