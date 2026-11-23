using WorldEcon.Simulation.Random;
using WorldEcon.SharedKernel.Globe;
using WorldEcon.WorldGen.Naming;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>A natural area: a named group of cells, each with a coverage share in basis points.</summary>
public sealed record GeneratedArea(int Index, GeographicRegionKind Kind, string Name, IReadOnlyList<(int Cell, short CoverageBp)> Cells);

/// <summary>
/// Stage 9: natural areas and their names. Landform-based kinds map one landform each: MountainRange → Mountain,
/// Hills → Hills, Basin → Basin, Plain → Plain, Lake → Lake, DryLakeBed → DryLakeBed. A cell is a member of such a kind
/// when the landform is one of the cell's dominant landforms (spec §6.4 "connected cells sharing a dominant feature"):
/// its composition share is at least <see cref="AreaSettings.MemberThresholdBp"/> AND either it is the cell's
/// largest entry (the first entry in composition order on ties; Stage 7 lists the highland entry first) or it is not
/// Plain and its share is at least <see cref="AreaSettings.DominantShareBp"/>. Plain is the remainder Stage 7 assigns after the
/// highland and lowland shares, so it is dominant only as the largest entry; this keeps one Plain area from spanning
/// the hill and mountain cells between lowlands. Coverage is that share. Ocean/Sea membership is
/// <see cref="WorldLayers.IsOcean"/>, and Forest (TemperateForest, TropicalForest, Taiga), Desert, Grassland
/// (Grassland, Savanna, Shrubland), Tundra, Swamp and IceSheet (Ice) membership is the cell's main biome; both have
/// coverage 10,000. A cell can belong to several areas of different kinds. Connected components (BFS over
/// neighbours, in cell-id order) per kind with at least <see cref="AreaSettings.MinAreaCells"/> cells become
/// areas (Ocean, Lake and DryLakeBed: at least <see cref="AreaSettings.MinWaterAreaCells"/>); an ocean component smaller than
/// <see cref="AreaSettings.SeaMaxCells"/> is a Sea. Areas are indexed in (kind, smallest member cell) order. A name
/// that repeats an earlier area's gets a roman numeral suffix (II, III, ...). The Basin kind is not produced by the
/// full pipeline: Stage 8 relabels every Basin share to Lake or DryLakeBed. It is kept for the future map editor.
/// </summary>
public static class AreaStage
{
    private const int BpFull = 10_000;

    public static void Run(WorldLayers layers, WorldGenSettings all, ulong seed)
    {
        var settings = all.Areas;
        var grid = layers.Grid;
        var n = grid.CellCount;
        var kinds = Enum.GetValues<GeographicRegionKind>();
        var found = new List<(GeographicRegionKind Kind, List<int> Cells, short[] Coverage)>();

        foreach (var kind in kinds)
        {
            if (kind == GeographicRegionKind.Sea) continue; // derived from Ocean components
            var coverage = new short[n];
            for (var c = 0; c < n; c++)
                coverage[c] = (short)Coverage(layers, c, kind, settings.MemberThresholdBp, settings.DominantShareBp);

            var minCells = kind is GeographicRegionKind.Ocean or GeographicRegionKind.Lake or GeographicRegionKind.DryLakeBed
                ? settings.MinWaterAreaCells
                : settings.MinAreaCells;
            var visited = new bool[n];
            for (var start = 0; start < n; start++)
            {
                if (visited[start] || coverage[start] == 0) continue;
                var cells = new List<int> { start };
                visited[start] = true;
                for (var head = 0; head < cells.Count; head++)
                    foreach (var nb in grid.Neighbours(cells[head]))
                    {
                        if (visited[nb] || coverage[nb] == 0) continue;
                        visited[nb] = true;
                        cells.Add(nb);
                    }

                if (cells.Count < minCells) continue;
                cells.Sort();
                var k = kind == GeographicRegionKind.Ocean && cells.Count < settings.SeaMaxCells ? GeographicRegionKind.Sea : kind;
                found.Add((k, cells, coverage));
            }
        }

        // Stable sort: (kind, smallest member cell); the smallest cell is unique per component within a kind.
        var ordered = found.OrderBy(f => (int)f.Kind).ThenBy(f => f.Cells[0]).ToList();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var areas = new List<GeneratedArea>(ordered.Count);
        foreach (var f in ordered)
        {
            var nameSeed = DeterministicHash.Combine(seed, (ulong)f.Kind, (ulong)f.Cells[0], 0);
            var name = AreaNameGenerator.Name(f.Kind, nameSeed);
            var occurrence = used.GetValueOrDefault(name) + 1;
            used[name] = occurrence;
            if (occurrence > 1) name = $"{name} {Roman(occurrence)}";
            var cells = f.Cells.Select(c => (c, f.Coverage[c])).ToArray();
            areas.Add(new GeneratedArea(areas.Count, f.Kind, name, cells));
        }

        layers.Areas = areas;
    }

