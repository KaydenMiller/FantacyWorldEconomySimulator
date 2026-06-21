namespace WorldEcon.Simulation.Random;

/// <summary>One independent RNG stream per simulation subsystem (spec Build §4.3, §8.2).</summary>
public enum RngStream
{
    Pricing = 1,
    Production = 2,
    Trade = 3,
    Events = 4,
}

public interface IRngStreams
{
    IRng For(RngStream stream);
    IReadOnlyDictionary<RngStream, RngState> Capture();
}

/// <summary>
/// Seeds one xoshiro256** stream per subsystem from the world seed mixed with a per-stream
/// constant, so adding a draw in one subsystem never shifts another's sequence.
/// </summary>
public sealed class RngStreams : IRngStreams
{
    private readonly Dictionary<RngStream, Xoshiro256StarStar> _streams = new();

    public RngStreams(ulong worldSeed, IReadOnlyDictionary<RngStream, RngState>? restore = null)
    {
        foreach (RngStream stream in Enum.GetValues<RngStream>())
        {
            _streams[stream] = restore is not null && restore.TryGetValue(stream, out RngState state)
                ? new Xoshiro256StarStar(state)
                : new Xoshiro256StarStar(MixSeed(worldSeed, (ulong)stream));
        }
    }

    public IRng For(RngStream stream) => _streams[stream];

    public IReadOnlyDictionary<RngStream, RngState> Capture()
        => _streams.ToDictionary(kv => kv.Key, kv => kv.Value.Capture());

    private static ulong MixSeed(ulong worldSeed, ulong stream)
    {
        unchecked
        {
            var sm = new SplitMix64(worldSeed ^ (stream * 0x9E3779B97F4A7C15UL));
            return sm.Next();
        }
    }
}
