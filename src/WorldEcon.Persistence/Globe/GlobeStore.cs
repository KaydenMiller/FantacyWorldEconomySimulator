using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Persistence.Globe;

/// <summary>
/// Bulk write and load of a generated world. <see cref="WriteAsync"/> stores the per-cell layer table
/// (<c>globe_cells</c>), neighbour rows, plates, composition, river edges, the <c>worldgen_runs</c> record, the tile
/// blocks, the lakes and the region level in one transaction (the context's active EF Core (Entity Framework Core) transaction when there is one, else its own; see
/// <see cref="SqliteAmbientTransaction"/>), through one prepared parameterised <see cref="SqliteCommand"/> per table (one row per
/// execution); rows do not pass through the EF Core change tracker.
/// <para>
/// Natural areas (<see cref="WorldLayers.Areas"/>) are not written here: seeding stores them as
/// <see cref="GeographicRegion"/> and <see cref="GeographicRegionCell"/> rows, so <see cref="LoadAsync"/>
/// returns an empty <see cref="WorldLayers.Areas"/>.
/// </para>
/// <para>
/// Grid frequency and sea level are read from the <see cref="World"/> row. The caller records them with
/// <see cref="World.SetGlobe"/> and <see cref="World.SetSeaLevel"/> and saves the world before writing;
/// <see cref="WriteAsync"/> throws if the row does not match the generation. Geometry is never stored:
/// <see cref="LoadAsync"/> rebuilds <see cref="GlobeGrid"/> and <see cref="GlobeGeometry"/> from the frequency.
/// </para>
/// </summary>
public sealed class GlobeStore(WorldDbContext db) : ITileBlockSource
{
    // A DbContext is not safe for concurrent use, so tile block loads through this store are serialised. Callers that load
    // blocks from several threads at once (the map renderer) use SqliteTileBlockSource, which opens one connection per load.
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>
    /// Inserts one <c>tile_blocks</c> row per block, in the order given (callers pass ascending cell order), inside the
    /// context's transaction or one begun here. Blocks are encoded in parallel batches of <see cref="EncodeBatchSize"/>;
    /// rows are inserted sequentially through one prepared command, so memory holds at most one batch of encoded blocks.
    /// </summary>
    public async Task WriteTileBlocksAsync(IEnumerable<TileBlock> blocks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        await using var scope = await SqliteAmbientTransaction.JoinOrBeginAsync(db, ct);
        WriteTileBlocks(scope.Connection, scope.Transaction, blocks, null, ct);
        await scope.CommitAsync(ct);
    }

    public const int EncodeBatchSize = 256;

    // onBatch receives the number of blocks inserted so far after each batch.
    private static void WriteTileBlocks(SqliteConnection conn, SqliteTransaction tx, IEnumerable<TileBlock> blocks, Action<int>? onBatch, CancellationToken ct)
    {
        using var cmd = Prepare(conn, tx, "tile_blocks", "CellId", "TileCount", "Data");
        var p = cmd.Parameters;
        var batch = new List<TileBlock>(EncodeBatchSize);
        var encoded = new byte[EncodeBatchSize][];
        var written = 0;
        foreach (var block in blocks)
        {
            batch.Add(block);
            if (batch.Count == EncodeBatchSize)
            {
                written += InsertBatch(cmd, p, batch, encoded, ct);
                onBatch?.Invoke(written);
            }
        }

        if (batch.Count > 0)
        {
            written += InsertBatch(cmd, p, batch, encoded, ct);
            onBatch?.Invoke(written);
        }
    }

    private static int InsertBatch(SqliteCommand cmd, SqliteParameterCollection p, List<TileBlock> batch, byte[][] encoded, CancellationToken ct)
    {
        Parallel.For(0, batch.Count, new ParallelOptions { CancellationToken = ct }, i => encoded[i] = TileBlockCodec.Encode(batch[i]));
        for (var i = 0; i < batch.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            p[0].Value = batch[i].GlobeCell;
            p[1].Value = batch[i].Count;
            p[2].Value = encoded[i];
            cmd.ExecuteNonQuery();
        }

        var n = batch.Count;
        batch.Clear();
        Array.Clear(encoded);
        return n;
    }

