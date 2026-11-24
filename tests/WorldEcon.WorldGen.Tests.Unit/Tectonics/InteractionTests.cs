using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

/// <summary>Two plates on a small globe: plate 0 is the half with x &lt; 0, plate 1 the rest; they move toward each other along x.</summary>
internal static class TwoPlates
{
    public static StepState Make(CrustKind west, CrustKind east, int frequency = 16, int westAge = 100, int eastAge = 50,
        TectonicsSettings? t = null, double speedMmPerYr = 80, double? eastSpeedMmPerYr = null)
    {
        var grid = GlobeGrid.Build(frequency);
        var geo = GlobeGeometry.Compute(grid);
        var n = grid.CellCount;
        var plateOf = new int[n];
        var record = new CrustRecord(n);
        for (var c = 0; c < n; c++)
        {
            var s = record.Allocate();
            var isWest = geo.Centre(c).X < 0;
            plateOf[c] = isWest ? 0 : 1;
            var kind = isWest ? west : east;
            record.Kind[s] = kind;
            record.ThicknessDam[s] = kind == CrustKind.Oceanic ? CrustRecord.OceanicThicknessDam : CrustRecord.ContinentalThicknessDam;
            record.FormedAgeMy[s] = (ushort)(isWest ? westAge : eastAge);
        }

        // Rotations about ±y move plate 0 toward +x and plate 1 toward −x on the equator (they converge at x = 0, z > 0).
        var settings = new WorldGenSettings { Grid = new() { Frequency = frequency }, Tectonics = t ?? new TectonicsSettings() };
        return StepState.Create(grid, geo, settings, plateOf, record,
            [new Vector3d(0, -1, 0), new Vector3d(0, 1, 0)], [speedMmPerYr, eastSpeedMmPerYr ?? speedMmPerYr], seed: 42);
    }

    public static int Run(StepState s, int steps)
    {
        for (var i = 0; i < steps; i++) s.StepMotionAndInteractions();
        return steps;
    }
}

public class InteractionTests
{
    [Test]
    public void Ocean_continent_convergence_uplifts_the_continent_near_the_front_and_records_Andean()
    {
        var s = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Continental);
        TwoPlates.Run(s, 5);
        var before = s.Present();
        TwoPlates.Run(s, 1);
        var cells = s.Present();
        // Cells thickened in the last step (the front moves between steps, so earlier uplift is not checked here).
        var uplifted = Enumerable.Range(0, s.Grid.CellCount)
            .Where(c => cells.Kind[c] == CrustKind.Continental && cells.ThicknessDam[c] > before.ThicknessDam[c]).ToList();
        uplifted.Should().NotBeEmpty();
        uplifted.Should().OnlyContain(c => cells.Orogeny[c] == OrogenyKind.Andean && cells.ArcMagmaAgeMy[c] == 0);
        // Uplift stays within twice the profile width of a subduction front.
        uplifted.Should().OnlyContain(c => s.DistanceToPlate(c, plate: 0) <= 2 * s.Settings.Tectonics.SubductionUpliftCells + 1);
    }

    [Test]
    public void Older_ocean_subducts_under_younger_and_the_overriding_side_becomes_an_arc()
    {
        var s = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Oceanic, westAge: 150, eastAge: 20);
        TwoPlates.Run(s, 6);
        var cells = s.Present();
        Enumerable.Range(0, s.Grid.CellCount).Should().Contain(c => cells.Orogeny[c] == OrogenyKind.Arc && s.PlateOfCell[c] == 1);
        Enumerable.Range(0, s.Grid.CellCount).Should().NotContain(c => cells.Orogeny[c] == OrogenyKind.Arc && s.PlateOfCell[c] == 0);
    }

    [Test]
    public void Continent_collision_conserves_continental_volume()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental);
        var before = s.ContinentalVolumeDam();
        TwoPlates.Run(s, 4);
        s.ContinentalVolumeDam().Should().BeApproximately(before, before * 0.02, "collision moves thickness, it does not destroy it");
        var cells = s.Present();
        Enumerable.Range(0, s.Grid.CellCount).Should().Contain(c => cells.Orogeny[c] == OrogenyKind.Himalayan
            && (cells.Flags[c] & CrustFlags.CollisionGranite) != 0);
    }

    [Test]
    public void Collision_radius_grows_with_area_and_speed()
    {
        var t = new TectonicsSettings();
        StepState.CollisionRadiusCells(t, terraneCells: 400, closingCellsPerStep: 1).Should()
            .BeLessThan(StepState.CollisionRadiusCells(t, terraneCells: 1600, closingCellsPerStep: 1));
        StepState.CollisionRadiusCells(t, terraneCells: 400, closingCellsPerStep: 1).Should()
            .BeLessThan(StepState.CollisionRadiusCells(t, terraneCells: 400, closingCellsPerStep: 4));
    }

    [Test]
    public void Every_cell_has_crust_and_a_plate_after_steps()
    {
        var s = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Continental);
        TwoPlates.Run(s, 8);
        s.SlotOfCell.Should().OnlyContain(x => x >= 0);
        s.PlateOfCell.Should().OnlyContain(p => p == 0 || p == 1);
    }

    [Test]
    public void Diverging_plates_fill_gaps_with_new_ocean()
    {
        // Reverse speeds: plates move apart at x = 0 on the near side.
        var s = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Oceanic, speedMmPerYr: -80);
        TwoPlates.Run(s, 4);
        var cells = s.Present();
        Enumerable.Range(0, s.Grid.CellCount).Should().Contain(c => cells.FormedAgeMy[c] == 0);
    }

    [Test]
    public void Long_continental_contact_becomes_a_fusion_candidate()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental, t: new TectonicsSettings { CollisionMaxSteps = 3, FusionMaxSpeedPermille = 0 });
        TwoPlates.Run(s, 4);
        s.FusionCandidates().Should().Contain((0, 1));
    }

    [Test]
    public void Slow_continental_contact_is_a_fusion_candidate_at_once()
    {
        // The east plate rotates the same way, a little slower: the west plate catches up at 20 mm/yr relative speed.
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental, t: new TectonicsSettings { CollisionMaxSteps = 1_000, FusionMaxSpeedPermille = 500 },
            speedMmPerYr: 80, eastSpeedMmPerYr: -60);
        TwoPlates.Run(s, 3);
        s.FusionCandidates().Should().Contain((0, 1));
    }
}
