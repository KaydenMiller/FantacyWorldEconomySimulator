namespace WorldEcon.Simulation.Random;

/// <summary>
/// xoshiro256** PRNG, seeded via SplitMix64. Fixed algorithm = stable across .NET versions,
/// so (seed) reproduces a sequence forever (spec Build §4.3).
/// </summary>
public sealed class Xoshiro256StarStar : IRng
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256StarStar(ulong seed)
    {
        var sm = new SplitMix64(seed);
        _s0 = sm.Next();
        _s1 = sm.Next();
        _s2 = sm.Next();
        _s3 = sm.Next();
    }

    public Xoshiro256StarStar(RngState state)
    {
        _s0 = state.S0;
        _s1 = state.S1;
        _s2 = state.S2;
        _s3 = state.S3;
    }

    public RngState Capture() => new(_s0, _s1, _s2, _s3);

    public ulong NextULong()
    {
        unchecked
        {
            ulong result = Rotl(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 45);
            return result;
        }
    }

    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));

        // Lemire's unbiased bounded integer.
        ulong range = (ulong)maxExclusive;
        ulong x = NextULong();
        UInt128 m = (UInt128)x * range;
        ulong low = (ulong)m;
        if (low < range)
        {
            ulong threshold = (0UL - range) % range;
            while (low < threshold)
            {
                x = NextULong();
                m = (UInt128)x * range;
                low = (ulong)m;
            }
        }
        return (int)(ulong)(m >> 64);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));
}

/// <summary>SplitMix64 — used only to expand a single seed into xoshiro's 256-bit state.</summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _x = seed;

    public ulong Next()
    {
        unchecked
        {
            _x += 0x9E3779B97F4A7C15UL;
            ulong z = _x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
