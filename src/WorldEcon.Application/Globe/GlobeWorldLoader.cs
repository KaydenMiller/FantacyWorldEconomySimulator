using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Globe;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Application.Globe;

/// <summary>A settlement pinned to a globe cell and, for worlds with tiles, to a tile in it (null when not pinned to a tile).</summary>
public sealed record GlobeSettlement(Guid Id, string Name, int Cell, int? TileId = null, PortKind Port = PortKind.None);

/// <summary>
/// A route of the travel graph as the map draws it: its category (Land, Sea, Air), surface, vehicle tier range, length,
/// the road it belongs to, its end names and its tile path from NodeA to NodeB.
/// </summary>
public sealed record GlobeRoute(Guid Id, RouteCategory Category, RouteSurface Surface, int MinTier, int MaxTier, long LengthMetres,
    long TrafficCount, string RoadName, string NodeA, string NodeB, IReadOnlyList<int> Tiles);

/// <summary>Everything a globe viewer needs from one database, loaded once (see <see cref="GlobeWorldLoader.LoadAllAsync"/>).</summary>
/// <param name="Regions">Region-cell summary.</param>
/// <param name="Lakes">Lakes and dry lake beds, ordered by id.</param>
/// <param name="TileGrid">Tile grid of the world (tile ids, centres, <see cref="TileGrid.Nearest"/>).</param>
/// <param name="Tiles">
/// On-demand tile block loads (<see cref="SqliteTileBlockSource"/> over the database file); safe to call from several
/// worker threads at once. Tiles are not held in memory.
/// </param>
public sealed record LoadedGlobe(
    World World,
    WorldLayers Layers,
    WorldGenSettings Settings,
    int? GeneratorVersion,
    IReadOnlyList<GlobeSettlement> Settlements,
    RegionSummary Regions,
    IReadOnlyList<TileLake> Lakes,
    TileGrid TileGrid,
    ITileBlockSource Tiles)
{
    /// <summary>Routes of the travel graph with their tile paths, ordered by road name then route id; empty for a world without roads.</summary>
    public IReadOnlyList<GlobeRoute> Routes { get; init; } = [];

    /// <summary>Stored map imagery; null for a world written before it existed.</summary>
    public CellImagery? Imagery { get; init; }

    /// <summary>Stored major river courses at tile resolution; null for a world written before they existed.</summary>
    public RiverCourses? RiverCourses { get; init; }

    /// <summary>
    /// A loaded globe over an in-memory generation (tests, tools): tiles come from an <see cref="InMemoryTileBlockSource"/>.
    /// <paramref name="generated"/> must be the output of <see cref="WorldGenerator.Generate"/>.
    /// </summary>
    public static LoadedGlobe FromGenerated(World world, GeneratedWorld generated, WorldGenSettings settings, IReadOnlyList<GlobeSettlement> settlements)
    {
        ArgumentNullException.ThrowIfNull(generated);
        var tiles = generated.Tiles ?? throw new ArgumentException("The generation has no tiles (GenerateGlobe output).", nameof(generated));
        var regions = generated.Regions ?? throw new ArgumentException("The generation has no region summary.", nameof(generated));
        return new LoadedGlobe(world, generated.Layers, settings, generated.GeneratorVersion, settlements, regions, generated.Lakes,
            tiles.Grid, new InMemoryTileBlockSource(tiles))
        { Imagery = generated.Imagery, RiverCourses = generated.RiverCourses };
    }
}

/// <summary>
/// Loads a stored globe: the first world of a database, its layers, region summary, lakes and settings
/// (<see cref="GlobeStore.LoadAsync"/>; tiles stay in the database and load on demand), its natural areas rebuilt as
/// <see cref="GeneratedArea"/>s and its pinned settlements. Shared by the CLI <c>globe</c> commands and the desktop app.
/// </summary>
public static class GlobeWorldLoader
{
    /// <summary>Opens a SQLite database file (it must exist).</summary>
    public static WorldDbContext Open(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new InvalidOperationException($"Database '{dbPath}' does not exist.");
        return new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={dbPath}").Options);
    }

    /// <summary>The first world and its layers; areas are filled when <paramref name="withAreas"/>.</summary>
    public static async Task<(World World, WorldLayers Layers)> LoadLayersAsync(WorldDbContext ctx, bool withAreas, CancellationToken ct = default)
    {
        var world = await ctx.Worlds.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The database contains no world.");
        if (world.GridFrequency <= 0)
            throw new InvalidOperationException($"World '{world.Name}' has no generated globe.");
        var layers = await new GlobeStore(ctx).LoadLayersAsync(world.Id, ct);
        if (withAreas)
            layers.Areas = await LoadAreasAsync(ctx, world.Id, ct);
        return (world, layers);
    }

