using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Noise;

/// <summary>3D value noise sampled on the sphere surface; deterministic, integer-lattice based.</summary>
public static class SphereNoise
{
    /// <summary>Fractal value noise in [-1, 1]; each octave doubles frequency and halves amplitude.</summary>
    public static double Sample(Vector3d p, ulong seed, int octaves, double frequency) =>
        Sample(p, seed, octaves, frequency, persistence: 0.5, lacunarity: 2.0);

    /// <summary>
    /// Fractal value noise in [-1, 1]; each octave multiplies amplitude by <paramref name="persistence"/> and frequency by
    /// <paramref name="lacunarity"/>. The sum is normalised by the total amplitude (0 octaves, or a zero total, gives 0).
    /// </summary>
    public static double Sample(Vector3d p, ulong seed, int octaves, double frequency, double persistence, double lacunarity)
    {
        double sum = 0, amp = 1, norm = 0, f = frequency;
        for (var o = 0; o < octaves; o++)
        {
            // Pre-mix the octave into the seed; adding o directly correlates with the lattice x coordinate through XOR.
            var octaveSeed = DeterministicHash.Combine(seed, (ulong)o, 0, 0);
            sum += amp * Lattice(p * f, octaveSeed);
            norm += amp;
            amp *= persistence;
            f *= lacunarity;
        }
        return norm == 0 ? 0 : Math.Clamp(sum / norm, -1.0, 1.0);
    }

    private static double Lattice(Vector3d q, ulong seed)
    {
        var x0 = (long)Math.Floor(q.X);
        var y0 = (long)Math.Floor(q.Y);
        var z0 = (long)Math.Floor(q.Z);
        var tx = Smooth(q.X - x0);
        var ty = Smooth(q.Y - y0);
        var tz = Smooth(q.Z - z0);

        var c00 = Lerp(Corner(seed, x0, y0, z0), Corner(seed, x0 + 1, y0, z0), tx);
        var c10 = Lerp(Corner(seed, x0, y0 + 1, z0), Corner(seed, x0 + 1, y0 + 1, z0), tx);
        var c01 = Lerp(Corner(seed, x0, y0, z0 + 1), Corner(seed, x0 + 1, y0, z0 + 1), tx);
        var c11 = Lerp(Corner(seed, x0, y0 + 1, z0 + 1), Corner(seed, x0 + 1, y0 + 1, z0 + 1), tx);
        return Lerp(Lerp(c00, c10, ty), Lerp(c01, c11, ty), tz);
    }

    private static double Corner(ulong seed, long x, long y, long z)
    {
        var h = DeterministicHash.Combine(seed, unchecked((ulong)x), unchecked((ulong)y), unchecked((ulong)z));
        return (h >> 11) / (double)(1UL << 53) * 2.0 - 1.0;
    }

    private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
