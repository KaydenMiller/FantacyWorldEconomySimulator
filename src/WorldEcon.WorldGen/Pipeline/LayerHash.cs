using WorldEcon.Simulation.Random;

namespace WorldEcon.WorldGen.Pipeline;

/// <summary>
/// Order-sensitive 64-bit hash of a layer's values, chained through <see cref="DeterministicHash"/>.
/// Framework hashing is not used because it is randomised per process.
/// </summary>
public static class LayerHash
{
    private const ulong Seed = 0x4C41594552484153UL; // "LAYERHAS"

    public static ulong Of(ReadOnlySpan<int> values)
    {
        var h = Seed;
        foreach (var v in values)
            h = DeterministicHash.Combine(h, unchecked((ulong)(long)v), 0, 0);
        return h;
    }

    public static ulong Of(ReadOnlySpan<short> values)
    {
        var h = Seed;
        foreach (var v in values)
            h = DeterministicHash.Combine(h, unchecked((ulong)(long)v), 0, 0);
        return h;
    }

    public static ulong Of(ReadOnlySpan<long> values)
    {
        var h = Seed;
        foreach (var v in values)
            h = DeterministicHash.Combine(h, unchecked((ulong)v), 0, 0);
        return h;
    }

    public static ulong Of(ReadOnlySpan<byte> values)
    {
        var h = Seed;
        foreach (var v in values)
            h = DeterministicHash.Combine(h, v, 0, 0);
        return h;
    }

    /// <summary>Hash of a signed byte layer, each value sign-extended like the <see cref="int"/> overload.</summary>
    public static ulong Of(ReadOnlySpan<sbyte> values)
    {
        var h = Seed;
        foreach (var v in values)
            h = DeterministicHash.Combine(h, unchecked((ulong)(long)v), 0, 0);
        return h;
    }

    public static ulong Of(ReadOnlySpan<ushort> values)
    {
        var h = Seed;
        foreach (var v in values)
            h = DeterministicHash.Combine(h, v, 0, 0);
        return h;
    }

    /// <summary>Hash of a flag layer: false as 0 and true as 1, the same values as the byte overload.</summary>
    public static ulong Of(ReadOnlySpan<bool> values)
    {
        var h = Seed;
        foreach (var v in values)
            h = DeterministicHash.Combine(h, v ? 1UL : 0UL, 0, 0);
        return h;
    }
}
