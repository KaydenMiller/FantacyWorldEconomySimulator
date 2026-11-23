using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Stages;

/// <summary>Local east/north tangent frame on the unit sphere and neighbour-slot selection, shared by flow stages.</summary>
internal static class TangentFrame
{
    private const double PolarFallbackY = 0.999;

    /// <summary>Unit east tangent at unit position <paramref name="p"/>; falls back to +X (made tangent) near the poles.</summary>
    public static Vector3d East(Vector3d p)
    {
        var east = Math.Abs(p.Y) > PolarFallbackY
            ? new Vector3d(1, 0, 0)
            : Vector3d.Cross(new Vector3d(0, 1, 0), p);
        // Remove any radial component (only matters for the polar fallback), then normalise.
        return (east - (p * Vector3d.Dot(east, p))).Normalize();
    }

    /// <summary>Unit north tangent for the given east tangent at <paramref name="p"/>.</summary>
    public static Vector3d North(Vector3d p, Vector3d east) => Vector3d.Cross(p, east);

    /// <summary>Normalised direction from <paramref name="p"/> to each neighbour of cell <paramref name="c"/>, in slot order.</summary>
    public static Vector3d[] NeighbourDirections(GlobeGrid grid, GlobeGeometry geo, int c, Vector3d p)
    {
        var neighbours = grid.Neighbours(c);
        var tangents = new Vector3d[neighbours.Length];
        for (var k = 0; k < neighbours.Length; k++)
            tangents[k] = (geo.Centre(neighbours[k]) - p).Normalize();
        return tangents;
    }

    /// <summary>Slot whose direction has the largest dot product with <paramref name="intended"/>; ties go to the lower slot.</summary>
    public static int BestSlot(Vector3d[] tangents, Vector3d intended)
    {
        var best = 0;
        var bestDot = double.MinValue;
        for (var k = 0; k < tangents.Length; k++)
        {
            var v = Vector3d.Dot(tangents[k], intended);
            if (v > bestDot) { bestDot = v; best = k; }
        }

        return best;
    }
}
