using WorldEcon.Simulation.Random;
using WorldEcon.SharedKernel.Globe;

namespace WorldEcon.WorldGen.Naming;

/// <summary>Deterministic syllable-based names for natural areas: a generated stem plus a kind-specific prefix or suffix.</summary>
public static class AreaNameGenerator
{
    private static readonly string[] Onsets =
    [
        "Bar", "Cal", "Dor", "El", "Fen", "Gal", "Hal", "Ir", "Jor", "Kel", "Lor", "Mar", "Nor", "Or", "Pel", "Quen",
        "Ral", "Sar", "Tor", "Ul", "Vel", "Wyn", "Yor", "Zan", "Ash", "Bren", "Cor", "Dun", "Eld", "Thal",
    ];

    private static readonly string[] Middles = ["a", "o", "i", "e", "an", "en", "or", "al", "ur", "ar", "id", "eth"];

    private static readonly string[] Endings = ["dor", "mir", "wen", "ria", "gard", "thar", "lon", "vik", "ren", "dal", "nos", "essa", "und", "ia"];

    public static string Name(GeographicRegionKind kind, ulong seed)
    {
        var onset = Pick(Onsets, seed, 1);
        var syllables = 1 + (int)(DeterministicHash.Combine(seed, 2, 0, 0) % 2);
        var stem = onset;
        for (var i = 0; i < syllables - 1; i++)
            stem += Pick(Middles, seed, 10 + (ulong)i);
        stem += Pick(Endings, seed, 20);

        return kind switch
        {
            GeographicRegionKind.Ocean => $"{stem} Ocean",
            GeographicRegionKind.Sea => $"{stem} Sea",
            GeographicRegionKind.Lake => $"Lake {stem}",
            GeographicRegionKind.DryLakeBed => $"{stem} Flats",
            GeographicRegionKind.MountainRange => $"{stem} Mountains",
            GeographicRegionKind.Hills => $"{stem} Hills",
            GeographicRegionKind.Basin => $"{stem} Basin",
            GeographicRegionKind.Plain => $"{stem} Plains",
            GeographicRegionKind.Forest => $"{stem} Forest",
            GeographicRegionKind.Desert => $"{stem} Desert",
            GeographicRegionKind.Grassland => $"{stem} Steppe",
            GeographicRegionKind.Tundra => $"{stem} Tundra",
            GeographicRegionKind.Swamp => $"{stem} Marsh",
            GeographicRegionKind.IceSheet => DeterministicHash.Combine(seed, 30, 0, 0) % 2 == 0 ? $"{stem} Glacier" : $"{stem} Ice Shelf",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static string Pick(string[] table, ulong seed, ulong slot) =>
        table[(int)(DeterministicHash.Combine(seed, slot, 0, 0) % (ulong)table.Length)];
}