    /// <summary>Loads and decodes only the row of <paramref name="cell"/>; null when no block is stored for it.</summary>
    public async Task<TileBlock?> LoadTileBlockAsync(int cell, CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            var row = await db.TileBlocks.AsNoTracking()
                .Where(r => r.CellId == cell)
                .Select(r => new { r.TileCount, r.Data })
                .SingleOrDefaultAsync(ct);
            if (row is null) return null;
            if (row.Data is null) throw new InvalidDataException($"tile_blocks row {cell} has no data (NULL).");
            var block = TileBlockCodec.Decode(row.Data, row.TileCount);
            if (block.GlobeCell != cell || block.Count != row.TileCount)
                throw new InvalidDataException(
                    $"tile_blocks row {cell} holds cell {block.GlobeCell} with {block.Count} tiles; the row records {row.TileCount}.");
            return block;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    Task<TileBlock?> ITileBlockSource.LoadAsync(int globeCell, CancellationToken ct) => LoadTileBlockAsync(globeCell, ct);

    public async Task WriteLakesAsync(IReadOnlyList<TileLake> lakes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lakes);
        await using var scope = await SqliteAmbientTransaction.JoinOrBeginAsync(db, ct);
        WriteLakes(scope.Connection, scope.Transaction, lakes);
        await scope.CommitAsync(ct);
    }

    private static void WriteLakes(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<TileLake> lakes)
    {
        using var cmd = Prepare(conn, tx, "lakes",
            "Id", "Kind", "WaterLevelM", "TileCount", "AreaKm2", "OutletTile", "LowestTile", "Inflow", "Evaporation", "ExitTile");
        var p = cmd.Parameters;
        foreach (var lake in lakes)
        {
            p[0].Value = lake.Id;
            p[1].Value = (byte)lake.Kind;
            p[2].Value = lake.WaterLevelM;
            p[3].Value = lake.TileCount;
            p[4].Value = lake.AreaKm2;
            p[5].Value = lake.OutletTile;
            p[6].Value = lake.LowestTile;
            p[7].Value = lake.Inflow;
            p[8].Value = lake.Evaporation;
            p[9].Value = lake.ExitTile;
            cmd.ExecuteNonQuery();
        }
    }

    public async Task<List<TileLake>> LoadLakesAsync(CancellationToken ct = default)
    {
        var rows = await db.Lakes.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        foreach (var r in rows)
            if (r.Kind is not ((byte)TileWaterKind.Lake or (byte)TileWaterKind.DryLakeBed))
                throw new InvalidDataException($"lakes row {r.Id} has kind {r.Kind}; expected {(byte)TileWaterKind.Lake} (Lake) or {(byte)TileWaterKind.DryLakeBed} (DryLakeBed).");
        return rows.Select(r => new TileLake(r.Id, (TileWaterKind)r.Kind, r.WaterLevelM, r.TileCount, r.AreaKm2, r.OutletTile, r.LowestTile, r.Inflow, r.Evaporation, r.ExitTile)).ToList();
    }

