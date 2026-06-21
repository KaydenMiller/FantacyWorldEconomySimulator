namespace WorldEcon.Simulation.Random;

/// <summary>Persistable snapshot of an <see cref="IRng"/>'s internal state.</summary>
public readonly record struct RngState(ulong S0, ulong S1, ulong S2, ulong S3);

/// <summary>
/// Deterministic, version-pinned PRNG (spec Build §4.3). NOT System.Random — its
/// seeded sequence is not stable across .NET versions, which would break replay.
/// </summary>
public interface IRng
{
    ulong NextULong();
    int NextInt(int maxExclusive);
    RngState Capture();
}
