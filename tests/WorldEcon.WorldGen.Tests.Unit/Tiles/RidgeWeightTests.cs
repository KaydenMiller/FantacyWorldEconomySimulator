using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tectonics;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

public class RidgeWeightTests
{
    private static WorldLayers Land(int frequency = 8)
    {
        var grid = GlobeGrid.Build(frequency);
        var l = new WorldLayers(grid, GlobeGeometry.Compute(grid));
        var n = grid.CellCount;
        var crust = new CrustRecord(n);
        for (var c = 0; c < n; c++)
        {
            var s = crust.Allocate();
            crust.Kind[s] = CrustKind.Continental;
        }

        l.Crust = crust;
        l.ElevationM = Enumerable.Repeat(500, n).ToArray();
        l.SeaLevelM = 0;
        l.BoundaryKind = new byte[n];
        return l;
    }

    private static readonly TilesSettings Tiles = new() { RidgeMaxOrogenyAgeMy = 150 };

    [Test]
    public void Old_belt_inside_a_plate_gets_crests()
    {
        var l = Land();
        l.Crust!.LastOrogenyAgeMy[3] = 40;
        l.Crust.Orogeny[3] = OrogenyKind.Himalayan;
        var w = TileElevationStage.RidgeWeight(l, Tiles);
        w[3].Should().BeApproximately(1.0 * (1 - 40 / 150.0), 1e-9);
    }

    [Test]
    public void Belt_wider_than_one_cell_gets_crests_across_its_width()
    {
        var l = Land();
        foreach (var c in new[] { 3, 4, 5 })
        {
            l.Crust!.LastOrogenyAgeMy[c] = 0;
            l.Crust.Orogeny[c] = OrogenyKind.Andean;
        }

        var w = TileElevationStage.RidgeWeight(l, Tiles);
        new[] { 3, 4, 5 }.Should().OnlyContain(c => Math.Abs(w[c] - 0.8) < 1e-9);
    }

    [Test]
    public void Orogeny_older_than_the_limit_gets_none()
    {
        var l = Land();
        l.Crust!.LastOrogenyAgeMy[3] = 151;
        l.Crust.Orogeny[3] = OrogenyKind.Himalayan;
        TileElevationStage.RidgeWeight(l, Tiles)[3].Should().Be(0);
        TileElevationStage.RidgeWeight(l, Tiles)[4].Should().Be(0, "no orogeny at all");
    }

    [Test]
    public void Ocean_gets_none()
    {
        var l = Land();
        l.Crust!.LastOrogenyAgeMy[3] = 0;
        l.Crust.Orogeny[3] = OrogenyKind.Arc;
        l.ElevationM[3] = -100;
        TileElevationStage.RidgeWeight(l, Tiles)[3].Should().Be(0);
    }

    [Test]
    public void Arc_weight_is_half()
    {
        var l = Land();
        l.Crust!.LastOrogenyAgeMy[3] = 0;
        l.Crust.Orogeny[3] = OrogenyKind.Arc;
        TileElevationStage.RidgeWeight(l, Tiles)[3].Should().BeApproximately(0.5, 1e-9);
    }
}
