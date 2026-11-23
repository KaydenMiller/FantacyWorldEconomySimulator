using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>
/// Stage 4: gyre approximation of surface ocean currents (settings: <see cref="CurrentSettings"/>; defaults quoted).
/// Per ocean cell the intended flow is a tangent
/// vector: westward below 30° |latitude|, eastward 30–60°, westward above 60°. Between 0 and 60° |latitude|,
/// a cell whose westward neighbour is land (western margin) flows poleward and a cell whose eastward
/// neighbour is land (eastern margin) flows equatorward; a cell that is both uses the western-margin rule.
/// <see cref="WorldLayers.CurrentDir"/> is the neighbour slot best aligned with the intended vector (ties to
/// the lower slot). Margin cells get a temperature anomaly: +20…+60 tenths of °C for western (poleward)
/// margins, −20…−60 for eastern (equatorward) margins. Magnitude is 20 + 40 × (1 − |lat − 30°| / 10°)
/// for |lat| within 20–40° (peak 60 at 30°) and 20 elsewhere. Basin labelling is not built: the margin
/// rule is local (a neighbour test), so no basin id is consumed.
/// </summary>
public static class CurrentStage
{
    public static void Run(WorldLayers layers, WorldGenSettings settings)
    {
        var s = settings.Currents;
        double polarLatDeg = s.PolarLatDeg;
        double tropicLatDeg = s.TropicLatDeg;
        double peakLat = s.AnomalyPeakLatDeg;
        double halfWidth = s.AnomalyHalfWidthDeg;
        double bandLow = s.AnomalyPeakLatDeg - s.AnomalyHalfWidthDeg;
        double bandHigh = s.AnomalyPeakLatDeg + s.AnomalyHalfWidthDeg;
        var rise = s.AnomalyPeakDeciC - s.AnomalyBaseDeciC;
        var grid = layers.Grid;
        var geo = layers.Geometry;
        var n = grid.CellCount;
        var dir = new sbyte[n];
        var anomaly = new short[n];

        for (var c = 0; c < n; c++)
        {
            if (!layers.IsOcean(c))
            {
                dir[c] = -1;
                continue;
            }

            var p = geo.Centre(c);
            var east = TangentFrame.East(p);
            var north = TangentFrame.North(p, east);

            var lat = geo.LatLon(c).LatDeg;
            var absLat = Math.Abs(lat);
            var poleward = lat >= 0 ? north : north * -1;
            var neighbours = grid.Neighbours(c);

            // Westernmost / easternmost neighbour slots (most negative / positive dot with east).
            int west = 0, eastSlot = 0;
            double westDot = double.MaxValue, eastDot = double.MinValue;
            var tangents = TangentFrame.NeighbourDirections(grid, geo, c, p);
            for (var k = 0; k < neighbours.Length; k++)
            {
                var e = Vector3d.Dot(tangents[k], east);
                if (e < westDot) { westDot = e; west = k; }
                if (e > eastDot) { eastDot = e; eastSlot = k; }
            }

            Vector3d intended;
            var margin = 0; // +1 western (poleward), -1 eastern (equatorward)
            if (absLat <= polarLatDeg && !layers.IsOcean(neighbours[west]))
                margin = 1;
            else if (absLat <= polarLatDeg && !layers.IsOcean(neighbours[eastSlot]))
                margin = -1;

            if (margin == 1)
                intended = poleward;
            else if (margin == -1)
                intended = poleward * -1;
            else if (absLat < tropicLatDeg || absLat > polarLatDeg)
                intended = east * -1;
            else
                intended = east;

            var best = TangentFrame.BestSlot(tangents, intended);

            dir[c] = (sbyte)best;

            if (margin != 0)
            {
                var mag = absLat >= bandLow && absLat <= bandHigh
                    ? s.AnomalyBaseDeciC + (rise * (1 - (Math.Abs(absLat - peakLat) / halfWidth)))
                    : s.AnomalyBaseDeciC;
                anomaly[c] = (short)(margin * (int)Math.Round(mag));
            }
        }

        layers.CurrentDir = dir;
        layers.CurrentAnomalyDeciC = anomaly;
    }

    public static ulong Hash(WorldLayers l)
    {
        var d = new int[l.CurrentDir.Length];
        for (var i = 0; i < d.Length; i++)
            d[i] = l.CurrentDir[i];
        return DeterministicHash.Combine(LayerHash.Of(d), LayerHash.Of(l.CurrentAnomalyDeciC), 0, 0);
    }
}
