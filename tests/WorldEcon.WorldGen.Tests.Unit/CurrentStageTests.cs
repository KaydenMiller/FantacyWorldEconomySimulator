using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class CurrentStageTests
{
    private static readonly WorldGenSettings Settings = new() { Grid = new() { Frequency = 16 } };

    // landBand: land where lon in [0, 60] and |lat| <= 60; null = all ocean.
    private static WorldLayers Build(int m, bool landBand)
    {
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        var l = new WorldLayers(grid, geo) { ElevationM = new int[grid.CellCount], SeaLevelM = 0 };
        for (var c = 0; c < grid.CellCount; c++)
        {
            var (lat, lon) = geo.LatLon(c);
            var land = landBand && lon is >= 0 and <= 60 && Math.Abs(lat) <= 60;
            l.ElevationM[c] = land ? 100 : -1000;
        }

        return l;
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(4, true)]
    [Arguments(16, true)]
    [Arguments(1, false)]
    [Arguments(8, false)]
    public void Ocean_dir_is_a_valid_slot_and_land_is_minus_one(int m, bool landBand)
    {
        var l = Build(m, landBand);
        CurrentStage.Run(l, Settings);
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            if (l.IsOcean(c))
                l.CurrentDir[c].Should().BeInRange(0, (sbyte)(l.Grid.Neighbours(c).Length - 1));
            else
            {
                l.CurrentDir[c].Should().Be(-1);
                l.CurrentAnomalyDeciC[c].Should().Be(0);
            }
        }
    }

    [Test]
    public void All_ocean_world_has_no_anomalies()
    {
        var l = Build(16, false);
        CurrentStage.Run(l, Settings);
        l.CurrentAnomalyDeciC.Should().OnlyContain(a => a == 0);
    }

    [Test]
    public void Northern_margins_have_signed_anomalies()
    {
        var l = Build(16, true);
        CurrentStage.Run(l, Settings);
        var geo = l.Geometry;
        int west = 0, eastern = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            if (!l.IsOcean(c)) continue;
            var (lat, lon) = geo.LatLon(c);
            if (lat is < 20 or > 40) continue;
            // Land spans lon 0..60: cells just east of it have land to the west (western margin).
            var wlon = Neighbour(l, c, west: true);
            var elon = Neighbour(l, c, west: false);
            if (!l.IsOcean(wlon) && lon > 60)
            {
                l.CurrentAnomalyDeciC[c].Should().BeInRange(20, 60);
                west++;
            }
            else if (l.IsOcean(wlon) && !l.IsOcean(elon) && lon < 0)
            {
                l.CurrentAnomalyDeciC[c].Should().BeInRange(-60, -20);
                eastern++;
            }
        }

        west.Should().BeGreaterThan(0);
        eastern.Should().BeGreaterThan(0);
    }

    [Test]
    public void Western_margin_flows_poleward_and_eastern_equatorward()
    {
        var l = Build(16, true);
        CurrentStage.Run(l, Settings);
        var geo = l.Geometry;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            if (!l.IsOcean(c)) continue;
            var (lat, lon) = geo.LatLon(c);
            if (lat is < 20 or > 40) continue;
            var a = l.CurrentAnomalyDeciC[c];
            if (a == 0) continue;
            var nb = l.Grid.Neighbours(c)[l.CurrentDir[c]];
            var dLat = geo.LatLon(nb).LatDeg - lat;
            if (a > 0) dLat.Should().BeGreaterThan(0);
            else dLat.Should().BeLessThan(0);
        }
    }

    [Test]
    public void Open_ocean_tropics_flow_west_and_midlatitudes_east()
    {
        var l = Build(16, false);
        CurrentStage.Run(l, Settings);
        var geo = l.Geometry;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var (lat, _) = geo.LatLon(c);
            var a = Math.Abs(lat);
            if (a is > 80 or (> 28 and < 32) or (> 58 and < 62)) continue;
            var p = geo.Centre(c);
            var east = Vector3d.Cross(new Vector3d(0, 1, 0), p).Normalize();
            var nb = l.Grid.Neighbours(c)[l.CurrentDir[c]];
            var e = Vector3d.Dot(geo.Centre(nb) - p, east);
            if (a is < 30 or > 60) e.Should().BeLessThan(0);
            else e.Should().BeGreaterThan(0);
        }
    }

    [Test]
    public void Is_deterministic()
    {
        var a = Build(16, true);
        var b = Build(16, true);
        CurrentStage.Run(a, Settings);
        CurrentStage.Run(b, Settings);
        CurrentStage.Hash(a).Should().Be(CurrentStage.Hash(b));
        a.CurrentDir.Should().Equal(b.CurrentDir);
    }

    private static int Neighbour(WorldLayers l, int c, bool west)
    {
        var geo = l.Geometry;
        var p = geo.Centre(c);
        var east = Vector3d.Cross(new Vector3d(0, 1, 0), p).Normalize();
        var best = -1;
        var bestDot = west ? double.MaxValue : double.MinValue;
        foreach (var nb in l.Grid.Neighbours(c))
        {
            var d = Vector3d.Dot((geo.Centre(nb) - p).Normalize(), east);
            if (west ? d < bestDot : d > bestDot) { bestDot = d; best = nb; }
        }

        return best;
    }
}
