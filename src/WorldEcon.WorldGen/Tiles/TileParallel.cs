namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Parallel loop over tile index ranges. The range [0, count) is cut into fixed chunks of <see cref="ChunkSize"/>
/// indices (independent of the thread count); the body must write only the slots of its own indices, so the result is
/// the same for any <see cref="ParallelOptions.MaxDegreeOfParallelism"/>.
/// </summary>
internal static class TileParallel
{
    public const int ChunkSize = 1 << 16;

    public static void For(int count, ParallelOptions? options, Action<int, int> body) => For(count, ChunkSize, options, body);

    /// <summary>As <see cref="For(int, ParallelOptions?, Action{int, int})"/> with chunks of <paramref name="chunkSize"/> indices (e.g. globe or region cells).</summary>
    public static void For(int count, int chunkSize, ParallelOptions? options, Action<int, int> body)
    {
        int chunks = (count + chunkSize - 1) / chunkSize;
        Parallel.For(0, chunks, options ?? new ParallelOptions(), c =>
        {
            int start = c * chunkSize;
            body(start, Math.Min(count, start + chunkSize));
        });
    }
}
