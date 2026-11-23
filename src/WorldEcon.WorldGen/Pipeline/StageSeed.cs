using WorldEcon.Simulation.Random;

namespace WorldEcon.WorldGen.Pipeline;

/// <summary>Derives an independent, stable seed per generation stage from the world seed.</summary>
public static class StageSeed
{
    private const ulong WorldGenTag = 0x574F524C4447454EUL; // "WORLDGEN"

    public static ulong For(ulong worldSeed, WorldGenStage stage) =>
        DeterministicHash.Combine(worldSeed, WorldGenTag, (ulong)stage, 0);
}
