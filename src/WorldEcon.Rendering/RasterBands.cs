namespace WorldEcon.Rendering;

/// <summary>
/// Splits the rows of a frame bitmap into horizontal bands and fills them in parallel. Each band owns rows
/// [rowStart, rowEnd), so no two threads write the same pixel; each band visits the triangles in their submission order,
/// so a pixel covered by two triangles gets the later one, exactly as a single pass would. The result is the same bitmap
/// as one sequential pass over all rows.
/// </summary>
/// <remarks>
/// Band count: twice the processor count (work stealing evens out bands that cross the globe disc and bands that are
/// background only), at least <see cref="MinRowsPerBand"/> rows each. Every band tests every triangle's row range, an
/// early reject of a few comparisons per triangle per band.
/// </remarks>
internal static class RasterBands
{
    /// <summary>Fewest rows per band; a bitmap shorter than twice this is filled in one pass on the calling thread.</summary>
    public const int MinRowsPerBand = 16;

    /// <summary>Calls <paramref name="fillRows"/>(rowStart, rowEnd) once per band, concurrently, covering rows 0 to <paramref name="height"/>.</summary>
    public static void Run(int height, Action<int, int> fillRows)
    {
        var bands = Math.Clamp(height / MinRowsPerBand, 1, 2 * Environment.ProcessorCount);
        if (bands == 1)
        {
            fillRows(0, height);
            return;
        }

        Parallel.For(0, bands, b => fillRows((int)((long)b * height / bands), (int)((long)(b + 1) * height / bands)));
    }

    /// <summary>
    /// Rows [y0, y1) whose pixel centres lie between <paramref name="minY"/> and <paramref name="maxY"/> (top-inclusive,
    /// bottom-exclusive), limited to the band [rowStart, rowEnd); empty when y0 ≥ y1.
    /// </summary>
    public static (int Y0, int Y1) Rows(float minY, float maxY, int rowStart, int rowEnd) =>
        (Math.Max(rowStart, (int)MathF.Ceiling(minY - 0.5f)), Math.Min(rowEnd, (int)MathF.Ceiling(maxY - 0.5f)));
}
