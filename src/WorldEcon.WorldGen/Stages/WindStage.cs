using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>
/// Stage 5: prevailing surface winds by latitude band, per hemisphere (settings: <see cref="WindSettings"/>; defaults
/// quoted). Winds are named for where they come
/// from; <see cref="WorldLayers.WindDir"/> stores the neighbour slot the air moves toward (best aligned with
/// the intended tangent vector, ties to the lower slot). 0–30° |latitude|: trade winds, toward the west and
/// the equator (−east + 0.5 × equatorward). 30–60°: westerlies, toward the east and the pole
/// (east + 0.5 × poleward). 60–90°: polar easterlies, toward the west (+ <see cref="WindSettings.PolarMeridionalShareBp"/>
/// × equatorward, default 0: along the parallels, so polar air circles the pole instead of flowing out of it and
/// moisture from polar coasts reaches a polar continent). The band of a cell is chosen from its latitude shifted by
/// <see cref="WindSettings.BandNoiseAmplitudeDeg"/> (15°) × fractal noise in [−1, 1], so band edges wander instead of
/// following parallels; the hemisphere (poleward direction) is the geometric one. Strength is 160 at band centres
/// (15°, 45°, 75° of shifted latitude), falling linearly to 60 at band edges. Land more than 2000 m above sea
/// level loses (elevation above sea level) / 50, with a floor of 20.
/// </summary>
public static class WindStage
{
    /// <summary>Runs the stage; <paramref name="seed"/> seeds the band-edge noise (<see cref="WindSettings.BandNoiseAmplitudeDeg"/>).</summary>
    public static void Run(WorldLayers layers, WorldGenSettings settings, ulong seed)
    {
        var w = settings.Winds;
        double bandWidthDeg = w.BandWidthDeg;
        double halfBand = w.BandWidthDeg / 2.0;
        double peakStrength = w.PeakStrength;
        double strengthDrop = w.PeakStrength - w.EdgeStrength;
        var meridional = w.MeridionalShareBp / 10_000.0;
        var polarMeridional = w.PolarMeridionalShareBp / 10_000.0;
        var grid = layers.Grid;
        var geo = layers.Geometry;
        var n = grid.CellCount;
        var dir = new sbyte[n];
        var strength = new byte[n];

        for (var c = 0; c < n; c++)
        {
            var p = geo.Centre(c);
            var east = TangentFrame.East(p);
            var north = TangentFrame.North(p, east);
            var lat = geo.LatLon(c).LatDeg;
            var absLat = Math.Abs(ClimateStage.PerturbedLatitude(layers, c, seed, w.BandNoiseAmplitudeDeg, w.BandNoiseFrequency, w.BandNoiseOctaves));
            var poleward = lat >= 0 ? north : north * -1;
            var equatorward = poleward * -1;

            var band = Math.Min((int)(absLat / bandWidthDeg), 2);
            var intended = band switch
            {
                0 => ((east * -1) + (equatorward * meridional)).Normalize(),
                1 => (east + (poleward * meridional)).Normalize(),
                _ => ((east * -1) + (equatorward * polarMeridional)).Normalize(),
            };

            var tangents = TangentFrame.NeighbourDirections(grid, geo, c, p);
            dir[c] = (sbyte)TangentFrame.BestSlot(tangents, intended);

            var centre = (band * bandWidthDeg) + halfBand;
            var fromCentre = Math.Min(Math.Abs(absLat - centre) / halfBand, 1);
            var s = (int)Math.Round(peakStrength - (strengthDrop * fromCentre));

            var above = layers.ElevationM[c] - layers.SeaLevelM;
            if (!layers.IsOcean(c) && above > w.MountainThresholdM)
                s = Math.Max(w.MountainFloorStrength, s - (above / w.MountainLossDivisorM));

            strength[c] = (byte)Math.Clamp(s, 0, 255);
        }

        layers.WindDir = dir;
        layers.WindStrength = strength;
    }

    public static ulong Hash(WorldLayers l)
    {
        var d = new int[l.WindDir.Length];
        for (var i = 0; i < d.Length; i++)
            d[i] = l.WindDir[i];
        return DeterministicHash.Combine(LayerHash.Of(d), LayerHash.Of(l.WindStrength), 0, 0);
    }
}
