using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class MotionTests
{
    private static (GlobeGrid Grid, GlobeGeometry Geo) Globe(int frequency)
    {
        var g = GlobeGrid.Build(frequency);
        return (g, GlobeGeometry.Compute(g));
    }

    // Plate 0 west of the prime meridian plane x < 0, plate 1 the rest.
    private static int[] Halves(GlobeGeometry geo, int n) =>
        Enumerable.Range(0, n).Select(c => geo.Centre(c).X < 0 ? 0 : 1).ToArray();

    [Test]
    public void Rotation_moves_a_point_by_its_angle()
    {
        var r = new QuantisedRotation(new Vector3d(0, 0, 1), 0).Advance(500_000); // 0.5 rad about z
        var p = r.Apply(new Vector3d(1, 0, 0));
        p.X.Should().BeApproximately(Math.Cos(0.5), 1e-12);
        p.Y.Should().BeApproximately(Math.Sin(0.5), 1e-12);
        r.ApplyInverse(p).X.Should().BeApproximately(1, 1e-12);
    }

    [Test]
    public void Speed_converts_to_whole_micro_radians_per_step()
    {
        // 50 mm/yr = 50 km/My; over 2 My on a 6,371 km radius: 100/6371 rad.
        QuantisedRotation.StepMicroRad(50, 2, 6371).Should().Be((long)Math.Round(100.0 / 6371 * 1e6));
    }

    [Test]
    public void Mean_displacement_matches_the_speed()
    {
        var axis = new Vector3d(0, 0, 1);
        var r = new QuantisedRotation(axis, 0);
        var step = QuantisedRotation.StepMicroRad(50, 2, 6371);
        for (var i = 0; i < 125; i++) r = r.Advance(step);
        var moved = Math.Acos(Vector3d.Dot(r.Apply(new Vector3d(1, 0, 0)), new Vector3d(1, 0, 0)) ) ;
        // 125 × 2 My × 50 km/My = 12,500 km along the equator, modulo full turns (40,030 km).
        var km = (r.AngleMicroRad / 1e6) * 6371;
        km.Should().BeApproximately(12_500, 120);
        moved.Should().BeGreaterThan(0);
    }

    [Test]
    public void A_single_plate_claims_every_cell_exactly_once_after_moving()
    {
        var (grid, geo) = Globe(16);
        var frames = new PlateFrames(grid, geo, new int[grid.CellCount]);
        frames.SetAxis(0, new Vector3d(0.3, 0.4, 0.866).Normalize());
        for (var i = 0; i < 10; i++) frames.Advance(0, 20_000);
        var claims = frames.Claim();
        claims.Count.ToArray().Should().OnlyContain(c => c == 1);
    }

    [Test]
    public void Two_plates_claim_their_interiors_once_and_boundaries_at_most_twice()
    {
        var (grid, geo) = Globe(16);
        var plateOf = Halves(geo, grid.CellCount);
        var frames = new PlateFrames(grid, geo, plateOf);
        frames.SetAxis(0, new Vector3d(0, 1, 0));
        frames.SetAxis(1, new Vector3d(0, -1, 0));
        frames.Advance(0, 30_000);
        frames.Advance(1, 30_000);
        var claims = frames.Claim();
        claims.Count.ToArray().Should().OnlyContain(c => c >= 0 && c <= 2);
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (Math.Abs(geo.Centre(c).X) > 0.3) claims.Count[c].Should().Be(1, $"cell {c} is deep inside a plate");
            if (claims.Count[c] == 2) claims.PlateA[c].Should().BeLessThan(claims.PlateB[c], "ties are ordered by plate id");
        }
    }

    [Test]
    public void Same_input_same_claims_parallel_and_serial()
    {
        var (grid, geo) = Globe(16);
        var plateOf = Halves(geo, grid.CellCount);
        PlateFrames Make()
        {
            var f = new PlateFrames(grid, geo, plateOf);
            f.SetAxis(0, new Vector3d(0, 1, 0));
            f.SetAxis(1, new Vector3d(1, 1, 0).Normalize());
            f.Advance(0, 40_000);
            f.Advance(1, -25_000);
            return f;
        }

        var serial = Make().Claim(new ParallelOptions { MaxDegreeOfParallelism = 1 });
        var parallel = Make().Claim(new ParallelOptions { MaxDegreeOfParallelism = 8 });
        parallel.Count.ToArray().Should().Equal(serial.Count.ToArray());
        parallel.FrameA.ToArray().Should().Equal(serial.FrameA.ToArray());
        parallel.PlateB.ToArray().Should().Equal(serial.PlateB.ToArray());
    }

    [Test]
    public void Rebase_keeps_present_positions()
    {
        var (grid, geo) = Globe(16);
        var frames = new PlateFrames(grid, geo, Halves(geo, grid.CellCount));
        frames.SetAxis(0, new Vector3d(0, 1, 0));
        frames.Advance(0, 50_000);
        var before = frames.Claim();
        var present = new int[grid.CellCount];
        for (var c = 0; c < present.Length; c++) present[c] = before.Count[c] == 0 ? 1 : before.PlateA[c];
        frames.Rebase(0, present);
        frames.Rotation(0).AngleMicroRad.Should().Be(0);
        var after = frames.Claim();
        for (var c = 0; c < present.Length; c++)
            if (before.Count[c] == 1 && before.PlateA[c] == 0) after.PlateA[c].Should().Be(0);
    }

    [Test]
    public void Frame_slot_of_a_claim_identifies_the_crust_it_carries()
    {
        var (grid, geo) = Globe(8);
        var frames = new PlateFrames(grid, geo, new int[grid.CellCount]);
        var claims = frames.Claim();
        for (var c = 0; c < grid.CellCount; c++) frames.SlotOf(0, claims.FrameA[c]).Should().Be(c, "before any move slot = cell");
    }
}