    /// <summary>Stores the region-level payload as the single <c>region_level</c> row (Id 0), replacing any earlier one.</summary>
    public async Task WriteRegionLevelAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        await using var scope = await SqliteAmbientTransaction.JoinOrBeginAsync(db, ct);
        WriteRegionLevel(scope.Connection, scope.Transaction, data);
        await scope.CommitAsync(ct);
    }

    private static void WriteRegionLevel(SqliteConnection conn, SqliteTransaction tx, byte[] data)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO region_level (Id, Data) VALUES (0, $Data)";
        cmd.Parameters.Add(new SqliteParameter("$Data", data));
        cmd.ExecuteNonQuery();
    }

    private static void WriteMapImagery(SqliteConnection conn, SqliteTransaction tx, int id, byte[] data)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO map_imagery (Id, Data) VALUES ($Id, $Data)";
        cmd.Parameters.Add(new SqliteParameter("$Id", id));
        cmd.Parameters.Add(new SqliteParameter("$Data", data));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The stored region-level payload, or null when none was written.</summary>
    public async Task<byte[]?> LoadRegionLevelAsync(CancellationToken ct = default)
        => await db.RegionLevels.AsNoTracking().Where(r => r.Id == 0).Select(r => r.Data).SingleOrDefaultAsync(ct);

    /// <summary>
    /// The settings recorded in <c>worldgen_runs</c> for <paramref name="world"/>, or null when no run is recorded. Properties
    /// added to <see cref="WorldGenSettings"/> after the run was written take their defaults. The stored generator
    /// version is checked first: a run written by an older or newer generator throws <see cref="GlobeGeneratorVersionException"/>
    /// (its settings may name keys this build retired, and its layers differ from what this build generates).
    /// </summary>
    public async Task<WorldGenSettings?> LoadSettingsAsync(WorldId world, CancellationToken ct = default)
    {
        var run = await db.Set<WorldGenRunRow>().AsNoTracking()
            .Where(r => r.WorldId == world)
            .Select(r => new { r.GeneratorVersion, r.SettingsJson })
            .SingleOrDefaultAsync(ct);
        if (run is null) return null;
        GlobeGeneratorVersionException.ThrowIfDifferent(run.GeneratorVersion);
        return WorldGenSettingsJson.Deserialize(run.SettingsJson);
    }

    /// <summary>
    /// Writes a generated world in one transaction (the context's when active, else one begun here): the globe tables
    /// (summaries), the <c>worldgen_runs</c> record, one <c>tile_blocks</c> row per globe cell in ascending cell order
    /// (encoded in parallel batches of <see cref="EncodeBatchSize"/>), the <c>lakes</c> rows and the <c>region_level</c>
    /// row (<see cref="RegionSummaryCodec"/>). <paramref name="generated"/> must come from <see cref="WorldGenerator.Generate"/>
    /// (tiles and region summary present). <paramref name="progress"/> receives each step as it starts and the tile-block
    /// fraction after every batch; it is called synchronously on the writing thread. Returns the elapsed time.
    /// </summary>
    public async Task<TimeSpan> WriteAsync(WorldId world, WorldGenSettings settings, GeneratedWorld generated,
        IProgress<GlobeWriteProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(generated);
        var l = generated.Layers;
        var tiles = generated.Tiles
            ?? throw new InvalidOperationException("The generation has no tile layers (WorldGenerator.GenerateGlobe output); persist the output of WorldGenerator.Generate.");
        var regions = generated.Regions
            ?? throw new InvalidOperationException("The generation has no region summary (WorldGenerator.GenerateGlobe output); persist the output of WorldGenerator.Generate.");
        var g = settings.Grid;
        if (g.Frequency != l.Grid.Frequency)
            throw new InvalidOperationException(
                $"Settings grid frequency {g.Frequency} does not match the generated grid frequency {l.Grid.Frequency}.");
        if (regions.Grid.Frequency != g.Frequency * g.RegionMultiplier || tiles.Grid.Frequency != g.Frequency * g.RegionMultiplier * g.TileMultiplier)
            throw new InvalidOperationException(
                $"Settings give region frequency {g.Frequency * g.RegionMultiplier} and tile frequency {g.Frequency * g.RegionMultiplier * g.TileMultiplier}; " +
                $"the generation has {regions.Grid.Frequency} and {tiles.Grid.Frequency}.");

        var row = await db.Worlds.AsNoTracking()
            .Where(w => w.Id == world)
            .Select(w => new { w.GridFrequency, w.PlanetRadiusKm, w.SeaLevelM, w.WorldGenVersion })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"World {world.Value} does not exist; save it before writing its globe.");
        if (row.GridFrequency != l.Grid.Frequency || row.PlanetRadiusKm != g.PlanetRadiusKm || row.SeaLevelM != l.SeaLevelM || row.WorldGenVersion != generated.GeneratorVersion)
            throw new InvalidOperationException(
                $"World {world.Value} records grid frequency {row.GridFrequency}, radius {row.PlanetRadiusKm} km, sea level {row.SeaLevelM} m, generator version {row.WorldGenVersion}; " +
                $"the generation has {l.Grid.Frequency}, {g.PlanetRadiusKm} km, {l.SeaLevelM} m, {generated.GeneratorVersion}. " +
                "Call World.SetGlobe and World.SetSeaLevel and save the world before writing its globe.");

        await using (var scope = await SqliteAmbientTransaction.JoinOrBeginAsync(db, ct))
        {
            var (conn, tx) = (scope.Connection, scope.Transaction);
            progress?.Report(new GlobeWriteProgress(GlobeWriteStep.GlobeTables, 0));
            WriteCells(conn, tx, world, l);
            WriteNeighbours(conn, tx, world, l.Grid);
            WritePlates(conn, tx, world, l.Plates);
            WriteComposition(conn, tx, world, l.Composition);
            WriteRiverEdges(conn, tx, world, l.RiverEdges);
            WriteRun(conn, tx, world, settings, generated);

            var cells = l.Grid.CellCount;
            progress?.Report(new GlobeWriteProgress(GlobeWriteStep.TileBlocks, 0));
            var source = new InMemoryTileBlockSource(tiles);
            if (source.CellCount != cells)
                throw new InvalidOperationException($"The tile layers group tiles into {source.CellCount} globe cells; the globe has {cells}.");
            WriteTileBlocks(conn, tx, source.All(), progress is null ? null : done => progress.Report(new GlobeWriteProgress(GlobeWriteStep.TileBlocks, (double)done / cells)), ct);

            progress?.Report(new GlobeWriteProgress(GlobeWriteStep.Lakes, 0));
            WriteLakes(conn, tx, generated.Lakes);
            progress?.Report(new GlobeWriteProgress(GlobeWriteStep.RegionLevel, 0));
            WriteRegionLevel(conn, tx, RegionSummaryCodec.Encode(regions));
            // Map imagery rides with the region level step (derived view data).
            if (generated.Imagery is { } imagery) WriteMapImagery(conn, tx, MapImageryRow.ImageryId, MapImageryCodec.EncodeImagery(imagery));
            if (generated.RiverCourses is { } courses) WriteMapImagery(conn, tx, MapImageryRow.RiverCoursesId, MapImageryCodec.EncodeRiverCourses(courses));
            await scope.CommitAsync(ct);
        }

        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>
    /// Loads a stored world without its tiles: globe layers (<see cref="LoadLayersAsync"/>), the region summary (grid rebuilt
    /// from the recorded Frequency × RegionMultiplier), the lakes and the recorded settings. Tile blocks load on demand
    /// through <see cref="ITileBlockSource"/> (this store, or <see cref="SqliteTileBlockSource"/> for loads from several
    /// threads). A world written by an older or newer generator throws <see cref="GlobeGeneratorVersionException"/>; a world with
    /// no recorded run or no region level throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<StoredGlobe> LoadAsync(WorldId world, CancellationToken ct = default)
    {
        var settings = await LoadSettingsAsync(world, ct)
            ?? throw new InvalidOperationException($"World {world.Value} has no recorded generation run; regenerate the world.");
        var version = await db.WorldGenRuns.AsNoTracking().Where(r => r.WorldId == world).Select(r => r.GeneratorVersion).SingleAsync(ct);
        var layers = await LoadLayersAsync(world, ct);
        var data = await LoadRegionLevelAsync(ct)
            ?? throw new InvalidOperationException($"World {world.Value} has no region level (no tile data); regenerate the world.");
        var regionGrid = GlobeGrid.Build(settings.Grid.Frequency * settings.Grid.RegionMultiplier);
        var regions = RegionSummaryCodec.Decode(data, regionGrid, GlobeGeometry.Compute(regionGrid));
        if (regions.GlobeParent.Any(c => (uint)c >= (uint)layers.Grid.CellCount))
            throw new InvalidDataException("Region level names a globe cell outside the grid.");
        var lakes = await LoadLakesAsync(ct);
        var lakeIds = lakes.Select(l => l.Id).ToHashSet();
        for (var c = 0; c < regions.Count; c++)
            if (regions.LakeId[c] >= 0 && !lakeIds.Contains(regions.LakeId[c]))
                throw new InvalidDataException($"Region level cell {c} names lake {regions.LakeId[c]}, which the lakes table does not hold.");
        var tileGrid = new TileGrid(regionGrid.Frequency * settings.Grid.TileMultiplier);
        // A file written before the AddMapImagery migration has no map_imagery table (opening does not migrate): no imagery.
        var hasImageryTable = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'map_imagery'").SingleAsync(ct) > 0;
        var imageryRows = hasImageryTable
            ? await db.MapImagery.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Data, ct)
            : [];
        var imagery = imageryRows.TryGetValue(MapImageryRow.ImageryId, out var i) ? MapImageryCodec.DecodeImagery(i, layers.Grid.CellCount) : null;
        var courses = imageryRows.TryGetValue(MapImageryRow.RiverCoursesId, out var rc) ? MapImageryCodec.DecodeRiverCourses(rc, tileGrid.Count) : null;
        if (courses is not null && (courses.From.Any(t => (uint)t >= (uint)tileGrid.Count) || courses.To.Any(t => (uint)t >= (uint)tileGrid.Count)))
            throw new InvalidDataException("River courses name a tile outside the tile grid.");
        return new StoredGlobe(layers, regions, lakes, settings, version, tileGrid) { Imagery = imagery, RiverCourses = courses };
    }

    /// <summary>
    /// Loads the globe-cell layers only (no region summary, lakes or tiles). A world whose recorded run is from an older or newer
    /// generator throws <see cref="GlobeGeneratorVersionException"/>.
    /// </summary>
    public async Task<WorldLayers> LoadLayersAsync(WorldId world, CancellationToken ct = default)
    {
        var w = await db.Worlds.AsNoTracking()
            .Where(x => x.Id == world)
            .Select(x => new { x.GridFrequency, x.SeaLevelM })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"World {world.Value} does not exist.");
        var runVersion = await db.WorldGenRuns.AsNoTracking().Where(r => r.WorldId == world).Select(r => (int?)r.GeneratorVersion).SingleOrDefaultAsync(ct);
        if (runVersion is { } v) GlobeGeneratorVersionException.ThrowIfDifferent(v);

        var grid = GlobeGrid.Build(w.GridFrequency);
        var layers = new WorldLayers(grid, GlobeGeometry.Compute(grid)) { SeaLevelM = w.SeaLevelM };
        var n = grid.CellCount;

        var cells = await db.GlobeCells.AsNoTracking().Where(r => r.WorldId == world).OrderBy(r => r.CellId).ToListAsync(ct);
        if (cells.Count != n)
            throw new InvalidOperationException($"World {world.Value} has {cells.Count} globe cell rows; grid frequency {w.GridFrequency} needs {n}.");

        var plateOf = new int[n];
        var elevation = new int[n];
        var boundary = new byte[n];
        var currentDir = new sbyte[n];
        var currentAnomaly = new short[n];
        var windDir = new sbyte[n];
        var windStrength = new byte[n];
        var temperature = new short[n];
        var precipitation = new int[n];
        var biome = new byte[n];
        var downstream = new int[n];
        var flow = new long[n];
        var water = new byte[n];
        for (var c = 0; c < n; c++)
        {
            var r = cells[c];
            if (r.CellId != c)
                throw new InvalidOperationException($"World {world.Value} globe cell rows are not dense: expected cell {c}, found {r.CellId}.");
            plateOf[c] = r.PlateId;
            elevation[c] = r.ElevationM;
            boundary[c] = r.BoundaryKind;
            currentDir[c] = r.CurrentDir;
            currentAnomaly[c] = r.CurrentAnomalyDeciC;
            windDir[c] = r.WindDir;
            windStrength[c] = r.WindStrength;
            temperature[c] = r.TemperatureDeciC;
            precipitation[c] = r.PrecipitationMm;
            biome[c] = r.Biome;
            downstream[c] = r.DownstreamOf;
            flow[c] = r.FlowAccum;
            water[c] = r.WaterBodyKind;
        }

        layers.PlateOf = plateOf;
        layers.ElevationM = elevation;
        layers.BoundaryKind = boundary;
        layers.CurrentDir = currentDir;
        layers.CurrentAnomalyDeciC = currentAnomaly;
        layers.WindDir = windDir;
        layers.WindStrength = windStrength;
        layers.TemperatureDeciC = temperature;
        layers.PrecipitationMm = precipitation;
        layers.BiomeOf = biome;
        layers.DownstreamOf = downstream;
        layers.FlowAccum = flow;
        layers.WaterBodyKind = water;

        var plates = await db.Plates.AsNoTracking().Where(r => r.WorldId == world).OrderBy(r => r.PlateId).ToListAsync(ct);
        layers.Plates = plates
            .Select(p => new PlateInfo(p.PlateId, p.Continental, new Vector3d(p.RotationAxisX, p.RotationAxisY, p.RotationAxisZ), p.AngularSpeed, p.SeedCell))
            .ToArray();

        var comp = await db.GlobeCellCompositions.AsNoTracking()
            .Where(r => r.WorldId == world)
            .OrderBy(r => r.CellId).ThenBy(r => r.Slot)
            .ToListAsync(ct);
        var composition = new CompositionEntry[n][];
        var i = 0;
        for (var c = 0; c < n; c++)
        {
            var start = i;
            while (i < comp.Count && comp[i].CellId == c)
                i++;
            var entries = new CompositionEntry[i - start];
            for (var k = 0; k < entries.Length; k++)
                entries[k] = new CompositionEntry((Landform)comp[start + k].Landform, comp[start + k].ShareBp);
            composition[c] = entries;
        }

        if (i != comp.Count)
            throw new InvalidOperationException($"World {world.Value} has composition rows for cell {comp[i].CellId}, outside the grid.");
        layers.Composition = composition;

        var rivers = await db.RiverEdges.AsNoTracking()
            .Where(r => r.WorldId == world)
            .OrderBy(r => r.LowCellId).ThenBy(r => r.HighCellId)
            .ToListAsync(ct);
        layers.RiverEdges = rivers.Select(r => new RiverEdge(r.LowCellId, r.HighCellId, r.FlowsLowToHigh, r.Flow)).ToList();

        return layers;
    }

    private static SqliteCommand Prepare(SqliteConnection conn, SqliteTransaction tx, string table, params string[] columns)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "$" + c))})";
        foreach (var c in columns)
            cmd.Parameters.Add(new SqliteParameter("$" + c, null));
        cmd.Prepare();
        return cmd;
    }

    private static void WriteCells(SqliteConnection conn, SqliteTransaction tx, WorldId world, WorldLayers l)
    {
        using var cmd = Prepare(conn, tx, "globe_cells",
            "WorldId", "CellId", "PlateId", "ElevationM", "BoundaryKind", "CurrentDir", "CurrentAnomalyDeciC", "WindDir",
            "WindStrength", "TemperatureDeciC", "PrecipitationMm", "Biome", "DownstreamOf", "FlowAccum", "WaterBodyKind");
        var p = cmd.Parameters;
        p[0].Value = world.Value;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            p[1].Value = c;
            p[2].Value = l.PlateOf[c];
            p[3].Value = l.ElevationM[c];
            p[4].Value = l.BoundaryKind[c];
            p[5].Value = l.CurrentDir[c];
            p[6].Value = l.CurrentAnomalyDeciC[c];
            p[7].Value = l.WindDir[c];
            p[8].Value = l.WindStrength[c];
            p[9].Value = l.TemperatureDeciC[c];
            p[10].Value = l.PrecipitationMm[c];
            p[11].Value = l.BiomeOf[c];
            p[12].Value = l.DownstreamOf[c];
            p[13].Value = l.FlowAccum[c];
            p[14].Value = l.WaterBodyKind[c];
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteNeighbours(SqliteConnection conn, SqliteTransaction tx, WorldId world, GlobeGrid grid)
    {
        using var cmd = Prepare(conn, tx, "globe_cell_neighbours", "WorldId", "CellId", "Slot", "NeighbourCellId");
        var p = cmd.Parameters;
        p[0].Value = world.Value;
        for (var c = 0; c < grid.CellCount; c++)
        {
            var nb = grid.Neighbours(c);
            p[1].Value = c;
            for (var s = 0; s < nb.Length; s++)
            {
                p[2].Value = (byte)s;
                p[3].Value = nb[s];
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void WritePlates(SqliteConnection conn, SqliteTransaction tx, WorldId world, PlateInfo[] plates)
    {
        using var cmd = Prepare(conn, tx, "plates",
            "WorldId", "PlateId", "Continental", "RotationAxisX", "RotationAxisY", "RotationAxisZ", "AngularSpeed", "SeedCell");
        var p = cmd.Parameters;
        p[0].Value = world.Value;
        foreach (var plate in plates)
        {
            p[1].Value = plate.Id;
            p[2].Value = plate.Continental;
            p[3].Value = plate.RotationAxis.X;
            p[4].Value = plate.RotationAxis.Y;
            p[5].Value = plate.RotationAxis.Z;
            p[6].Value = plate.AngularSpeed;
            p[7].Value = plate.SeedCell;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteComposition(SqliteConnection conn, SqliteTransaction tx, WorldId world, CompositionEntry[][] composition)
    {
        using var cmd = Prepare(conn, tx, "globe_cell_composition", "WorldId", "CellId", "Slot", "Landform", "ShareBp");
        var p = cmd.Parameters;
        p[0].Value = world.Value;
        for (var c = 0; c < composition.Length; c++)
        {
            p[1].Value = c;
            var entries = composition[c];
            for (var s = 0; s < entries.Length; s++)
            {
                p[2].Value = (byte)s;
                p[3].Value = (byte)entries[s].Landform;
                p[4].Value = entries[s].ShareBp;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void WriteRiverEdges(SqliteConnection conn, SqliteTransaction tx, WorldId world, List<RiverEdge> edges)
    {
        using var cmd = Prepare(conn, tx, "river_edges", "WorldId", "LowCellId", "HighCellId", "FlowsLowToHigh", "Flow");
        var p = cmd.Parameters;
        p[0].Value = world.Value;
        foreach (var e in edges)
        {
            p[1].Value = e.LowCell;
            p[2].Value = e.HighCell;
            p[3].Value = e.FlowsLowToHigh;
            p[4].Value = e.Flow;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteRun(SqliteConnection conn, SqliteTransaction tx, WorldId world, WorldGenSettings settings, GeneratedWorld generated)
    {
        using var cmd = Prepare(conn, tx, "worldgen_runs", "WorldId", "GeneratorVersion", "SettingsJson", "StagesJson");
        var p = cmd.Parameters;
        p[0].Value = world.Value;
        p[1].Value = generated.GeneratorVersion;
        p[2].Value = WorldGenSettingsJson.Serialize(settings);
        p[3].Value = JsonSerializer.Serialize(
            generated.Stages.Select(s => new WorldGenStageRecord(s.Stage.ToString(), s.Hash, s.Duration.Ticks)).ToList());
        cmd.ExecuteNonQuery();
    }
}

/// <summary>A world loaded by <see cref="GlobeStore.LoadAsync"/>: everything except the tile blocks.</summary>
/// <param name="Layers">Globe-cell layers (summaries of the tiles); <see cref="WorldLayers.Areas"/> is empty.</param>
/// <param name="Regions">Region-cell summary.</param>
/// <param name="Lakes">Lakes and dry lake beds, ordered by id.</param>
/// <param name="Settings">The recorded generation settings.</param>
/// <param name="GeneratorVersion">The recorded generator version (equal to <see cref="WorldGenerator.GeneratorVersion"/>).</param>
/// <param name="TileGrid">Tile grid at Frequency × RegionMultiplier × TileMultiplier (ids, centres, neighbours, <see cref="TileGrid.Nearest"/>).</param>
public sealed record StoredGlobe(
    WorldLayers Layers,
    RegionSummary Regions,
    IReadOnlyList<TileLake> Lakes,
    WorldGenSettings Settings,
    int GeneratorVersion,
    TileGrid TileGrid)
{
    /// <summary>Stored map imagery; null for a world written before it existed.</summary>
    public CellImagery? Imagery { get; init; }

    /// <summary>Stored river courses; null for a world written before they existed.</summary>
    public RiverCourses? RiverCourses { get; init; }
}

/// <summary>Step of <see cref="GlobeStore.WriteAsync"/>, in write order.</summary>
public enum GlobeWriteStep
{
    GlobeTables,
    TileBlocks,
    Lakes,
    RegionLevel,
}

/// <summary>Progress of <see cref="GlobeStore.WriteAsync"/>: the current step and the fraction of it done (tile blocks report per batch; other steps report 0 when they start).</summary>
public readonly record struct GlobeWriteProgress(GlobeWriteStep Step, double Fraction);

/// <summary>
/// A stored globe written by a different generator version than this build (<see cref="WorldGenerator.GeneratorVersion"/>).
/// Older: <c>world generated by generator v{stored}; this build is v{current} — regenerate the world</c>. Newer: <c>world
/// generated by generator v{stored}; this build is v{current} — open it with a newer build of this program</c> (its tables
/// may hold values this build does not know). The CLI and the desktop app show the message as is.
/// </summary>
public sealed class GlobeGeneratorVersionException(int storedVersion)
    : InvalidOperationException($"world generated by generator v{storedVersion}; this build is v{WorldGenerator.GeneratorVersion} — "
        + (storedVersion > WorldGenerator.GeneratorVersion ? "open it with a newer build of this program" : "regenerate the world"))
{
    public int StoredVersion { get; } = storedVersion;

    public int CurrentVersion => WorldGenerator.GeneratorVersion;

    /// <summary>Throws unless <paramref name="storedVersion"/> equals this build's generator version.</summary>
    internal static void ThrowIfDifferent(int storedVersion)
    {
        if (storedVersion != WorldGenerator.GeneratorVersion) throw new GlobeGeneratorVersionException(storedVersion);
    }
}
