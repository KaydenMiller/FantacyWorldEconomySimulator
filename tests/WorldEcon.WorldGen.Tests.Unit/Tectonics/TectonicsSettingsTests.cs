using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class TectonicsSettingsTests
{
    [Test]
    public void Defaults_validate()
    {
        new WorldGenSettings().Validate().IsError.Should().BeFalse();
    }

    [Test]
    public void Tectonics_defaults_match_the_design()
    {
        var t = new WorldGenSettings().Tectonics;
        t.StartArrangement.Should().Be(0);
        t.CratonCount.Should().Be(10);
        t.SimulatedMy.Should().Be(250);
        t.StepMy.Should().Be(2);
        t.FrameEverySteps.Should().Be(10);
        t.SubductionUpliftCells.Should().Be(2);
        t.ErosionEFoldMy.Should().Be(150, "calibrated for land spread (design start value 200)");
        t.FusionMaxSpeedPermille.Should().Be(200);
        t.CollisionMaxSteps.Should().Be(20);
        t.TopographyNoiseM.Should().Be(150);
        t.FailedRiftBp.Should().Be(3000);
    }

    [Test]
    public void Plate_speeds_are_in_millimetres_per_year()
    {
        var p = new WorldGenSettings().Plates;
        p.MinSpeedMmPerYr.Should().Be(10);
        p.MaxSpeedMmPerYr.Should().Be(100);
    }

    [Test]
    public void Old_angular_speed_keys_are_unknown_settings()
    {
        var act = () => WorldGenSettingsJson.Deserialize("""{"Plates":{"MinAngularSpeedPermille":200}}""");
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("unknown setting 'Plates.MinAngularSpeedPermille'");
    }

    [Test]
    public void Step_longer_than_history_is_invalid()
    {
        var s = new WorldGenSettings { Tectonics = new() { SimulatedMy = 10, StepMy = 20 } };
        s.Validate().Errors.Select(e => e.Code).Should().Contain("settings.Tectonics.SimulatedMy");
    }

    [Test]
    public void Speed_min_above_max_is_invalid()
    {
        var s = new WorldGenSettings { Plates = new() { MinSpeedMmPerYr = 101 } };
        s.Validate().Errors.Select(e => e.Code).Should().Equal("settings.Plates.MaxSpeedMmPerYr");
    }

    [Test]
    public void Ridge_orogeny_age_limit_exists()
    {
        new WorldGenSettings().Tiles.RidgeMaxOrogenyAgeMy.Should().BeGreaterThan(0);
    }
}

public class PlateDynamicsSettingsTests
{
    [Test]
    public void Dynamics_and_plate_count_defaults()
    {
        var t = new WorldGenSettings().Tectonics;
        t.SlabPullBp.Should().Be(20_000);
        t.ContinentalDragBp.Should().Be(5_000);
        t.CollisionDragBp.Should().Be(7_000);
        t.SpeedEaseMy.Should().Be(20);
        t.PoleDriftDegPer100My.Should().Be(15);
        t.TargetPlateCount.Should().Be(16);
        t.OceanicRiftShareBp.Should().Be(10_000, "calibrated: 3,000 gave a median of 8 plates, 10,000 gives 12 (seeds 1-20)");
        new WorldGenSettings().Validate().IsError.Should().BeFalse();
    }

    [Test]
    public void Out_of_range_dynamics_values_are_invalid()
    {
        new WorldGenSettings { Tectonics = new() { CollisionDragBp = 10_001 } }.Validate().Errors.Select(e => e.Code)
            .Should().Contain("settings.Tectonics.CollisionDragBp");
        new WorldGenSettings { Tectonics = new() { TargetPlateCount = 0 } }.Validate().Errors.Select(e => e.Code)
            .Should().Contain("settings.Tectonics.TargetPlateCount");
    }
}
