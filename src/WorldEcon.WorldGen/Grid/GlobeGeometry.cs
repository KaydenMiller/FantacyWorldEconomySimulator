namespace WorldEcon.WorldGen.Grid;

/// <summary>
/// Geometry derived from a <see cref="GlobeGrid"/>: cell centres and corners on the unit sphere,
/// latitude/longitude, nearest-cell lookup, distances and areas. Derived data only; nothing here
/// feeds back into grid topology. Convention: +Y is the north pole; longitude 0 is at +Z and
/// increases towards +X.
/// </summary>
public sealed class GlobeGeometry
{
    private static readonly double Phi = (1 + Math.Sqrt(5)) / 2;

    /// <summary>Icosahedron vertices (cell ids 0–11), in the numbering used by <see cref="GlobeGrid"/>.</summary>
    internal static readonly Vector3d[] IcoVertices =
    [
        new(-1, Phi, 0), new(1, Phi, 0), new(-1, -Phi, 0), new(1, -Phi, 0),
        new(0, -1, Phi), new(0, 1, Phi), new(0, -1, -Phi), new(0, 1, -Phi),
        new(Phi, 0, -1), new(Phi, 0, 1), new(-Phi, 0, -1), new(-Phi, 0, 1),
    ];

    private readonly GlobeGrid _grid;
    private readonly Vector3d[] _centres;
    private readonly Vector3d[] _corners;

    private GlobeGeometry(GlobeGrid grid, Vector3d[] centres, Vector3d[] corners)
    {
        _grid = grid;
        _centres = centres;
        _corners = corners;
    }

    public static GlobeGeometry Compute(GlobeGrid grid)
    {
        var centres = new Vector3d[grid.CellCount];
        for (int c = 0; c < centres.Length; c++)
        {
            if (grid.IsPentagon(c))
            {
                centres[c] = IcoVertices[c].Normalize();
                continue;
            }
            var (a, b, d, wa, wb, wc) = grid.LatticePoint(c);
            centres[c] = (IcoVertices[a] * wa + IcoVertices[b] * wb + IcoVertices[d] * wc).Normalize();
        }

        var corners = new Vector3d[grid.TriangleCount];
        for (int t = 0; t < corners.Length; t++)
        {
            var (a, b, c) = grid.Triangle(t);
            corners[t] = (centres[a] + centres[b] + centres[c]).Normalize();
        }
        return new GlobeGeometry(grid, centres, corners);
    }

    /// <summary>Unit-length cell centre.</summary>
    public Vector3d Centre(int cell) => _centres[cell];

    /// <summary>Unit-length position of the corner at the given small triangle.</summary>
    public Vector3d Corner(int triangle) => _corners[triangle];

    public (double LatDeg, double LonDeg) LatLon(int cell)
    {
        var v = _centres[cell];
        double lat = Math.Asin(Math.Clamp(v.Y, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(v.X, v.Z) * 180 / Math.PI;
        return (lat, lon);
    }

    /// <summary>
    /// Greedy walk from cell 0 to the neighbour with the largest dot product against the target,
    /// until no neighbour improves; ties go to the lower id.
    /// </summary>
    public int NearestCell(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        var target = new Vector3d(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
        int best = 0;
        double bestDot = Vector3d.Dot(_centres[0], target);
        while (true)
        {
            int next = best;
            double nextDot = bestDot;
            foreach (int n in _grid.Neighbours(best))
            {
                double d = Vector3d.Dot(_centres[n], target);
                if (d > nextDot || (d == nextDot && next != best && n < next))
                {
                    next = n;
                    nextDot = d;
                }
            }
            if (next == best) return best;
            best = next;
            bestDot = nextDot;
        }
    }

    public double GreatCircleKm(int a, int b, double radiusKm)
    {
        var va = _centres[a];
        var vb = _centres[b];
        double angle = Math.Atan2(Vector3d.Cross(va, vb).Length, Vector3d.Dot(va, vb));
        return angle * radiusKm;
    }

    /// <summary>
    /// Area of the cell's polygon: spherical triangles (centre, corner k, corner k + 1) summed
    /// over the cell's corner ring, so the areas of all cells sum to the sphere area.
    /// </summary>
    public double CellAreaKm2(int cell, double radiusKm)
    {
        var tris = _grid.CornerTriangles(cell);
        var o = _centres[cell];
        double sum = 0;
        for (int k = 0; k < tris.Length; k++)
            sum += SphericalTriangleArea(o, _corners[tris[k]], _corners[tris[(k + 1) % tris.Length]]);
        return sum * radiusKm * radiusKm;
    }

    // Van Oosterom–Strakhov solid angle of a spherical triangle on the unit sphere.
    private static double SphericalTriangleArea(Vector3d a, Vector3d b, Vector3d c)
    {
        double num = Math.Abs(Vector3d.Dot(a, Vector3d.Cross(b, c)));
        double den = 1 + Vector3d.Dot(a, b) + Vector3d.Dot(b, c) + Vector3d.Dot(c, a);
        return 2 * Math.Atan2(num, den);
    }
}
