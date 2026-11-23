namespace WorldEcon.WorldGen.Grid;

/// <summary>
/// The gnomonic chart of one globe cell: the tangent plane at its centre c with axes e1 = normalize(Y × c) (X × c within
/// 1e-6 of a pole) and e2 = c × e1. Texel (i, j) of an N × N cell texture covers [−h, h]² with h = the largest |u| or
/// |v| of the cell's corners, so the texture contains the whole cell polygon. Generation (cell imagery) and rendering
/// both use this type, so a texel means the same point on the sphere in both.
/// </summary>
public readonly record struct CellChart(Vector3d Centre, Vector3d E1, Vector3d E2, double HalfExtent)
{
    /// <summary>The chart of globe cell <paramref name="cell"/> of <paramref name="grid"/> with <paramref name="geometry"/>.</summary>
    public static CellChart For(GlobeGrid grid, GlobeGeometry geometry, int cell)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(geometry);
        var c = geometry.Centre(cell);
        var axis = Math.Abs(c.Y) > 1 - 1e-6 ? new Vector3d(1, 0, 0) : new Vector3d(0, 1, 0);
        var e1 = Vector3d.Cross(axis, c).Normalize();
        var e2 = Vector3d.Cross(c, e1);
        var h = 0.0;
        foreach (var t in grid.CornerTriangles(cell))
        {
            var (u, v) = ToPlane(c, e1, e2, geometry.Corner(t));
            h = Math.Max(h, Math.Max(Math.Abs(u), Math.Abs(v)));
        }

        return new CellChart(c, e1, e2, h);
    }

    /// <summary>Plane coordinates (u, v) of a unit vector in front of the chart (dot with the centre &gt; 0).</summary>
    public (double U, double V) ToPlane(Vector3d p) => ToPlane(Centre, E1, E2, p);

    /// <summary>The unit vector at plane coordinates (u, v).</summary>
    public Vector3d FromPlane(double u, double v) => (Centre + (E1 * u) + (E2 * v)).Normalize();

    /// <summary>Unit vector at the centre of texel (i, j) of an <paramref name="size"/> × <paramref name="size"/> texture.</summary>
    public Vector3d TexelCentre(int i, int j, int size)
    {
        var step = 2 * HalfExtent / size;
        return FromPlane(-HalfExtent + ((i + 0.5) * step), -HalfExtent + ((j + 0.5) * step));
    }

    /// <summary>Texture coordinates of <paramref name="p"/> in texels (0 … size along each axis).</summary>
    public (double I, double J) ToTexel(Vector3d p, int size)
    {
        var (u, v) = ToPlane(p);
        var k = size / (2 * HalfExtent);
        return ((u + HalfExtent) * k, (v + HalfExtent) * k);
    }

    private static (double U, double V) ToPlane(Vector3d c, Vector3d e1, Vector3d e2, Vector3d p)
    {
        var d = Vector3d.Dot(p, c);
        return (Vector3d.Dot(p, e1) / d, Vector3d.Dot(p, e2) / d);
    }
}