    private static int Coverage(WorldLayers l, int cell, GeographicRegionKind kind, int thresholdBp, int dominantBp)
    {
        switch (kind)
        {
            case GeographicRegionKind.Ocean:
                return l.IsOcean(cell) ? BpFull : 0;
            case GeographicRegionKind.Lake: return Share(l, cell, Landform.Lake, thresholdBp, dominantBp);
            case GeographicRegionKind.DryLakeBed: return Share(l, cell, Landform.DryLakeBed, thresholdBp, dominantBp);
            case GeographicRegionKind.MountainRange: return Share(l, cell, Landform.Mountain, thresholdBp, dominantBp);
            case GeographicRegionKind.Hills: return Share(l, cell, Landform.Hills, thresholdBp, dominantBp);
            case GeographicRegionKind.Basin: return Share(l, cell, Landform.Basin, thresholdBp, dominantBp);
            case GeographicRegionKind.Plain: return Share(l, cell, Landform.Plain, thresholdBp, dominantBp);
        }

        var biome = (Biome)l.BiomeOf[cell];
        var member = kind switch
        {
            GeographicRegionKind.Forest => biome is Biome.TemperateForest or Biome.TropicalForest or Biome.Taiga,
            GeographicRegionKind.Desert => biome == Biome.Desert,
            GeographicRegionKind.Grassland => biome is Biome.Grassland or Biome.Savanna or Biome.Shrubland,
            GeographicRegionKind.Tundra => biome == Biome.Tundra,
            GeographicRegionKind.Swamp => biome == Biome.Swamp,
            GeographicRegionKind.IceSheet => biome == Biome.Ice,
            _ => false,
        };
        return member ? BpFull : 0;
    }

    // Landform membership: share >= threshold and the landform is dominant in the cell (see the class comment).
    private static int Share(WorldLayers l, int cell, Landform landform, int thresholdBp, int dominantBp)
    {
        var comp = l.Composition[cell];
        var share = 0;
        var largest = -1;
        for (var i = 0; i < comp.Length; i++)
        {
            if (comp[i].Landform == landform) share = comp[i].ShareBp;
            if (largest < 0 || comp[i].ShareBp > comp[largest].ShareBp) largest = i;
        }

        if (share == 0 || share < thresholdBp) return 0;
        var isLargest = comp[largest].Landform == landform;
        var isSecondary = landform != Landform.Plain && share >= dominantBp;
        return isLargest || isSecondary ? share : 0;
    }

    private static string Roman(int n) => n switch
    {
        2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X",
        _ => $"({n})",
    };

    public static ulong Hash(WorldLayers l)
    {
        var h = DeterministicHash.Combine((ulong)l.Areas.Count, 0, 0, 0);
        foreach (var a in l.Areas)
        {
            h = DeterministicHash.Combine(h, (ulong)a.Index, (ulong)a.Kind, (ulong)a.Cells.Count);
            foreach (var (cell, cov) in a.Cells)
                h = DeterministicHash.Combine(h, (ulong)cell, (ulong)cov, 0);
            foreach (var ch in a.Name)
                h = DeterministicHash.Combine(h, ch, 0, 0);
        }

        return h;
    }
}
