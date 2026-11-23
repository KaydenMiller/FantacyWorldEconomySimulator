using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Tile stage 3 (design spec §6 step 6): temperature and precipitation per tile. Interpolation is <see cref="TileField"/>
/// over the globe values.
/// <para>
/// Temperature (tenths of °C) = interpolated globe temperature − <see cref="ClimateSettings.LapseDeciCPerKm"/> × (tile
/// surface − interpolated globe surface) / 1,000 m, on absolute elevations so the difference between the tile and the
/// globe sea levels does not shift temperatures. Surface: a land tile's elevation (also below sea level, e.g. an enclosed
/// depression); a globe cell's elevation, or the globe sea level for an ocean cell (the globe stage applies no lapse on
/// ocean cells). Ocean tiles get no lapse correction (the interpolated globe temperature). Rounded to nearest (midpoint
/// away from zero), clamped to <see cref="short"/>.
/// </para>
/// <para>
/// Precipitation (mm/year) = interpolated globe precipitation × factor / 10,000, rounded to nearest and clamped to
/// <see cref="ushort"/>, with factor (bp) = 10,000 + <see cref="TilesSettings.OrographicGainBp"/> × rise, clamped to
/// [<see cref="TilesSettings.OrographicMinBp"/>, <see cref="TilesSettings.OrographicMaxBp"/>]. Rise (m/km) = (height
/// above sea of the tile − height above sea of its upwind neighbour) / chord distance in km. Wind: each globe cell's
/// direction is the unit vector from its centre toward its <see cref="WorldLayers.WindDir"/> neighbour's centre; the tile
/// wind is that vector field interpolated with <see cref="TileField"/> (per component), projected onto the tile's
/// tangent plane, so the direction changes smoothly across globe-cell borders. The upwind neighbour is the tile neighbour
/// whose direction has the largest dot product with the reversed wind (ties: lower slot). A windward slope (rising
/// downwind) gets more rain, a leeward slope less. A tile whose interpolated wind is zero gets factor 10,000.
/// </para>
/// </summary>
public static class TileClimateStage
{
    private const int BpFull = 10_000;

    public static void Run(TileLayers tiles, WorldLayers globe, WorldGenSettings settings, ParallelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(globe);
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Tiles;
        double lapse = settings.Climate.LapseDeciCPerKm;
        double radiusKm = settings.Grid.PlanetRadiusKm;
        var grid = tiles.Grid;
        var field = new TileField(grid, tiles.GlobeTiles);
        var globeTemp = globe.TemperatureDeciC;
        var globePrecip = globe.PrecipitationMm;
        var globeSurface = new int[globe.Grid.CellCount];
        for (int c = 0; c < globeSurface.Length; c++)
            globeSurface[c] = globe.IsOcean(c) ? globe.SeaLevelM : globe.ElevationM[c];
        var wind = WindVectors(globe);
        var wx = new double[wind.Length];
        var wy = new double[wind.Length];
        var wz = new double[wind.Length];
        for (int c = 0; c < wind.Length; c++) (wx[c], wy[c], wz[c]) = (wind[c].X, wind[c].Y, wind[c].Z);
        var ocean = tiles.Ocean;
        var elevation = tiles.ElevationM;
        int sea = tiles.SeaLevelM;
        long gain = s.OrographicGainBp;
        double minBp = s.OrographicMinBp, maxBp = s.OrographicMaxBp;
        var temperature = new short[grid.Count];
        var precipitation = new ushort[grid.Count];

        TileParallel.For(grid.Count, options, (start, end) =>
        {
            Span<int> nb = stackalloc int[6];
            for (int id = start; id < end; id++)
            {
                var w = field.At(id);
                int height = Math.Max(0, elevation[id] - sea);
                double t = w.Apply(globeTemp);
                if (!ocean[id]) t -= lapse * (elevation[id] - w.Apply(globeSurface)) / 1000.0;
                temperature[id] = (short)Math.Clamp(Math.Round(t, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);

                var p = grid.Centre(id);
                var wv = new Vector3d(w.Apply(wx), w.Apply(wy), w.Apply(wz));
                var tangent = wv - (p * Vector3d.Dot(wv, p));
                int k = grid.Neighbours(id, nb);
                int up = -1;
                double upDot = double.NegativeInfinity, upChord = 0;
                for (int slot = 0; slot < k; slot++)
                {
                    var d = grid.Centre(nb[slot]) - p;
                    double len = d.Length;
                    double dot = -Vector3d.Dot(d, tangent) / len;
                    if (dot > upDot) { upDot = dot; up = nb[slot]; upChord = len; }
                }

                double factor = BpFull;
                if (up >= 0 && tangent.Length > 1e-12)
                {
                    double rise = (height - Math.Max(0, elevation[up] - sea)) / (upChord * radiusKm);
                    factor = Math.Clamp(BpFull + (gain * rise), minBp, maxBp);
                }
                double precip = w.Apply(globePrecip) * factor / BpFull;
                precipitation[id] = (ushort)Math.Clamp(Math.Round(precip, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);
            }
        });

        tiles.TemperatureDeciC = temperature;
        tiles.PrecipitationMm = precipitation;
    }

    /// <summary>Unit vector from each globe cell centre toward its <see cref="WorldLayers.WindDir"/> neighbour's centre.</summary>
    internal static Vector3d[] WindVectors(WorldLayers globe)
    {
        var n = globe.Grid.CellCount;
        var result = new Vector3d[n];
        for (int c = 0; c < n; c++)
        {
            var target = globe.Grid.Neighbours(c)[globe.WindDir[c]];
            result[c] = (globe.Geometry.Centre(target) - globe.Geometry.Centre(c)).Normalize();
        }
        return result;
    }

    public static ulong Hash(TileLayers t) =>
        DeterministicHash.Combine(LayerHash.Of(t.TemperatureDeciC), LayerHash.Of(t.PrecipitationMm), 0, 0);
}
