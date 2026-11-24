using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.Application.Globe;
using WorldEcon.Persistence.Globe;
using WorldEcon.Rendering;
using WorldEcon.Seeding;
using WorldEcon.SharedKernel.Globe;
using System.Diagnostics;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Queries;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Cli;

/// <summary>The <c>globe</c> command family: statistics, cell and tile inspection, area listing, PNG rendering, validation queries.</summary>
internal static class GlobeCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string TileUsage = "globe tile <dbPath> <tileId> | globe tile <dbPath> --at <lat>,<lon>";

    public static async Task<int> RunAsync(string[] args, Func<string, WorldDbContext> open, Func<string, int> missingArgs)
    {
        const string statsUsage = "globe <dbPath> | globe cell <dbPath> <cellId> | globe tile <dbPath> <tileId>|--at <lat>,<lon> | globe areas <dbPath> [kind] | globe png <dbPath> <layer> <path> [--yaw D] [--pitch D] [--size N] [--zoom Z] [--level globe|region|detail] [--graticule] [--no-equator] [--river-arrows] [--river-ends] [--hex-grid] | globe find <dbPath> wasatch | globe defaults | globe settings <dbPath>";
        if (args.Length < 2)
            return missingArgs(statsUsage);

        switch (args[1].ToLowerInvariant())
        {
            case "cell":
                return args.Length < 4 ? missingArgs("globe cell <dbPath> <cellId>") : await Cell(open, args[2], args[3]);
            case "tile":
                return args.Length < 4 ? missingArgs(TileUsage) : await Tile(open, args);
            case "areas":
                return args.Length < 3 ? missingArgs("globe areas <dbPath> [kind]") : await Areas(open, args[2], args.Length > 3 ? args[3] : null);
            case "png":
                return args.Length < 5 ? missingArgs("globe png <dbPath> <layer> <path> [--yaw D] [--pitch D] [--size N] [--zoom Z] [--level globe|region|detail] [--graticule] [--no-equator] [--river-arrows] [--river-ends] [--hex-grid]") : await Png(open, args);
            case "find":
                return args.Length < 4 ? missingArgs("globe find <dbPath> wasatch") : await Find(open, args[2], args[3]);
            case "defaults":
                Console.WriteLine(WorldGenSettingsJson.Serialize(new WorldGenSettings(), indented: true));
                return 0;
            case "settings":
                return args.Length < 3 ? missingArgs("globe settings <dbPath>") : await Settings(open, args[2]);
            default:
                return await Stats(open, args[1]);
        }
    }

    private sealed record Loaded(WorldDbContext Ctx, World World, WorldLayers Layers);

    private static async Task<Loaded> LoadAsync(Func<string, WorldDbContext> open, string path, bool withAreas)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Database '{path}' does not exist.");
        var ctx = open(path);
        var (world, layers) = await GlobeWorldLoader.LoadLayersAsync(ctx, withAreas);
        return new Loaded(ctx, world, layers);
    }

    private static string Coord(WorldLayers l, int cell)
    {
        var (lat, lon) = l.Geometry.LatLon(cell);
        return $"{lat.ToString("F2", Inv)}, {lon.ToString("F2", Inv)}";
    }

    private static string Comp(WorldLayers l, int cell)
        => string.Join(" ", l.Composition[cell].Select(e => $"{e.Landform}:{e.ShareBp}"));

    // ---- globe <db> ----
    private static async Task<int> Stats(Func<string, WorldDbContext> open, string path)
    {
        var (ctx, world, l) = await LoadAsync(open, path, withAreas: true);
        await using var _ = ctx;
        var n = l.Grid.CellCount;
        var pentagons = Enumerable.Range(0, n).Count(l.Grid.IsPentagon);

        Console.WriteLine($"World '{world.Name}' (seed {world.Seed})");
        Console.WriteLine($"  Cells:        {n} ({pentagons} pentagons), frequency {l.Grid.Frequency}, radius {world.PlanetRadiusKm} km");
        await PrintTileStats(ctx, world);
        Console.WriteLine($"  Plates:       {l.Plates.Length} ({l.Plates.Count(p => p.Continental)} continental)");
        Console.WriteLine($"  Land:         {GlobeQueries.LandPercent(l).ToString("F1", Inv)} % (area-weighted); sea level {l.SeaLevelM} m");
        Console.WriteLine($"  Elevation m:  all cells: min {l.ElevationM.Min()}, p10 {GlobeQueries.Percentile(l.ElevationM, 10)}, median {GlobeQueries.Percentile(l.ElevationM, 50)}, p90 {GlobeQueries.Percentile(l.ElevationM, 90)}, max {l.ElevationM.Max()}");
        var landElevation = Enumerable.Range(0, n).Where(c => !l.IsOcean(c)).Select(c => l.ElevationM[c] - l.SeaLevelM).ToArray();
        Console.WriteLine(landElevation.Length == 0
            ? "  Land elev m:  no land cells"
            : $"  Land elev m:  above sea level, land cells only: min {landElevation.Min()}, p10 {GlobeQueries.Percentile(landElevation, 10)}, median {GlobeQueries.Percentile(landElevation, 50)}, p90 {GlobeQueries.Percentile(landElevation, 90)}, max {landElevation.Max()}");
        var temp = l.TemperatureDeciC.Select(t => (int)t).ToArray();
        Console.WriteLine($"  Temperature:  min {temp.Min() / 10.0:F1}, median {GlobeQueries.Percentile(temp, 50) / 10.0:F1}, max {temp.Max() / 10.0:F1} C");
        Console.WriteLine($"  Precip mm/yr: min {l.PrecipitationMm.Min()}, median {GlobeQueries.Percentile(l.PrecipitationMm, 50)}, max {l.PrecipitationMm.Max()}");

        Console.WriteLine("  Biomes:       " + string.Join(", ", l.BiomeOf
            .GroupBy(b => (Biome)b).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
        var lakes = l.WaterBodyKind.Count(w => w == (byte)WaterBodyKind.Lake);
        var dry = l.WaterBodyKind.Count(w => w == (byte)WaterBodyKind.DryLakeBed);
        var riverKm = GlobeQueries.RiverLengthKm(l, world.PlanetRadiusKm);
        Console.WriteLine($"  Rivers:       {l.RiverEdges.Count} edges, {riverKm.ToString("N0", Inv)} km (great-circle, centre to centre)");
        Console.WriteLine($"  Lakes:        {GlobeQueries.WaterBodies(l, WaterBodyKind.Lake)} lakes ({lakes} cells); {GlobeQueries.WaterBodies(l, WaterBodyKind.DryLakeBed)} dry lake beds ({dry} cells)");
        Console.WriteLine("  Areas:        " + (l.Areas.Count == 0 ? "none"
            : string.Join(", ", l.Areas.GroupBy(a => a.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))));

        var settlements = await ctx.Settlements.AsNoTracking().Where(s => s.WorldId == world.Id && s.CellId != null).OrderBy(s => s.Name).ToListAsync();
        Console.WriteLine($"  Settlements:  {settlements.Count} pinned");
        foreach (var s in settlements)
        {
            var c = s.CellId!.Value.Value;
            Console.WriteLine($"    {s.Name} -> cell {c} ({Coord(l, c)}) {(Biome)l.BiomeOf[c]}");
        }

        var routes = await ctx.Routes.AsNoTracking().Where(r => r.WorldId == world.Id).ToListAsync();
        var roads = await ctx.Roads.AsNoTracking().Where(r => r.WorldId == world.Id).ToListAsync();
        Console.WriteLine($"  Routes:       {routes.Count} in {roads.Count} roads, {await ctx.RouteNodes.CountAsync(n => n.WorldId == world.Id)} nodes" +
            (routes.Count == 0 ? "" : " (" + string.Join(", ", routes.GroupBy(r => r.Category).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Count()}, {(g.Sum(r => r.LengthMetres) / 1000.0).ToString("N0", Inv)} km")) + ")"));

        var run = await ctx.Set<WorldGenRunRow>().AsNoTracking().FirstOrDefaultAsync(r => r.WorldId == world.Id);
        if (run is null)
        {
            Console.WriteLine("  Stages:       no worldgen run recorded");
            return 0;
        }
        var stages = JsonSerializer.Deserialize<List<WorldGenStageRecord>>(run.StagesJson) ?? [];
        Console.WriteLine($"  Stages (generator version {run.GeneratorVersion}):");
        foreach (var s in stages)
            Console.WriteLine($"    {s.Stage,-10} {TimeSpan.FromTicks(s.DurationTicks).TotalSeconds,8:F3} s  hash {s.Hash:X16}");
        Console.WriteLine($"  Stage total:  {TimeSpan.FromTicks(stages.Sum(s => s.DurationTicks)).TotalSeconds:F3} s");
        return 0;
    }

    // Tile grid size, stored tile counts and lakes (settings, lakes and the tile_blocks counts; no block is decoded).
    private static async Task PrintTileStats(WorldDbContext ctx, World world)
    {
        var store = new GlobeStore(ctx);
        var settings = await store.LoadSettingsAsync(world.Id);
        if (settings is null)
        {
            Console.WriteLine("  Tiles:        no worldgen run recorded");
            return;
        }

        var g = settings.Grid;
        var regionGrid = new TileGrid(g.Frequency * g.RegionMultiplier);
        var tileGrid = new TileGrid(regionGrid.Frequency * g.TileMultiplier);
        var stored = await ctx.TileBlocks.AsNoTracking().GroupBy(_ => 1)
            .Select(x => new { Blocks = x.Count(), Tiles = x.Sum(r => (long)r.TileCount) }).SingleOrDefaultAsync();
        var lakes = await store.LoadLakesAsync();
        var lakeCount = lakes.Count(k => k.Kind == TileWaterKind.Lake);
        var largest = lakes.Where(k => k.Kind == TileWaterKind.Lake).OrderByDescending(k => k.AreaKm2).ThenBy(k => k.Id).FirstOrDefault();
        Console.WriteLine($"  Regions:      {regionGrid.Count:N0} region cells, frequency {regionGrid.Frequency} (x {g.RegionMultiplier})");
        Console.WriteLine($"  Tiles:        {tileGrid.Count:N0} tiles, frequency {tileGrid.Frequency} (x {g.TileMultiplier}), tile size " +
            $"{TileMetrics.SizeKm(world.PlanetRadiusKm, tileGrid.Count).ToString("F2", Inv)} km; stored {stored?.Tiles ?? 0:N0} tiles in {stored?.Blocks ?? 0:N0} blocks");
        Console.WriteLine($"  Tile lakes:   {lakeCount:N0} lakes, {lakes.Count - lakeCount:N0} dry lake beds" + (largest is null ? "" :
            $"; largest lake {largest.Id}: {largest.AreaKm2.ToString("N0", Inv)} km2, {largest.TileCount:N0} tiles, water level {largest.WaterLevelM} m"));
    }

    // ---- globe tile <db> <tileId> | --at <lat>,<lon> ----
    private static async Task<int> Tile(Func<string, WorldDbContext> open, string[] args)
    {
        var path = args[2];
        if (!File.Exists(path))
            throw new InvalidOperationException($"Database '{path}' does not exist.");
        await using var ctx = open(path);
        var world = await ctx.Worlds.AsNoTracking().FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("The database contains no world.");
        var store = new GlobeStore(ctx);
        var settings = await store.LoadSettingsAsync(world.Id)
            ?? throw new InvalidOperationException($"World '{world.Name}' has no recorded generation run.");
        var g = settings.Grid;
        var globeGrid = new TileGrid(g.Frequency);
        var regionGrid = new TileGrid(g.Frequency * g.RegionMultiplier);
        var tileGrid = new TileGrid(regionGrid.Frequency * g.TileMultiplier);

        int tile;
        if (args[3] == "--at")
        {
            if (args.Length != 5)
                throw new InvalidOperationException($"--at needs <lat>,<lon>. Usage: {TileUsage}");
            var parts = args[4].Split(',');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, Inv, out var lat) || lat is < -90 or > 90
                || !double.TryParse(parts[1], NumberStyles.Float, Inv, out var lon) || lon is < -180 or > 180)
                throw new InvalidOperationException($"--at expects <lat>,<lon> in degrees (lat -90..90, lon -180..180), not '{args[4]}'.");
            tile = tileGrid.Nearest(UnitVector(lat, lon));
        }
        else
        {
            if (args.Length != 4)
                throw new InvalidOperationException($"Unexpected arguments. Usage: {TileUsage}");
            if (!int.TryParse(args[3], NumberStyles.None, Inv, out tile) || tile >= tileGrid.Count)
                throw new InvalidOperationException($"Tile id must be an integer in 0..{tileGrid.Count - 1}.");
        }

        var cell = GridHierarchy.Parent(tileGrid, globeGrid, tile);
        var region = GridHierarchy.Parent(tileGrid, regionGrid, tile);
        var block = await store.LoadTileBlockAsync(cell)
            ?? throw new InvalidOperationException($"No tile block is stored for globe cell {cell}; regenerate the world.");
        var i = Array.BinarySearch(block.TileIds, tile);
        if (i < 0)
            throw new InvalidOperationException($"Tile {tile} is not in the stored block of its globe cell {cell}.");

        var (tLat, tLon) = LatLon(tileGrid.Centre(tile));
        var water = (TileWaterKind)block.Water[i];
        Console.WriteLine($"Tile {tile}{(tileGrid.IsPentagon(tile) ? " (pentagon)" : "")} at {tLat.ToString("F4", Inv)}, {tLon.ToString("F4", Inv)}");
        Console.WriteLine($"  Globe cell:   {cell}");
        Console.WriteLine($"  Region cell:  {region}");
        Console.WriteLine($"  Area:         {tileGrid.AreaKm2(tile, world.PlanetRadiusKm).ToString("F1", Inv)} km2");
        Console.WriteLine($"  Elevation:    {block.ElevationM[i]} m (sea level {world.SeaLevelM} m)");
        Console.WriteLine($"  Landform:     {(Landform)block.Landform[i]}");
        Console.WriteLine($"  Biome:        {(Biome)block.Biome[i]}");
        Console.WriteLine($"  Water:        {water}");
        var lakeId = block.LakeId[i];
        if (lakeId >= 0)
        {
            var lake = (await store.LoadLakesAsync()).FirstOrDefault(k => k.Id == lakeId);
            Console.WriteLine(lake is null
                ? $"  Lake:         {lakeId} (no lakes row)"
                : $"  Lake:         {lakeId} ({lake.Kind}), water level {lake.WaterLevelM} m, {lake.TileCount:N0} tiles, {lake.AreaKm2.ToString("N0", Inv)} km2");
        }
        else
        {
            Console.WriteLine("  Lake:         none");
        }

        var slot = block.Downstream[i];
        string downstream = "none";
        if (slot >= 0)
        {
            Span<int> ring = stackalloc int[6];
            var count = tileGrid.Neighbours(tile, ring);
            downstream = slot < count ? $"tile {ring[slot]} (slot {slot})" : $"slot {slot} (outside the ring of {count})";
        }

        Console.WriteLine($"  Flow:         {block.Flow[i].ToString("N0", Inv)} mm·km²/yr, downstream {downstream}");
        Console.WriteLine($"  Temperature:  {(block.TemperatureDeciC[i] / 10.0).ToString("F1", Inv)} C");
        Console.WriteLine($"  Precipitation:{block.PrecipitationMm[i],6} mm/yr");
        var here = (await GlobeWorldLoader.LoadSettlementsAsync(ctx, world.Id)).Where(st => st.TileId == tile).Select(st => st.Name).ToList();
        Console.WriteLine("  Settlements:  " + (here.Count == 0 ? "none" : string.Join(", ", here)));
        return 0;
    }

    private static Vector3d UnitVector(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        return new Vector3d(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }

    // Same convention as GlobeGeometry.LatLon: latitude from Y, longitude atan2(X, Z).
    private static (double LatDeg, double LonDeg) LatLon(Vector3d v) =>
        (Math.Asin(Math.Clamp(v.Y, -1, 1)) * 180 / Math.PI, Math.Atan2(v.X, v.Z) * 180 / Math.PI);

    // ---- globe cell <db> <cellId> ----
    private static async Task<int> Cell(Func<string, WorldDbContext> open, string path, string cellText)
    {
        var (ctx, world, l) = await LoadAsync(open, path, withAreas: true);
        await using var _ = ctx;
        if (!int.TryParse(cellText, NumberStyles.None, Inv, out var c) || c >= l.Grid.CellCount)
            throw new InvalidOperationException($"Cell id must be an integer in 0..{l.Grid.CellCount - 1}.");

        var d = GlobeCellDetails.Build(l, c, await GlobeWorldLoader.LoadSettlementsAsync(ctx, world.Id));
        Console.WriteLine($"Cell {c}{(d.Pentagon ? " (pentagon)" : "")} at {Coord(l, c)}");
        Console.WriteLine($"  Plate:        {d.PlateId} ({(d.PlateContinental ? "continental" : "oceanic")}), boundary {d.Boundary}");
        Console.WriteLine($"  Elevation:    {d.ElevationM} m ({(d.Ocean ? "ocean" : "land")}, sea level {d.SeaLevelM} m)");
        Console.WriteLine($"  Current:      slot {d.CurrentSlot}, anomaly {d.CurrentAnomalyDeciC / 10.0:F1} C");
        Console.WriteLine($"  Wind:         slot {d.WindSlot}, strength {d.WindStrength}");
        Console.WriteLine($"  Temperature:  {d.TemperatureDeciC / 10.0:F1} C");
        Console.WriteLine($"  Precipitation:{d.PrecipitationMm,6} mm/yr");
        Console.WriteLine($"  Biome:        {d.Biome}");
        Console.WriteLine($"  Composition:  {Comp(l, c)}");
        Console.WriteLine($"  Downstream:   {d.DownstreamOf}, flow {d.Flow}, water {d.Water}");
        Console.WriteLine($"  River edges:  {(d.RiverEdges.Count == 0 ? "none" : string.Join(", ", d.RiverEdges.Select(e => $"{e.From}->{e.To}")))}");
        Console.WriteLine("  Neighbours:   " + string.Join(", ", d.Neighbours.Select((nb, slot) => $"[{slot}]{nb}")));
        Console.WriteLine("  Settlements:  " + (d.Settlements.Count == 0 ? "none" : string.Join(", ", d.Settlements)));
        Console.WriteLine("  Areas:");
        if (d.Areas.Count == 0) Console.WriteLine("    none");
        foreach (var a in d.Areas)
            Console.WriteLine($"    {a.Kind} '{a.Name}' coverage {a.CoverageBp} bp");
        return 0;
    }

    // ---- globe areas <db> [kind] ----
    private static async Task<int> Areas(Func<string, WorldDbContext> open, string path, string? kindText)
    {
        var (ctx, world, _) = await LoadAsync(open, path, withAreas: false);
        await using var __ = ctx;
        GeographicRegionKind? kind = null;
        if (kindText is not null)
        {
            if (!Enum.TryParse<GeographicRegionKind>(kindText, ignoreCase: true, out var k) || !Enum.IsDefined(k))
                throw new InvalidOperationException($"Unknown area kind '{kindText}'. Kinds: {string.Join(", ", Enum.GetNames<GeographicRegionKind>())}.");
            kind = k;
        }
        var rows = await ctx.GeographicRegions.AsNoTracking()
            .Where(r => r.WorldId == world.Id && (kind == null || r.Kind == kind))
            .Select(r => new { r.Id, r.Name, r.Kind, r.NameAuthored })
            .ToListAsync();
        var counts = await ctx.GeographicRegionCells.AsNoTracking()
            .GroupBy(m => m.GeographicRegionId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count);
        Console.WriteLine($"{"Kind",-14} {"Name",-32} {"Cells",6}  Authored");
        foreach (var r in rows.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal))
            Console.WriteLine($"{r.Kind,-14} {r.Name,-32} {counts.GetValueOrDefault(r.Id),6}  {(r.NameAuthored ? "yes" : "no")}");
        Console.WriteLine($"{rows.Count} areas");
        return 0;
    }

    // ---- globe png <db> <layer> <path> ----
    private static async Task<int> Png(Func<string, WorldDbContext> open, string[] args)
    {
        var dbPath = args[2];
        if (!Enum.TryParse<GlobeLayer>(args[3], ignoreCase: true, out var layer) || !Enum.IsDefined(layer))
            throw new InvalidOperationException($"Unknown layer '{args[3]}'. Layers: {string.Join(", ", Enum.GetNames<GlobeLayer>())}.");
        var outPath = args[4];
        double yaw = 0, pitch = 0, zoom = 1;
        MapLevel? level = null;
        var size = 1024;
        bool equator = true, graticule = false, riverArrows = false, riverEnds = false, hexGrid = false, routes = true;
        bool landRoutes = true, seaRoutes = true, airRoutes = true;
        string? centreOn = null;
        for (var i = 5; i < args.Length; i++)
        {
            if (args[i] == "--graticule") { graticule = true; continue; }
            if (args[i] == "--no-equator") { equator = false; continue; }
            if (args[i] == "--river-arrows") { riverArrows = true; continue; }
            if (args[i] == "--river-ends") { riverEnds = true; continue; }
            if (args[i] == "--hex-grid") { hexGrid = true; continue; }
            if (args[i] == "--no-routes") { routes = false; continue; }
            if (args[i] == "--no-land-routes") { landRoutes = false; continue; }
            if (args[i] == "--no-sea-routes") { seaRoutes = false; continue; }
            if (args[i] == "--no-air-routes") { airRoutes = false; continue; }
            if (i + 1 >= args.Length)
                throw new InvalidOperationException($"Option {args[i]} needs a value.");
            var v = args[i + 1];
            switch (args[i])
            {
                case "--yaw" when double.TryParse(v, NumberStyles.Float, Inv, out var y): yaw = y; break;
                case "--pitch" when double.TryParse(v, NumberStyles.Float, Inv, out var p): pitch = p; break;
                case "--size" when int.TryParse(v, NumberStyles.None, Inv, out var s) && s is >= 64 and <= 8192: size = s; break;
                case "--zoom" when double.TryParse(v, NumberStyles.Float, Inv, out var z) && z >= 1: zoom = z; break;
                case "--centre": centreOn = v; break;
                case "--level" when Enum.TryParse<MapLevel>(v, ignoreCase: true, out var lv) && Enum.IsDefined(lv): level = lv; break;
                default: throw new InvalidOperationException($"Invalid option or value: {args[i]} {v}");
            }
            i++;
        }

        if (!File.Exists(dbPath))
            throw new InvalidOperationException($"Database '{dbPath}' does not exist.");
        // MapRenderer at the view: --zoom multiplies the whole-globe scale; --level sets the scale where that level starts
        // (region cell 45 px, tile 24 px) times --zoom.
        var g = await GlobeWorldLoader.LoadAllAsync(dbPath);
        var mapWorld = new MapWorld(g.Layers, g.Regions, g.Lakes, g.Settings.Grid.TileMultiplier, g.Tiles, g.Settings.Tiles.RiverMinFlow, g.Settings.Tiles.MajorRiverFlow, g.Settings.Hydrology)
        { Imagery = g.Imagery, RiverCourses = g.RiverCourses };
        using var renderer = new MapRenderer(mapWorld);
        var view = mapWorld.ViewFromYawPitch(size, size, yaw, pitch, zoom);
        if (centreOn is not null)
        {
            var s = g.Settlements.FirstOrDefault(x => x.Name.Equals(centreOn, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No settlement named '{centreOn}'.");
            var c = s.TileId is { } t ? mapWorld.Tiles!.Centre(t) : g.Layers.Geometry.Centre(s.Cell);
            view = mapWorld.View(c, 0.45 * size * zoom, size, size);
        }
        if (level is { } target && target != MapLevel.Globe)
            view = view.WithScale((target == MapLevel.Region ? mapWorld.ScaleForRegionPx(45) : mapWorld.ScaleForTilePx(24)) * zoom);
        var markers = g.Settlements.Select(s => new MapMarker(s.Cell, s.TileId ?? -1, s.Name, (MapPortKind)(int)s.Port)).ToList();
        var routeLines = g.Routes.Where(r => r.Tiles.Count >= 2).Select(r => new MapRoute(r.Id, (MapRouteKind)(int)r.Category, r.Tiles)).ToList();
        var png = GlobePngRenderer.RenderMap(renderer, view, new MapDrawOptions { Layer = layer, Markers = markers, Equator = equator, Graticule = graticule, RiverArrows = riverArrows, RiverEnds = riverEnds, HexGrid = hexGrid, Routes = routes, LandRoutes = landRoutes, SeaRoutes = seaRoutes, AirRoutes = airRoutes, RouteLines = routeLines });
        await File.WriteAllBytesAsync(outPath, png);
        Console.WriteLine($"Wrote {layer} ({size}x{size}, yaw {yaw.ToString(Inv)}, pitch {pitch.ToString(Inv)}, {renderer.LastLevel} view, scale {view.ScalePxPerRad.ToString("F0", Inv)} px/rad, {markers.Count} markers, {(routes ? routeLines.Count : 0)} routes) to {outPath} ({png.Length} bytes)");
        return 0;
    }

    // ---- globe settings <db> ----
    private static async Task<int> Settings(Func<string, WorldDbContext> open, string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Database '{path}' does not exist.");
        await using var ctx = open(path);
        var world = await ctx.Worlds.AsNoTracking().FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("The database contains no world.");
        var settings = await new GlobeStore(ctx).LoadSettingsAsync(world.Id)
            ?? throw new InvalidOperationException($"World '{world.Name}' has no recorded generation run.");
        Console.WriteLine(WorldGenSettingsJson.Serialize(settings, indented: true));
        return 0;
    }

    // ---- globe find <db> wasatch ----
    private static async Task<int> Find(Func<string, WorldDbContext> open, string path, string what)
    {
        if (!what.Equals("wasatch", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown query '{what}'. Queries: wasatch.");
        var (ctx, _, l) = await LoadAsync(open, path, withAreas: false);
        await using var __ = ctx;
        var cells = GlobeQueries.WasatchCells(l);
        foreach (var c in cells)
            Console.WriteLine($"cell {c} ({Coord(l, c)}) {Comp(l, c)}");
        Console.WriteLine($"{cells.Count} cells");
        return 0;
    }
}
