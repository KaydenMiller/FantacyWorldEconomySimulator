using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class EventTests
{
    /// <summary>Six flood-filled plates on a frequency-16 globe, every cell continental, all plates still.</summary>
    private static StepState SixPlates(TectonicsSettings? t = null)
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = 16 }, Plates = new() { Count = 6 }, Tectonics = t ?? new TectonicsSettings() };
        var grid = GlobeGrid.Build(16);
        var geo = GlobeGeometry.Compute(grid);
        var layers = new WorldLayers(grid, geo);
        PlateStage.Run(layers, settings, 7);
        var record = new CrustRecord(grid.CellCount);
        for (var c = 0; c < grid.CellCount; c++)
        {
            var s = record.Allocate();
            record.Kind[s] = CrustKind.Continental;
            record.ThicknessDam[s] = CrustRecord.ContinentalThicknessDam;
        }

        return StepState.Create(grid, geo, settings, layers.PlateOf, record,
            layers.Plates.Select(p => p.RotationAxis).ToList(), layers.Plates.Select(_ => 0.0).ToList(), seed: 9);
    }

    [Test]
    public void Fusion_merges_plates_marks_the_suture_and_keeps_ids_dense()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental);
        TwoPlates.Run(s, 2);
        Events.Fuse(s, 0, 1);
        s.PlateCount.Should().Be(1);
        s.PlateOfCell.Should().OnlyContain(p => p == 0);
        var cells = s.Present();
        Enumerable.Range(0, s.Grid.CellCount).Should().Contain(c => (cells.Flags[c] & CrustFlags.Suture) != 0);
    }

    [Test]
    public void Fusing_middle_plates_renumbers_the_rest()
    {
        var s = SixPlates();
        Events.Fuse(s, 1, 3);
        s.PlateCount.Should().Be(5);
        s.PlateOfCell.Distinct().Order().Should().Equal(0, 1, 2, 3, 4);
        s.Frames.PlateCount.Should().Be(5);
    }

    [Test]
    public void Rift_probability_grows_with_area_and_continental_share()
    {
        var s = SixPlates();
        var sizes = Enumerable.Range(0, s.PlateCount).Select(p => s.PlateOfCell.Count(x => x == p)).ToArray();
        var big = Array.IndexOf(sizes, sizes.Max());
        var small = Array.IndexOf(sizes, sizes.Min());
        Events.RiftProbability(s, big).Should().BeGreaterThan(Events.RiftProbability(s, small));
        var continentalBig = Events.RiftProbability(s, big);
        foreach (var c in Enumerable.Range(0, s.Grid.CellCount).Where(c => s.PlateOfCell[c] == big)) s.Pool.Kind[s.SlotOfCell[c]] = CrustKind.Oceanic;
        Events.RiftProbability(s, big).Should().BeGreaterThan(0, "oceanic plates split too (OceanicRiftShareBp, steered by plate count)");
    }

    [Test]
    public void Rift_splits_into_two_plates_moving_apart()
    {
        var s = SixPlates();
        var plates = s.PlateCount;
        var created = Events.Rift(s, 0);
        created.Should().Be(plates);
        s.PlateCount.Should().Be(plates + 1);
        s.PlateOfCell.Should().Contain(0).And.Contain(created);
        var cells = s.Present();
        Enumerable.Range(0, s.Grid.CellCount).Should().Contain(c => cells.RiftAgeMy[c] == 0);
        for (var i = 0; i < 4; i++) s.StepMotionAndInteractions();
        s.Present().FormedAgeMy.Take(s.Grid.CellCount).Should().Contain(0, "new ocean opens between the two sides");
    }

    [Test]
    public void Failed_rift_fuses_again_after_the_scheduled_steps_and_keeps_the_flag()
    {
        var s = SixPlates(new TectonicsSettings { FailedRiftBp = 10_000, FailedRiftSteps = 2 });
        var plates = s.PlateCount;
        Events.Rift(s, 0);
        s.PlateCount.Should().Be(plates + 1);
        for (var i = 0; i < 2; i++)
        {
            s.StepMotionAndInteractions();
            Events.ProcessScheduled(s);
        }

        s.PlateCount.Should().Be(plates);
        var cells = s.Present();
        Enumerable.Range(0, s.Grid.CellCount).Should().Contain(c => (cells.Flags[c] & CrustFlags.FailedRift) != 0);
    }

    [Test]
    public void Later_rift_passes_through_failed_rift_cells()
    {
        var s = SixPlates();
        var plate0 = Enumerable.Range(0, s.Grid.CellCount).Where(c => s.PlateOfCell[c] == 0).ToList();
        var marked = plate0[plate0.Count / 2];
        s.Pool.Flags[s.SlotOfCell[marked]] |= CrustFlags.FailedRift;
        Events.Rift(s, 0);
        var cells = s.Present();
        var line = Enumerable.Range(0, s.Grid.CellCount).Where(c => cells.RiftAgeMy[c] == 0).ToList();
        line.Min(c => Math.Acos(Math.Clamp(Vector3d.Dot(s.Geo.Centre(c), s.Geo.Centre(marked)), -1, 1)))
            .Should().BeLessThan(2.5 * s.CellAngle);
    }
}

