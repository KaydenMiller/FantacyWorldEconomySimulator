using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class PlateDynamicsTests
{
    private static void Steps(StepState s, int n)
    {
        for (var i = 0; i < n; i++)
        {
            s.StepMotionAndInteractions();
            PlateDynamics.Update(s);
        }
    }

    [Test]
    public void A_plate_subducting_under_its_neighbour_speeds_up()
    {
        var s = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Continental, t: new TectonicsSettings { PoleDriftDegPer100My = 0 });
        var start = Math.Abs(s.StepMicroRad[0]);
        Steps(s, 10);
        s.LastSubductingCells[0].Should().BePositive();
        Math.Abs(s.StepMicroRad[0]).Should().BeGreaterThan(start);
    }

    [Test]
    public void Colliding_continents_slow_down()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental,
            t: new TectonicsSettings { PoleDriftDegPer100My = 0, CollisionMaxSteps = 1_000, FusionMaxSpeedPermille = 0 });
        var start = Math.Abs(s.StepMicroRad[0]);
        Steps(s, 10);
        s.LastCollided.Should().Contain(0);
        Math.Abs(s.StepMicroRad[0]).Should().BeLessThan(start);
    }

    [Test]
    public void Speed_eases_toward_its_target()
    {
        var s = TwoPlates.Make(CrustKind.Continental, CrustKind.Continental,
            t: new TectonicsSettings { PoleDriftDegPer100My = 0, CollisionMaxSteps = 1_000, FusionMaxSpeedPermille = 0, SpeedEaseMy = 1_000 });
        var start = Math.Abs(s.StepMicroRad[0]);
        Steps(s, 3);
        ((double)Math.Abs(s.StepMicroRad[0])).Should().BeGreaterThan(start * 0.98, "a 1,000 My ease time changes speed by under 1% per 2 My step");
    }

    [Test]
    public void Poles_drift_every_ten_million_years_and_not_without_drift()
    {
        var drifting = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Oceanic, t: new TectonicsSettings { PoleDriftDegPer100My = 30 });
        var fixedPoles = TwoPlates.Make(CrustKind.Oceanic, CrustKind.Oceanic, t: new TectonicsSettings { PoleDriftDegPer100My = 0 });
        var a0 = drifting.Axis[0];
        Steps(drifting, 5);
        Steps(fixedPoles, 5);
        var angle = Math.Acos(Math.Clamp(Vector3d.Dot(a0, drifting.Axis[0]), -1, 1)) * 180 / Math.PI;
        angle.Should().BeInRange(0.5, 3.0 + 1e-9, "30° per 100 My is 3° per 10 My");
        fixedPoles.Axis[0].Should().Be(new Vector3d(0, -1, 0));
    }

    [Test]
    public void Continental_plates_have_a_lower_target_than_oceanic_ones()
    {
        var t = new TectonicsSettings();
        PlateDynamics.TargetFactor(t, subductingShare: 0, continentalShare: 1, inCollision: false)
            .Should().BeLessThan(PlateDynamics.TargetFactor(t, subductingShare: 0, continentalShare: 0, inCollision: false));
    }
}
