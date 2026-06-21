namespace WorldEcon.SharedKernel;

/// <summary>
/// Deterministic integer / fixed-point arithmetic. All rounding is half-to-even
/// so price/cost computations are reproducible and auditable (spec Build §4.4).
/// Fractions are expressed in basis points (1 bp = 1/10000).
/// </summary>
public static class FixedMath
{
    public const long BpScale = 10_000;

    /// <summary>value * (bp / 10000), half-to-even.</summary>
    public static long MulBp(long value, long bp) => MulDiv(value, bp, BpScale);

    /// <summary>(a * b) / denominator using a 128-bit intermediate; half-to-even.</summary>
    public static long MulDiv(long a, long b, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        Int128 product = (Int128)a * b;
        Int128 q = product / denominator;
        Int128 r = product - q * denominator;
        if (r == 0) return checked((long)q);

        Int128 twiceR = Int128.Abs(r) * 2;
        Int128 absD = Int128.Abs((Int128)denominator);
        bool roundAway = twiceR > absD || (twiceR == absD && ((long)(q % 2)) != 0);
        if (roundAway)
        {
            bool negative = (a < 0) ^ (b < 0) ^ (denominator < 0);
            q += negative ? -1 : 1;
        }
        return checked((long)q);
    }

    /// <summary>Division rounding toward negative infinity.</summary>
    public static long DivFloor(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        long q = numerator / denominator;
        long r = numerator % denominator;
        if (r != 0 && ((r < 0) != (denominator < 0))) q--;
        return q;
    }

    /// <summary>Division rounding half-to-even.</summary>
    public static long DivRound(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        long q = numerator / denominator;
        long r = numerator - q * denominator;
        if (r == 0) return q;

        long twiceR = Math.Abs(r) * 2;
        long absD = Math.Abs(denominator);
        bool roundAway = twiceR > absD || (twiceR == absD && (q % 2) != 0);
        if (roundAway)
        {
            bool negative = (numerator < 0) ^ (denominator < 0);
            q = checked(q + (negative ? -1 : 1));
        }
        return q;
    }

    /// <summary>Modulo that is always in [0, modulus) for positive modulus.</summary>
    public static long FloorMod(long a, long modulus)
    {
        if (modulus <= 0) throw new ArgumentOutOfRangeException(nameof(modulus), "Modulus must be positive.");
        return ((a % modulus) + modulus) % modulus;
    }
}
