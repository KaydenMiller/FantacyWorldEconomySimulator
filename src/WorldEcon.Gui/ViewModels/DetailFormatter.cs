using System.Globalization;
using WorldEcon.Application.Globe;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Gui.ViewModels;

/// <summary>Turns globe cell, region cell, tile and area details into label/value rows for the details panel (invariant culture).</summary>
public static partial class DetailFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<DetailRow> Cell(GlobeCellDetails d, WorldLayers l)
    {
        var neighbours = d.Neighbours;
        string Slot(sbyte slot) => slot >= 0 && slot < neighbours.Count ? $"toward cell {neighbours[slot]} (slot {slot})" : "none";
        var rows = new List<DetailRow>
        {
            new("Cell id", d.Pentagon ? $"{d.Cell} (pentagon)" : d.Cell.ToString(Inv)),
            new("Lat / lon", $"{d.LatDeg.ToString("F2", Inv)}°, {d.LonDeg.ToString("F2", Inv)}°"),
            new("Biome", d.Biome.ToString()),
            new("Composition", string.Join("\n", d.Composition.Select(e => $"{e.Landform}: {(e.ShareBp / 100.0).ToString("F2", Inv)} %"))),
            new("Elevation", string.Create(Inv, $"{d.ElevationM:N0} m ({(d.Ocean ? "ocean" : "land")}; sea level {d.SeaLevelM:N0} m)")),
            new("Temperature", $"{(d.TemperatureDeciC / 10.0).ToString("F1", Inv)} °C"),
            new("Precipitation", string.Create(Inv, $"{d.PrecipitationMm:N0} mm/yr")),
            new("Wind", d.WindSlot < 0 ? "none" : $"{Slot(d.WindSlot)}, strength {d.WindStrength}"),
            new("Current", d.CurrentSlot < 0 ? "none" : $"{Slot(d.CurrentSlot)}, anomaly {(d.CurrentAnomalyDeciC / 10.0).ToString("+0.0;-0.0;0.0", Inv)} °C"),
            new("Water body", d.Water.ToString()),
            new("Flow", string.Create(Inv, $"{d.Flow:N0} mm·km²/yr")),
            new("Downstream", d.DownstreamOf < 0 ? "none" : d.DownstreamOf.ToString(Inv)),
            new("River edges", d.RiverEdges.Count == 0 ? "none" : string.Join(", ", d.RiverEdges.Select(e => $"{e.From}→{e.To}"))),
            new("Plate", $"{d.PlateId} ({(d.PlateContinental ? "continental" : "oceanic")}), boundary {d.Boundary}"),
            new("Areas", d.Areas.Count == 0 ? "none" : string.Join("\n", d.Areas.Select(a => $"{a.Kind} '{a.Name}' {(a.CoverageBp / 100.0).ToString("F2", Inv)} %"))),
            new("Settlements", d.Settlements.Count == 0 ? "none" : string.Join(", ", d.Settlements)),
            new("Neighbours", string.Join(", ", neighbours)),
        };
        if (d.Geology is { } g) rows.InsertRange(rows.FindIndex(r => r.Label == "Plate") + 1, g.Describe().Select(x => new DetailRow(x.Label, x.Value)));
        return rows;
    }

    /// <summary>A region cell: summary values of its tiles (spec §8) and its parent globe cell.</summary>
    public static IReadOnlyList<DetailRow> RegionCell(int r, WorldSession session)
    {
        var s = session.Regions;
        var (lat, lon) = LatLon(s.Geometry.Centre(r));
        var rows = new List<DetailRow>
        {
            new("Region cell", s.Grid.IsPentagon(r) ? $"{r} (pentagon)" : r.ToString(Inv)),
            new("Lat / lon", $"{lat.ToString("F3", Inv)}°, {lon.ToString("F3", Inv)}°"),
            new("Kind", ((Landform)s.Kind[r]).ToString()),
            new("Biome", ((Biome)s.Biome[r]).ToString()),
            new("Elevation", string.Create(Inv, $"{s.ElevationM[r]:N0} m mean (sea level {session.Layers.SeaLevelM:N0} m)")),
            new("Temperature", $"{(s.TemperatureDeciC[r] / 10.0).ToString("F1", Inv)} °C mean"),
            new("Precipitation", string.Create(Inv, $"{s.PrecipitationMm[r]:N0} mm/yr mean")),
            new("Mountain share", $"{(s.MountainShareBp[r] / 100.0).ToString("F1", Inv)} % of tiles"),
            new("Flow", string.Create(Inv, $"{s.Flow[r]:N0} mm·km²/yr (largest tile)")),
            new("Downstream", s.Downstream[r] < 0 ? "none (path ends inside the cell)" : $"region cell {s.Downstream[r]}"),
            new("Lake", LakeText(s.LakeId[r], session) + (s.InLargeLake[r] ? " (drawn at Globe View)" : "")),
            new("Tiles", s.TileCount[r].ToString("N0", Inv)),
            new("Globe cell", s.GlobeParent[r].ToString(Inv)),
        };
        return rows;
    }

    /// <summary>A tile: its stored values (element <paramref name="i"/> of <paramref name="block"/>), parent cells and settlements on it.</summary>
    public static IReadOnlyList<DetailRow> Tile(int tile, TileBlock block, int i, WorldSession session)
    {
        var grid = session.World.Tiles!;
        var (lat, lon) = LatLon(grid.Centre(tile));
        var water = (TileWaterKind)block.Water[i];
        var slot = block.Downstream[i];
        var downstream = "none";
        if (slot >= 0)
        {
            Span<int> ring = stackalloc int[6];
            var n = grid.Neighbours(tile, ring);
            downstream = slot < n ? $"tile {ring[slot]}" : $"slot {slot}";
        }

        var here = session.Data.Settlements.Where(x => x.TileId == tile).Select(x => x.Name).ToList();
        return
        [
            new("Tile", grid.IsPentagon(tile) ? $"{tile} (pentagon)" : tile.ToString(Inv)),
            new("Lat / lon", $"{lat.ToString("F4", Inv)}°, {lon.ToString("F4", Inv)}°"),
            new("Area", string.Create(Inv, $"{grid.AreaKm2(tile, session.Data.Settings.Grid.PlanetRadiusKm):F1} km²")),
            new("Elevation", string.Create(Inv, $"{block.ElevationM[i]:N0} m (sea level {session.Layers.SeaLevelM:N0} m)")),
            new("Landform", ((Landform)block.Landform[i]).ToString()),
            new("Biome", ((Biome)block.Biome[i]).ToString()),
            new("Water", water.ToString()),
            new("Lake", LakeText(block.LakeId[i], session)),
            new("Flow", string.Create(Inv, $"{block.Flow[i]:N0} mm·km²/yr, downstream {downstream}")),
            new("Temperature", $"{(block.TemperatureDeciC[i] / 10.0).ToString("F1", Inv)} °C"),
            new("Precipitation", string.Create(Inv, $"{block.PrecipitationMm[i]:N0} mm/yr")),
            new("Globe cell", block.GlobeCell.ToString(Inv)),
            new("Region cell", session.RegionCellOfTile(tile).ToString(Inv)),
            new("Settlements", here.Count == 0 ? "none" : string.Join(", ", here)),
            new("Node", NodeText(session, tile)),
            new("Routes", RoutesText(session.RoutesOnTile(tile))),
        ];
    }

    /// <summary>"none", or one line per route on the tile: road, ends, category, surface, tiers and length.</summary>
    internal static string RoutesText(IReadOnlyList<WorldEcon.Application.Globe.GlobeRoute> routes) =>
        routes.Count == 0 ? "none" : string.Join("\n", routes.Select(r => string.Create(Inv,
            $"{(r.RoadName.Length == 0 ? "(no road)" : r.RoadName)}: {r.NodeA} – {r.NodeB}, {r.Category} {r.Surface}, tiers {r.MinTier}-{r.MaxTier}, {r.LengthMetres / 1000.0:F1} km, traffic {r.TrafficCount}")));

    /// <summary>"none", or the lake's id, kind, water level, tile count and area.</summary>
    private static string LakeText(int lakeId, WorldSession session)
    {
        if (lakeId < 0) return "none";
        if (session.Lake(lakeId) is not { } k) return $"{lakeId} (not in the lake table)";
        return string.Create(Inv, $"{k.Id} ({k.Kind}), water level {k.WaterLevelM:N0} m, {k.TileCount:N0} tiles, {k.AreaKm2:N0} km²");
    }

    // Same convention as GlobeGeometry.LatLon: latitude from Y, longitude atan2(X, Z).
    private static (double LatDeg, double LonDeg) LatLon(Vector3d v) =>
        (Math.Asin(Math.Clamp(v.Y, -1, 1)) * 180 / Math.PI, Math.Atan2(v.X, v.Z) * 180 / Math.PI);

    public static IReadOnlyList<DetailRow> Area(GeneratedArea a, WorldLayers l)
    {
        if (a.Cells.Count == 0)
            return [new("Kind", a.Kind.ToString()), new("Name", a.Name), new("Cells", "no cells")];
        var (best, bestBp) = a.Cells.OrderByDescending(x => x.CoverageBp).ThenBy(x => x.Cell).First();
        var land = a.Cells.Count(x => !l.IsOcean(x.Cell));
        return
        [
            new("Kind", a.Kind.ToString()),
            new("Name", a.Name),
            new("Cells", string.Create(Inv, $"{a.Cells.Count:N0} ({land:N0} land, {a.Cells.Count - land:N0} ocean)")),
            new("Highest coverage", $"cell {best}, {(bestBp / 100.0).ToString("F2", Inv)} %"),
            new("Mean coverage", $"{(a.Cells.Average(x => (double)x.CoverageBp) / 100.0).ToString("F2", Inv)} %"),
        ];
    }
}
