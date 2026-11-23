using System.Diagnostics;
using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Globe;
using WorldEcon.SharedKernel.Globe;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.Seeding;

/// <summary>Outcome of <see cref="GlobeSeeding.RunAsync"/>.</summary>
/// <param name="Generated">The generated globe (layers, per-stage hashes and durations, generator version).</param>
/// <param name="PersistDuration">
/// Wall time of every write in <see cref="GlobeSeeding.RunAsync"/>: the first <c>SaveChanges</c> (the world row plus every
/// entity the caller had already added to the context, e.g. the whole imported economy), the globe tables, the
/// natural-area rows and the settlement pins. It excludes generation and the final commit.
/// </param>
/// <param name="GlobeTablesDuration">The part of <paramref name="PersistDuration"/> spent in <see cref="GlobeStore.WriteAsync"/> (globe tables only).</param>
/// <param name="Warnings">
/// Pin warnings (an auto-placed group anchor whose separation was relaxed, in request order), then route/terrain
/// contradictions (one line per settlement pair and route category).
/// </param>
public sealed record GlobeSeedingReport(GeneratedWorld Generated, TimeSpan PersistDuration, TimeSpan GlobeTablesDuration, IReadOnlyList<string> Warnings);

/// <summary>
/// Progress of <see cref="GlobeSeeding.RunAsync"/>: <paramref name="Step"/> is the step about to run or running (a
/// <see cref="WorldGenStage"/> name during generation, then <see cref="GlobeSeeding.StepGlobeTables"/>,
/// <see cref="GlobeSeeding.StepTileBlocks"/>, <see cref="GlobeSeeding.StepLakes"/>, <see cref="GlobeSeeding.StepRegionLevel"/>,
/// <see cref="GlobeSeeding.StepAreasAndPins"/>); <paramref name="Fraction"/> is the estimated fraction of the whole run
/// done (generation 0–<see cref="GlobeSeeding.GenerationShare"/> by the generator's stage weights, writing the rest).
/// Reported synchronously on the seeding thread; the same step is reported again with a higher fraction while tile blocks
/// are written.
/// </summary>
public readonly record struct SeedingProgress(string Step, double Fraction);

/// <summary>
/// Globe generation for a newly seeded world: generate, resolve pins and authored area names, persist, pin
/// settlements, and report route warnings.
/// <para>
/// Order: generation and the two lookups (<see cref="PinStage.Run"/>, <see cref="PinStage.NameAreas"/>) run before
/// anything is written, so a pin or name error throws <see cref="InvalidOperationException"/> without leaving a
/// half-written globe. Then the world row is updated (<see cref="World.SetGlobe"/>, <see cref="World.SetSeaLevel"/>)
/// and saved together with every entity already tracked by <paramref name="db"/>, the globe tables, tile blocks, lakes and
/// region level are written by <see cref="GlobeStore"/>, one <see cref="GeographicRegion"/> is created per generated area (through EF Core) with its
/// <see cref="GeographicRegionCell"/> rows (through one prepared <see cref="SqliteCommand"/>, not the change tracker),
/// and each settlement is pinned to its globe cell and tile (<see cref="Settlement.TileId"/>) and saved.
/// </para>
/// <para>
/// Atomicity: every write runs in one transaction. When the caller has begun one on <paramref name="db"/>
/// (<c>db.Database.BeginTransactionAsync</c>, as <see cref="SeedImporter"/> does) the writes join it and the caller
/// commits; otherwise this method begins its own and commits it at the end. A failure part-way leaves no world row,
/// globe, natural area or pin behind.
/// </para>
/// <para>
/// Pin seed: <c>DeterministicHash.Combine(worldSeed, 0x50494E53 /* "PINS" */, 0, 0)</c>. The tag differs from
/// <see cref="StageSeed"/>'s "WORLDGEN" tag, so pin tie-breaking is independent of every generation stage.
/// </para>
/// </summary>
public static class GlobeSeeding
{
    private const ulong PinTag = 0x50494E53UL; // "PINS"

    public const string StepGlobeTables = "Saving globe tables";
    public const string StepTileBlocks = "Saving tile blocks";
    public const string StepLakes = "Saving lakes";
    public const string StepRegionLevel = "Saving region level";
    public const string StepAreasAndPins = "Saving areas and pins";

    /// <summary>Share of <see cref="SeedingProgress.Fraction"/> assigned to generation (the rest is writing), from the default-preset run: about 20 s generation, 4 s writing.</summary>
    public const double GenerationShare = 0.85;

    /// <summary>The seed passed to <see cref="PinStage.Run"/> for a world seed.</summary>
    public static ulong PinSeed(ulong worldSeed) => DeterministicHash.Combine(worldSeed, PinTag, 0, 0);