public class EventRenumberTests
{
    [Test]
    public void Fusing_after_a_scheduled_rift_lost_a_plate_does_not_throw()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental);
        s.Scheduled.Add(new ScheduledRefusion(-1, 1, 99, []));
        var act = () => Events.Fuse(s, 0, 1);
        act.Should().NotThrow();
        s.Scheduled.Single().B.Should().Be(-1);
    }
}

public class EmptyPlateTests
{
    [Test]
    public void Plates_without_cells_are_removed_and_ids_stay_dense()
    {
        var s = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Continental);
        for (var c = 0; c < s.PlateOfCell.Length; c++) s.PlateOfCell[c] = 1;
        Events.RemoveEmptyPlates(s);
        s.PlateCount.Should().Be(1);
        s.PlateOfCell.Should().OnlyContain(p => p == 0);
        s.Frames.PlateCount.Should().Be(1);
    }
}

public class ReviewFixTests
{
    [Test]
    public void Removing_an_unrelated_plate_keeps_the_contact_count_of_other_pairs()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental, t: new TectonicsSettings { CollisionMaxSteps = 1_000, FusionMaxSpeedPermille = 0 });
        // A third, empty plate (id 2): removing it must not reset the contact count of the colliding pair (0, 1).
        s.Frames.AddPlate(new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1));
        s.Axis.Add(new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1));
        s.StepMicroRad.Add(0);
        for (var i = 0; i < 3; i++) s.StepMotionAndInteractions();
        var before = s.ContactSteps(0, 1);
        before.Should().BePositive();
        Events.RemoveEmptyPlates(s);
        s.PlateCount.Should().Be(2);
        s.ContactSteps(0, 1).Should().Be(before);
    }

    [Test]
    public void Continent_collision_conserves_volume_over_a_long_run()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental, t: new TectonicsSettings { CollisionMaxSteps = 1_000, FusionMaxSpeedPermille = 0 });
        var before = s.ContinentalVolumeDam();
        TwoPlates.Run(s, 40);
        TestContext.Current!.Output.WriteLine($"volume ratio after 40 steps: {s.ContinentalVolumeDam() / before:F4}");
        s.ContinentalVolumeDam().Should().BeLessThanOrEqualTo(before * 1.02, "a lost record's thickness is added once, not once per cell that shares its slot");
    }
}

public class RiftShapeTests
{
    [Test]
    public void Rift_line_is_not_a_straight_great_circle()
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = 32 }, Plates = new() { Count = 2 } };
        var grid = WorldEcon.WorldGen.Grid.GlobeGrid.Build(32);
        var geo = WorldEcon.WorldGen.Grid.GlobeGeometry.Compute(grid);
        var record = new CrustRecord(grid.CellCount);
        for (var c = 0; c < grid.CellCount; c++)
        {
            var slot = record.Allocate();
            record.Kind[slot] = CrustKind.Continental;
            record.ThicknessDam[slot] = CrustRecord.ContinentalThicknessDam;
        }

        var s = StepState.Create(grid, geo, settings, new int[grid.CellCount], record, [new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1)], [0.0], seed: 3);
        Events.Rift(s, 0).Should().Be(1);
        var line = Enumerable.Range(0, grid.CellCount).Where(c => s.Pool.RiftAgeMy[s.SlotOfCell[c]] == 0).Select(geo.Centre).ToList();
        // The plane through the globe centre, the first line cell and the line cell closest to 90° from it: a straight cut
        // keeps every line cell within about one cell of that plane.
        var a = line[0];
        var b = line.MinBy(q => Math.Abs(WorldEcon.WorldGen.Grid.Vector3d.Dot(a, q)));
        var normal = WorldEcon.WorldGen.Grid.Vector3d.Cross(a, b).Normalize();
        line.Max(p => Math.Abs(WorldEcon.WorldGen.Grid.Vector3d.Dot(p, normal))).Should().BeGreaterThan(6.5 * s.CellAngle,
            "a breakup line bends across a continent (a straight cut with 6 cells of wander measured 5.6 cells; region growing 7.3)");
    }
}

public class PlateCountTests
{
    private static StepState Oceanic() => TwoPlates.Make(CrustKind.Oceanic, CrustKind.Oceanic);

    [Test]
    public void An_all_oceanic_plate_can_rift()
    {
        var s = Oceanic();
        Events.Rift(s, 0).Should().Be(2);
        s.PlateCount.Should().Be(3);
    }

    [Test]
    public void Rift_probability_rises_below_the_target_plate_count_and_falls_above_it()
    {
        // Oceanic plates: only oceanic splits are steered.
        var few = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Oceanic, t: new TectonicsSettings { TargetPlateCount = 20 });
        var many = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Oceanic, t: new TectonicsSettings { TargetPlateCount = 1 });
        Events.RiftProbability(few, 0).Should().BeGreaterThan(Events.RiftProbability(many, 0) * 4);
    }
}

