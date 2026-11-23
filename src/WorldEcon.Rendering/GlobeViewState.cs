using System.Numerics;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>
/// Camera for an orthographic globe view on a <see cref="Width"/> × <see cref="Height"/> pixel canvas. The globe is
/// rotated <see cref="YawDeg"/> about +Y, then <see cref="PitchDeg"/> about +X; the disc radius is
/// 0.45 × min(width, height) × <see cref="Zoom"/>, centred on the canvas. Screen y points down, world +Y is up.
/// </summary>
public readonly record struct GlobeViewState(double Width, double Height, double YawDeg, double PitchDeg, double Zoom = 1)
{
    public float Radius => (float)(Math.Min(Width, Height) * 0.45 * Zoom);

    public float CentreX => (float)(Width / 2);

    public float CentreY => (float)(Height / 2);

    public Matrix4x4 Rotation =>
        Matrix4x4.CreateRotationY((float)(YawDeg * Math.PI / 180)) * Matrix4x4.CreateRotationX((float)(PitchDeg * Math.PI / 180));

    /// <summary>World-space unit vector at the canvas centre: the rotated +Z axis.</summary>
    public Vector3 ViewDirection => Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.Transpose(Rotation));

    /// <summary>Pixel position of a unit vector; false when it faces away from the viewer (the position is still set).</summary>
    public bool TryProject(Vector3d unit, out float x, out float y)
    {
        var n = Vector3.TransformNormal(new Vector3((float)unit.X, (float)unit.Y, (float)unit.Z), Rotation);
        x = CentreX + (n.X * Radius);
        y = CentreY - (n.Y * Radius);
        return n.Z > 0;
    }

    /// <summary>Inverse orthographic projection: the world-space unit vector under a pixel, or null outside the disc.</summary>
    public Vector3? Unproject(double sx, double sy)
    {
        var r = Radius;
        var x = (float)((sx - CentreX) / r);
        var y = (float)(-(sy - CentreY) / r);
        var d2 = (x * x) + (y * y);
        if (d2 > 1) return null;
        var view = new Vector3(x, y, MathF.Sqrt(1 - d2));
        return Vector3.TransformNormal(view, Matrix4x4.Transpose(Rotation));
    }

    /// <summary>
    /// Yaw and pitch (degrees) that put <paramref name="direction"/> at the canvas centre: yaw = atan2(−x, z) brings it
    /// into the y–z plane, pitch = atan2(y, √(x² + z²)) then lifts it onto +Z. Pitch is within ±90°.
    /// </summary>
    public static (double YawDeg, double PitchDeg) Facing(Vector3d direction)
    {
        var d = direction.Normalize();
        var yaw = Math.Atan2(-d.X, d.Z) * 180 / Math.PI;
        var pitch = Math.Atan2(d.Y, Math.Sqrt((d.X * d.X) + (d.Z * d.Z))) * 180 / Math.PI;
        return (yaw, pitch);
    }
}