    public static async Task<GlobeSeedingReport> RunAsync(
        WorldDbContext db,
        World world,
        WorldGenSettings settings,
        IReadOnlyList<(Settlement Settlement, PinRequest Request)> pins,
        IReadOnlyList<AreaNameRequest> names,
        IProgress<SeedingProgress>? progress = null,
        CancellationToken ct = default)
    {
        var genProgress = progress is null ? null : new SyncProgress<(WorldGenStage Stage, double Fraction)>(p =>
            progress.Report(new SeedingProgress(p.Stage.ToString(), p.Fraction * GenerationShare)));
        var generated = WorldGenerator.Generate(world.Seed, settings, new WorldGenOptions(Progress: genProgress));
        ct.ThrowIfCancellationRequested();
        var layers = generated.Layers;

        // Lookups first: nothing is written if a pin or an authored name is invalid.
        var pinResults = Unwrap(PinStage.Run(layers, pins.Select(p => p.Request).ToList(), PinSeed(world.Seed), settings.Pinning, generated.Tiles), "pin");
        var areaNames = Unwrap(PinStage.NameAreas(layers, names), "natural area");
        var authoredName = areaNames.ToDictionary(n => n.AreaIndex, n => n.Name);

        await using var ownTx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var sw = Stopwatch.StartNew();
        Unwrap(world.SetGlobe(settings.Grid.Frequency, settings.Grid.PlanetRadiusKm, generated.GeneratorVersion), "world");
        world.SetSeaLevel(layers.SeaLevelM);
        await db.SaveChangesAsync(ct);

        var writeProgress = progress is null ? null : new SyncProgress<GlobeWriteProgress>(p => progress.Report(WriteStep(p)));
        var globeTables = await new GlobeStore(db).WriteAsync(world.Id, settings, generated, writeProgress, ct);
        progress?.Report(new SeedingProgress(StepAreasAndPins, 0.99));

        var regions = new List<(GeographicRegion Region, GeneratedArea Area)>(layers.Areas.Count);
        foreach (var area in layers.Areas)
        {
            var authored = authoredName.TryGetValue(area.Index, out var name);
            var region = Unwrap(GeographicRegion.Create(world.Id, authored ? name! : area.Name, area.Kind, nameAuthored: authored), "natural area");
            db.GeographicRegions.Add(region);
            regions.Add((region, area));
        }
        await db.SaveChangesAsync(ct);
        await WriteRegionCellsAsync(db, regions, ct);

        var cellOf = new Dictionary<SettlementId, (string Name, int Cell)>();
        for (var i = 0; i < pins.Count; i++)
        {
            var settlement = pins[i].Settlement;
            var cell = pinResults[i].Cell;
            var tile = pinResults[i].TileId;
            Unwrap(settlement.PinTo(new GlobeCellId(cell), tile >= 0 ? tile : null), "pin");
            cellOf[settlement.Id] = (settlement.Name, cell);
        }
        await db.SaveChangesAsync(ct);
        sw.Stop();

        var routes = await db.Routes.AsNoTracking().Where(r => r.WorldId == world.Id).ToListAsync(ct);
        var warnings = pinResults.Where(p => p.Warning is not null).Select(p => p.Warning!).ToList();
        warnings.AddRange(RouteWarnings(layers, routes, cellOf));
        if (ownTx is not null)
            await ownTx.CommitAsync(ct);
        return new GlobeSeedingReport(generated, sw.Elapsed, globeTables, warnings);
    }