    /// <summary>Rebuilds <see cref="GeneratedArea"/> instances from the region rows, indexed in (kind, smallest member cell) order.</summary>
    public static async Task<List<GeneratedArea>> LoadAreasAsync(WorldDbContext ctx, WorldId worldId, CancellationToken ct = default)
    {
        var regions = await ctx.GeographicRegions.AsNoTracking().Where(r => r.WorldId == worldId).ToListAsync(ct);
        var members = await ctx.GeographicRegionCells.AsNoTracking()
            .Join(ctx.GeographicRegions.Where(r => r.WorldId == worldId), m => m.GeographicRegionId, r => r.Id, (m, _) => m)
            .ToListAsync(ct);
        var byRegion = members.GroupBy(m => m.GeographicRegionId).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Cell.Value).Select(m => (m.Cell.Value, m.CoverageBp)).ToList());
        var ordered = regions
            .Select(r => (Region: r, Cells: byRegion.GetValueOrDefault(r.Id) ?? []))
            .OrderBy(x => x.Region.Kind)
            .ThenBy(x => x.Cells.Count == 0 ? int.MaxValue : x.Cells[0].Value)
            .ToList();
        var areas = new List<GeneratedArea>(ordered.Count);
        foreach (var (region, cells) in ordered)
            areas.Add(new GeneratedArea(areas.Count, region.Kind, region.Name, cells));
        return areas;
    }

    /// <summary>Settlements pinned to a cell, ordered by name (ordinal) then id.</summary>
    public static async Task<List<GlobeSettlement>> LoadSettlementsAsync(WorldDbContext ctx, WorldId worldId, CancellationToken ct = default)
    {
        var rows = await ctx.Settlements.AsNoTracking().Where(s => s.WorldId == worldId && s.CellId != null).ToListAsync(ct);
        var ports = (await ctx.RouteNodes.AsNoTracking().Where(n => n.WorldId == worldId && n.SettlementId != null).ToListAsync(ct))
            .GroupBy(n => n.SettlementId!.Value).ToDictionary(g => g.Key, g => g.First().PortKind);
        return rows.Select(s => new GlobeSettlement(s.Id.Value, s.Name, s.CellId!.Value.Value, s.TileId, ports.GetValueOrDefault(s.Id)))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id).ToList();
    }

    /// <summary>Routes of the world with their tile paths (see <see cref="GlobeRoute"/>).</summary>
    public static async Task<List<GlobeRoute>> LoadRoutesAsync(WorldDbContext ctx, WorldId worldId, CancellationToken ct = default)
    {
        var nodes = await ctx.RouteNodes.AsNoTracking().Where(n => n.WorldId == worldId).ToDictionaryAsync(n => n.Id, ct);
        if (nodes.Count == 0) return [];
        var routes = await ctx.Routes.AsNoTracking().Where(r => r.WorldId == worldId).ToListAsync(ct);
        var roads = await ctx.Roads.AsNoTracking().Where(r => r.WorldId == worldId).ToListAsync(ct);
        var roadOf = roads.SelectMany(r => r.Routes.Select(e => (e.RouteId, r.Name))).ToDictionary(x => x.RouteId, x => x.Name);
        var ids = routes.Select(r => r.Id).ToHashSet();
        var tiles = (await ctx.RouteTiles.AsNoTracking().ToListAsync(ct)).Where(t => ids.Contains(t.RouteId))
            .GroupBy(t => t.RouteId).ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.OrderBy(t => t.Sequence).Select(t => t.TileId).ToArray());
        foreach (var v in await ctx.VoyagePaths.AsNoTracking().ToListAsync(ct))
            if (ids.Contains(v.RouteId)) tiles[v.RouteId] = v.Decode();
        string Name(RouteNodeId id) => nodes.TryGetValue(id, out var n) ? n.Name ?? $"{n.Kind} @{n.TileId}" : "?";
        return routes.Select(r => new GlobeRoute(r.Id.Value, r.Category, r.Surface, r.MinTier, r.MaxTier, r.LengthMetres, r.TrafficCount,
                roadOf.GetValueOrDefault(r.Id, r.Category == RouteCategory.Sea ? "Voyage" : ""), Name(r.NodeA), Name(r.NodeB), tiles.GetValueOrDefault(r.Id, [])))
            .OrderBy(r => r.RoadName, StringComparer.Ordinal).ThenBy(r => r.Id).ToList();
    }

    /// <summary>
    /// The first world and everything <see cref="GlobeStore.LoadAsync"/> returns, with areas filled. Throws
    /// <see cref="GlobeGeneratorVersionException"/> for a world from an older or newer generator version.
    /// </summary>
    public static async Task<(World World, StoredGlobe Globe)> LoadStoredAsync(WorldDbContext ctx, CancellationToken ct = default)
    {
        var world = await ctx.Worlds.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The database contains no world.");
        if (world.GridFrequency <= 0)
            throw new InvalidOperationException($"World '{world.Name}' has no generated globe.");
        var stored = await new GlobeStore(ctx).LoadAsync(world.Id, ct);
        stored.Layers.Areas = await LoadAreasAsync(ctx, world.Id, ct);
        return (world, stored);
    }

    /// <summary>Loads the world, layers with areas, region summary, lakes, settlements and recorded settings from a database file; tiles load on demand through <see cref="LoadedGlobe.Tiles"/>.</summary>
    public static async Task<LoadedGlobe> LoadAllAsync(string dbPath, CancellationToken ct = default)
    {
        await using var ctx = Open(dbPath);
        var (world, stored) = await LoadStoredAsync(ctx, ct);
        var settlements = await LoadSettlementsAsync(ctx, world.Id, ct);
        var routes = await LoadRoutesAsync(ctx, world.Id, ct);
        return new LoadedGlobe(world, stored.Layers, stored.Settings, stored.GeneratorVersion, settlements,
            stored.Regions, stored.Lakes, stored.TileGrid, new SqliteTileBlockSource(dbPath))
        { Imagery = stored.Imagery, RiverCourses = stored.RiverCourses, Routes = routes };
    }
}
