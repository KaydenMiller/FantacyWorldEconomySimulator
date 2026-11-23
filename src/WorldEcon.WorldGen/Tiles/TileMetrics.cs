namespace WorldEcon.WorldGen.Tiles;

/// <summary>Size figures of a tile grid for statistics output.</summary>
public static class TileMetrics
{
    /// <summary>Mean tile area in km²: sphere area 4πR² divided by the tile count.</summary>
    public static double MeanAreaKm2(double planetRadiusKm, long tileCount) =>
        4 * Math.PI * planetRadiusKm * planetRadiusKm / tileCount;

    /// <summary>
    /// Tile size in km: the centre-to-centre distance of a regular hexagon with the mean tile area, √(2A / √3). At the
    /// default preset (radius 6,371 km, 23,592,962 tiles) this is 5.00 km, the "5 km" of the preset name.
    /// </summary>
    public static double SizeKm(double planetRadiusKm, long tileCount) =>
        Math.Sqrt(2 * MeanAreaKm2(planetRadiusKm, tileCount) / Math.Sqrt(3));
}