    /// <summary>
    /// Land components: connected sets of non-ocean cells (breadth-first search over <c>Neighbours</c>, seeded in
    /// cell-id order). Returns the component index per cell, −1 for ocean cells.
    /// </summary>
    public static int[] Landmasses(WorldLayers layers)
    {
        var n = layers.Grid.CellCount;
        var comp = new int[n];
        Array.Fill(comp, -1);
        var queue = new Queue<int>();
        var next = 0;
        for (var start = 0; start < n; start++)
        {
            if (layers.IsOcean(start) || comp[start] >= 0) continue;
            comp[start] = next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var nb in layers.Grid.Neighbours(c))
                {
                    if (layers.IsOcean(nb) || comp[nb] >= 0) continue;
                    comp[nb] = next;
                    queue.Enqueue(nb);
                }
            }
            next++;
        }
        return comp;
    }

    /// <summary>
    /// Warnings for routes that contradict the terrain: a <see cref="RouteCategory.Land"/> route whose two ends lie on
    /// different landmasses, or a <see cref="RouteCategory.ShippingLane"/> with an end that is neither ocean nor next to
    /// ocean. Directed routes listed in both directions give one warning per settlement pair and category. Routes are
    /// visited in (from name, to name) ordinal order; routes with an end not in <paramref name="cellOf"/> are skipped.
    /// </summary>
    public static List<string> RouteWarnings(WorldLayers layers, IEnumerable<Route> routes, Dictionary<SettlementId, (string Name, int Cell)> cellOf)
    {
        var landmass = Landmasses(layers);
        var seen = new HashSet<(SettlementId, SettlementId, RouteCategory)>();
        var warnings = new List<string>();
        var ordered = routes
            .Where(r => cellOf.ContainsKey(r.FromSettlementId) && cellOf.ContainsKey(r.ToSettlementId))
            .OrderBy(r => cellOf[r.FromSettlementId].Name, StringComparer.Ordinal)
            .ThenBy(r => cellOf[r.ToSettlementId].Name, StringComparer.Ordinal);
        foreach (var r in ordered)
        {
            var from = cellOf[r.FromSettlementId];
            var to = cellOf[r.ToSettlementId];
            var key = string.CompareOrdinal(from.Name, to.Name) <= 0
                ? (r.FromSettlementId, r.ToSettlementId, r.Category)
                : (r.ToSettlementId, r.FromSettlementId, r.Category);
            if (!seen.Add(key)) continue;

            if (r.Category == RouteCategory.Land)
            {
                if (landmass[from.Cell] != landmass[to.Cell])
                    warnings.Add($"Land route {from.Name} -> {to.Name}: {from.Name} (cell {from.Cell}) and {to.Name} (cell {to.Cell}) are on different landmasses.");
            }
            else if (r.Category == RouteCategory.ShippingLane)
            {
                var inland = new[] { from, to }.Where(e => !layers.IsOcean(e.Cell) && !HasOceanNeighbour(layers, e.Cell)).ToList();
                if (inland.Count > 0)
                    warnings.Add($"Shipping lane {from.Name} -> {to.Name}: " +
                        string.Join(" and ", inland.Select(e => $"{e.Name} (cell {e.Cell})")) +
                        (inland.Count == 1 ? " is" : " are") + " inland (no ocean neighbour).");
            }
        }
        return warnings;
    }

    private static bool HasOceanNeighbour(WorldLayers layers, int cell)
    {
        foreach (var nb in layers.Grid.Neighbours(cell))
            if (layers.IsOcean(nb)) return true;
        return false;
    }

    private static async Task WriteRegionCellsAsync(WorldDbContext db, List<(GeographicRegion Region, GeneratedArea Area)> regions, CancellationToken ct)
    {
        await using (var scope = await SqliteAmbientTransaction.JoinOrBeginAsync(db, ct))
        {
            await using var cmd = scope.Connection.CreateCommand();
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = "INSERT INTO geographic_region_cells (GeographicRegionId, CellId, CoverageBp) VALUES ($region, $cell, $coverage)";
            var region = cmd.Parameters.Add(new SqliteParameter("$region", null));
            var cell = cmd.Parameters.Add(new SqliteParameter("$cell", null));
            var coverage = cmd.Parameters.Add(new SqliteParameter("$coverage", null));
            cmd.Prepare();
            foreach (var (r, area) in regions)
            {
                region.Value = r.Id.Value;
                foreach (var (c, bp) in area.Cells)
                {
                    // Same invariant as GeographicRegionCell.Create, checked here because rows bypass the domain factory.
                    if (bp < 1 || bp > GeographicRegionCell.MaxCoverageBp)
                        throw new InvalidOperationException($"Natural area '{r.Name}' has coverage {bp} bp on cell {c}; expected 1–10,000.");
                    cell.Value = c;
                    coverage.Value = bp;
                    cmd.ExecuteNonQuery();
                }
            }
            await scope.CommitAsync(ct);
        }
    }

    // Fractions within the writing share (1 − GenerationShare): globe tables start at 0, tile blocks run from 10 % to 90 %,
    // lakes start at 90 %, region level at 92 %; areas and pins are reported at 99 % of the whole run.
    private static SeedingProgress WriteStep(GlobeWriteProgress p)
    {
        var w = 1 - GenerationShare;
        return p.Step switch
        {
            GlobeWriteStep.GlobeTables => new SeedingProgress(StepGlobeTables, GenerationShare),
            GlobeWriteStep.TileBlocks => new SeedingProgress(StepTileBlocks, GenerationShare + w * (0.1 + 0.8 * p.Fraction)),
            GlobeWriteStep.Lakes => new SeedingProgress(StepLakes, GenerationShare + w * 0.9),
            _ => new SeedingProgress(StepRegionLevel, GenerationShare + w * 0.92),
        };
    }

    /// <summary><see cref="IProgress{T}"/> that calls the handler on the reporting thread (no synchronisation context post), so steps arrive in order.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static T Unwrap<T>(ErrorOr<T> result, string context)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Seed invalid ({context}): {string.Join("; ", result.Errors.Select(e => e.Description))}");
        return result.Value;
    }
}