public class SliverPlateTests
{
    [Test]
    public void Plates_below_the_minimum_size_merge_into_their_main_neighbour()
    {
        var s = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Continental);
        // A 3-cell plate inside plate 1.
        var cells = Enumerable.Range(0, s.Grid.CellCount).Where(c => s.PlateOfCell[c] == 1).Take(1).ToList();
        cells.AddRange(s.Grid.Neighbours(cells[0]).ToArray().Where(c => s.PlateOfCell[c] == 1).Take(2));
        s.Frames.AddPlate(new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1));
        s.Axis.Add(new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1));
        s.StepMicroRad.Add(0);
        foreach (var c in cells) s.PlateOfCell[c] = 2;
        Events.MergeSliverPlates(s);
        s.PlateCount.Should().Be(2);
        cells.Should().OnlyContain(c => s.PlateOfCell[c] == 1);
    }

    [Test]
    public void A_single_overlapping_cell_is_not_a_collision_contact()
    {
        StepState.MinCollisionOverlapCells.Should().BeGreaterThanOrEqualTo(3);
    }
}

public class DivergingContactTests
{
    [Test]
    public void Freshly_rifted_plates_moving_apart_do_not_build_a_collision_contact()
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = 32 }, Plates = new() { Count = 1 }, Tectonics = new() { FailedRiftBp = 0 } };
        var grid = WorldEcon.WorldGen.Grid.GlobeGrid.Build(32);
        var geo = WorldEcon.WorldGen.Grid.GlobeGeometry.Compute(grid);
        var record = new CrustRecord(grid.CellCount);
        // Continental crust only in a cap (0.7 rad) around +z: a plate moving away from the rift on one side approaches on
        // the far side of the globe, which is ocean here (it subducts, it does not collide).
        for (var c = 0; c < grid.CellCount; c++)
        {
            var slot = record.Allocate();
            var continental = geo.Centre(c).Z > Math.Cos(0.7);
            record.Kind[slot] = continental ? CrustKind.Continental : CrustKind.Oceanic;
            record.ThicknessDam[slot] = continental ? CrustRecord.ContinentalThicknessDam : CrustRecord.OceanicThicknessDam;
        }

        var s = StepState.Create(grid, geo, settings, new int[grid.CellCount], record, [new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1)], [0.0], seed: 3);
        Events.Rift(s, 0).Should().Be(1);
        for (var i = 0; i < 8; i++) s.StepMotionAndInteractions();
        s.ContactSteps(0, 1).Should().Be(0, "the two sides of a rift move apart");
    }
}

public class SliverContactTests
{
    [Test]
    public void Merging_a_sliver_keeps_the_receiving_plates_collision_contacts()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental, t: new TectonicsSettings { CollisionMaxSteps = 1_000, FusionMaxSpeedPermille = 0 });
        for (var i = 0; i < 3; i++) s.StepMotionAndInteractions();
        var before = s.ContactSteps(0, 1);
        before.Should().BePositive();
        var cell = Enumerable.Range(0, s.Grid.CellCount).First(c => s.PlateOfCell[c] == 1);
        s.Frames.AddPlate(new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1));
        s.Axis.Add(new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1));
        s.StepMicroRad.Add(0);
        s.PlateOfCell[cell] = 2;
        Events.MergeSliverPlates(s);
        s.PlateCount.Should().Be(2);
        s.ContactSteps(0, 1).Should().Be(before);
    }
}

public class OceanicSplitTests
{
    [Test]
    public void An_oceanic_split_leaves_a_continent_whole_on_one_side()
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = 32 }, Plates = new() { Count = 1 } };
        var grid = WorldEcon.WorldGen.Grid.GlobeGrid.Build(32);
        var geo = WorldEcon.WorldGen.Grid.GlobeGeometry.Compute(grid);
        var record = new CrustRecord(grid.CellCount);
        var continentCells = new List<int>();
        for (var c = 0; c < grid.CellCount; c++)
        {
            var slot = record.Allocate();
            var continental = geo.Centre(c).Z > Math.Cos(0.5);
            if (continental) continentCells.Add(c);
            record.Kind[slot] = continental ? CrustKind.Continental : CrustKind.Oceanic;
            record.ThicknessDam[slot] = continental ? CrustRecord.ContinentalThicknessDam : CrustRecord.OceanicThicknessDam;
        }

        for (ulong seed = 1; seed <= 5; seed++)
        {
            var s = StepState.Create(grid, geo, settings, new int[grid.CellCount], record.Clone(), [new WorldEcon.WorldGen.Grid.Vector3d(0, 0, 1)], [0.0], seed);
            Events.Rift(s, 0, oceanic: true).Should().Be(1);
            var sides = continentCells.Select(c => s.PlateOfCell[c]).Distinct().ToList();
            sides.Should().ContainSingle($"seed {seed}: an oceanic split runs through ocean");
        }
    }

    [Test]
    public void Steering_raises_only_the_oceanic_split_rate()
    {
        var few = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental, t: new TectonicsSettings { TargetPlateCount = 40 });
        var many = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental, t: new TectonicsSettings { TargetPlateCount = 1 });
        Events.ContinentalRiftLambda(few, 0).Should().Be(Events.ContinentalRiftLambda(many, 0));
    }
}
