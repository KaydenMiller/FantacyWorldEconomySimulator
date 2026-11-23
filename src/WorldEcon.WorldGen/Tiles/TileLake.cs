namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Surface water of a tile (design spec §7), stored as byte in <see cref="TileLayers.WaterKind"/>. Values 0–3 equal
/// <see cref="Pipeline.WaterBodyKind"/> (None, River, Lake, DryLakeBed); <see cref="Ocean"/> is added because the globe
/// enum has no ocean value (globe ocean comes from the sea level).
/// </summary>
public enum TileWaterKind : byte
{
    None,
    River,
    Lake,
    DryLakeBed,
    Ocean,
}

/// <summary>
/// One lake or dry lake bed from <see cref="TileHydrologyStage"/>: a connected set of tiles with one water level.
/// </summary>
/// <param name="Id">Index in <see cref="TileLayers.Lakes"/> and the value in <see cref="TileLayers.LakeId"/> of its tiles.</param>
/// <param name="Kind"><see cref="TileWaterKind.Lake"/> or <see cref="TileWaterKind.DryLakeBed"/>.</param>
/// <param name="WaterLevelM">Every tile has elevation strictly below this (metres). Lake: the water level. Dry lake bed: lowest elevation + max(<see cref="TilesSettings.MinBasinDepthM"/>, 1), the seasonally wetted floor.</param>
/// <param name="TileCount">Number of tiles.</param>
/// <param name="AreaKm2">Sum of the tile areas, km².</param>
/// <param name="OutletTile">Full lake: the lake tile that drains out (its downstream is outside the lake). Terminal lake and dry lake bed: −1 (endorheic; all its tiles drain to <paramref name="LowestTile"/>).</param>
/// <param name="LowestTile">Tile with the lowest elevation (ties: lowest id).</param>
/// <param name="Inflow">Flow in mm·km²/yr. Full lake with <paramref name="ExitTile"/> ≥ 0: the flow arriving at the exit tile (all water the water balance delivered to the lake, including the precipitation on the lake tiles and on the tiles of the exit path). Otherwise the flow arriving at the outlet, or at the lowest tile when there is no outlet.</param>
/// <param name="Evaporation">Σ over the lake's tiles of (<see cref="HydrologySettings.EvaporationBaseMm"/> + <see cref="HydrologySettings.EvaporationMmPerDegree"/> × max(0, tile °C)) × tile area, rounded per tile, mm·km²/yr. A full lake whose water level reaches a saddle inside it can consist of several components (several <see cref="TileLake"/> entries at one level, linked into one drainage tree); the component holding the exit carries the evaporation of all of them and the others carry 0. Inflow and Evaporation default to 0 for lakes loaded from storage that does not hold them.</param>
/// <param name="ExitTile">Full lake: the tile where its evaporation is booked; it passes Inflow − min(Inflow, Evaporation) downstream. The outlet for a lake filling the whole basin, otherwise the saddle tile the lake spills over (outside the lake). −1 for terminal lakes, dry lake beds and the non-exit components of a multi-component lake.</param>
public sealed record TileLake(int Id, TileWaterKind Kind, int WaterLevelM, int TileCount, double AreaKm2, int OutletTile, int LowestTile,
    long Inflow = 0, long Evaporation = 0, int ExitTile = -1)
{
    /// <summary>What leaves the lake through its outlet: Inflow − min(Inflow, Evaporation) (0 without an outlet).</summary>
    public long Outflow => OutletTile < 0 ? 0 : Inflow - Math.Min(Inflow, Evaporation);
}
