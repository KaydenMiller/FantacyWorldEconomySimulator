namespace WorldEcon.Simulation.Random;

/// <summary>
/// A stateless, deterministic hash for turning stable inputs (seed, ids, tick) into a uniform value —
/// used where the simulation needs a "random-looking" but fully reproducible choice without consuming
/// or persisting any RNG stream. Pure integer arithmetic (SplitMix64 finalizer), so it is stable
/// across .NET versions, unlike framework hashing.
/// </summary>
public static class DeterministicHash
{
    /// <summary>Mixes a seed and three inputs into a uniform 64-bit value.</summary>
    public static ulong Combine(ulong seed, ulong first, ulong second, ulong third)
    {
        unchecked
        {
            ulong x = seed;
            x = Stir(x ^ first);
            x = Stir(x ^ second);
            x = Stir(x ^ third);
            return x;
        }
    }

    /// <summary>A deterministic, uniform integer in [minInclusive, maxInclusive], derived from a seed,
    /// two entity ids, and a tick. Same inputs always yield the same value.</summary>
    public static long RangeInclusive(ulong seed, System.Guid first, System.Guid second, long tick,
        long minInclusive, long maxInclusive)
    {
        if (maxInclusive <= minInclusive)
            return minInclusive;
        ulong span = (ulong)(maxInclusive - minInclusive) + 1UL;
        ulong hash = Combine(seed, GuidToUlong(first), GuidToUlong(second), unchecked((ulong)tick));
        return minInclusive + (long)(hash % span);
    }

    private static ulong Stir(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static ulong GuidToUlong(System.Guid value)
    {
        System.Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        return System.BitConverter.ToUInt64(bytes) ^ System.BitConverter.ToUInt64(bytes[8..]);
    }
}
