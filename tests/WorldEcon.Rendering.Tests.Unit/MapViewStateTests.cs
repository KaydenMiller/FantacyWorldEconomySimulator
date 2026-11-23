using System.Numerics;
using FluentAssertions;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>Projection, level choice and camera operations of <see cref="MapViewState"/>.</summary>
public class MapViewStateTests
{
    // Region cell ~0.02 rad (about 40,962 cells scale), tile ~0.0008 rad.
    private const double RegionAngle = 0.02;
    private const double TileAngle = 0.0008;

    private static MapViewState At(double scale, Vector3d? centre = null, double w = 1000, double h = 800) =>
        new(centre ?? new Vector3d(0.3, 0.5, 0.8), scale, w, h, RegionAngle, TileAngle);

    // Scale giving a flatness f for RegionAngle: region px = 30 + 60 * t where smoothstep(t) = f.
    private static double ScaleForFlatness(double f)
    {
        double lo = 0, hi = 1;
        for (var i = 0; i < 60; i++)
        {
            var m = (lo + hi) / 2;
            if (m * m * (3 - (2 * m)) < f) lo = m;
            else hi = m;
        }

        return (30 + (60 * (lo + hi) / 2)) / RegionAngle;
    }

    private static Vector3d RandomUnit(Random rng)
    {
        while (true)
        {
            var v = new Vector3d((rng.NextDouble() * 2) - 1, (rng.NextDouble() * 2) - 1, (rng.NextDouble() * 2) - 1);
            if (v.Length is > 0.1 and <= 1) return v.Normalize();
        }
    }

    [Test]
    [Arguments(0.0)]
    [Arguments(0.5)]
    [Arguments(1.0)]
    public void Project_unproject_round_trips_for_visible_points(double f)
    {
        var scale = f == 0 ? 400 : ScaleForFlatness(f);
        var view = At(scale);
        view.Flatness.Should().BeApproximately(f, 1e-6);
        var rng = new Random(7);
        var tested = 0;
        for (var i = 0; i < 4000 && tested < 500; i++)
        {
            var p = RandomUnit(rng);
            if (view.Project(p) is not { } s) continue;
            tested++;
            var back = view.Unproject(s.X, s.Y);
            back.Should().NotBeNull();
            var again = view.Project(back!.Value)!.Value;
            Math.Sqrt(Math.Pow(again.X - s.X, 2) + Math.Pow(again.Y - s.Y, 2)).Should().BeLessThan(1e-6);
        }

        tested.Should().BeGreaterThan(100);
    }

    [Test]
    [Arguments(0.0)]
    [Arguments(0.3)]
    [Arguments(0.7)]
    public void Project_unproject_round_trips_near_the_visibility_limit(double f)
    {
        var scale = f == 0 ? 400 : ScaleForFlatness(f);
        var view = At(scale);
        foreach (var frac in new[] { 0.5, 0.9, 0.999 })
        {
            var theta = view.MaxAngle * frac;
            var perp = Vector3d.Cross(view.Centre, new Vector3d(0, 0, 1)).Normalize();
            var p = ((view.Centre * Math.Cos(theta)) + (perp * Math.Sin(theta))).Normalize();
            var s = view.Project(p)!.Value;
            var back = view.Unproject(s.X, s.Y)!.Value;
            var again = view.Project(back)!.Value;
            Math.Sqrt(Math.Pow(again.X - s.X, 2) + Math.Pow(again.Y - s.Y, 2)).Should().BeLessThan(1e-6);
        }
    }

    [Test]
    public void Centre_maps_to_the_screen_centre_and_back()
    {
        foreach (var f in new[] { 0.0, 0.5, 1.0 })
        {
            var view = At(f == 0 ? 400 : ScaleForFlatness(f));
            var s = view.Project(view.Centre)!.Value;
            s.X.Should().BeApproximately(500, 1e-9);
            s.Y.Should().BeApproximately(400, 1e-9);
            (view.Unproject(500, 400)!.Value - view.Centre).Length.Should().BeLessThan(1e-12);
        }
    }

    [Test]
    public void Screen_axes_put_north_up_and_east_right()
    {
        var view = new MapViewState(new Vector3d(0, 0, 1), 400, 1000, 800, RegionAngle, TileAngle);
        var up = view.Project(new Vector3d(0, 0.1, 1).Normalize())!.Value;
        up.Y.Should().BeLessThan(400);
        up.X.Should().BeApproximately(500, 1e-9);
        var right = view.Project(new Vector3d(0.1, 0, 1).Normalize())!.Value;
        right.X.Should().BeGreaterThan(500);
        right.Y.Should().BeApproximately(400, 1e-9);
    }

    [Test]
    public void Sphere_hides_beyond_90_degrees_and_the_cap_widens_with_flatness()
    {
        var sphere = At(400);
        sphere.Flatness.Should().Be(0);
        sphere.MaxAngle.Should().BeApproximately(Math.PI / 2, 1e-12);
        var perp = Vector3d.Cross(sphere.Centre, new Vector3d(0, 0, 1)).Normalize();
        Vector3d AtAngle(double theta) => ((sphere.Centre * Math.Cos(theta)) + (perp * Math.Sin(theta))).Normalize();
        sphere.Project(AtAngle((Math.PI / 2) - 0.001)).Should().NotBeNull();
        sphere.Project(AtAngle((Math.PI / 2) + 0.001)).Should().BeNull();
        sphere.Unproject(500 + 401, 400).Should().BeNull();
        (sphere.Unproject(500 + 399.999, 400) is not null).Should().BeTrue();

        var prev = sphere.MaxAngle;
        foreach (var f in new[] { 0.1, 0.25, 0.4, 0.5, 0.75, 1.0 })
        {
            var a = MapViewState.MaxAngleFor(f);
            a.Should().BeGreaterThanOrEqualTo(prev);
            if (f <= 0.5) a.Should().BeGreaterThan(prev);
            a.Should().BeLessThanOrEqualTo(Math.PI + 1e-12);
            a.Should().BeLessThanOrEqualTo((Math.PI / 2) + (Math.PI * f) + 1e-12);
            prev = a;
        }

        MapViewState.MaxAngleFor(1.0).Should().BeApproximately(Math.PI, 1e-12);
        var flat = At(ScaleForFlatness(1.0));
        flat.Project(flat.Centre * -1).Should().NotBeNull();
    }

    [Test]
    public void Screen_radius_is_monotone_in_angle_over_the_visible_range()
    {
        foreach (var f in new[] { 0.0, 0.2, 0.45, 0.5, 0.8, 1.0 })
        {
            var view = At(f == 0 ? 400 : ScaleForFlatness(f));
            var prev = -1.0;
            for (var i = 0; i <= 1000; i++)
            {
                var r = view.RadiusAt(view.MaxAngle * i / 1000);
                r.Should().BeGreaterThanOrEqualTo(prev);
                prev = r;
            }
        }
    }

    [Test]
    public void Level_and_flatness_follow_the_pixel_thresholds()
    {
        // Region cell px = scale * 0.02; tile px = scale * 0.0008.
        var globe = At(1000); // cell 20 px
        globe.Level.Should().Be(MapLevel.Globe);
        globe.Flatness.Should().Be(0);

        At(1499).Level.Should().Be(MapLevel.Globe); // 29.98 px
        var region = At(1500); // 30 px exactly
        region.Level.Should().Be(MapLevel.Region);
        region.Flatness.Should().Be(0);

        var mid = At(60 / RegionAngle); // 60 px: t = 0.5, smoothstep = 0.5
        mid.Level.Should().Be(MapLevel.Region);
        mid.Flatness.Should().BeApproximately(0.5, 1e-9);

        At(90 / RegionAngle).Flatness.Should().BeApproximately(1, 1e-12);
        At(20000).Flatness.Should().Be(1); // 400 px cell

        At((22 / TileAngle) - 1).Level.Should().Be(MapLevel.Region); // 27,499 -> 21.9992 px
        At(22 / TileAngle).Level.Should().Be(MapLevel.Detail);
        At(22 / TileAngle).Flatness.Should().Be(1);
    }

    [Test]
    public void Custom_thresholds_change_the_levels()
    {
        var t = new MapLevelThresholds(DetailMinTilePx: 10, RegionMinCellPx: 15, FlatStartPx: 15, FlatFullPx: 45);
        new MapViewState(new Vector3d(0, 0, 1), 10 / TileAngle, 1000, 800, RegionAngle, TileAngle, t).Level.Should().Be(MapLevel.Detail);
        new MapViewState(new Vector3d(0, 0, 1), 15 / RegionAngle, 1000, 800, RegionAngle, TileAngle, t).Level.Should().Be(MapLevel.Region);
    }

    [Test]
    public void Mean_angular_spacing_matches_the_sphere_area_formula()
    {
        MapViewState.MeanAngularSpacing(40962).Should().BeApproximately(Math.Sqrt(4 * Math.PI / 40962), 1e-15);
        MapViewState.MeanAngularSpacing(40962).Should().BeApproximately(0.0175, 0.0001);
    }

    [Test]
    public void Scale_is_clamped_between_whole_globe_and_a_200_px_tile()
    {
        var view = At(1000);
        view.WithScale(1).ScalePxPerRad.Should().BeApproximately(0.45 * 800, 1e-9);
        view.WithScale(1e9).ScalePxPerRad.Should().BeApproximately(200 / TileAngle, 1e-6);
        view.ZoomAt(1e-6, 100, 100).ScalePxPerRad.Should().BeApproximately(0.45 * 800, 1e-9);
    }

    [Test]
    [Arguments(2000.0, 1.5, 620.0, 300.0)]
    [Arguments(2000.0, 0.8, 380.0, 500.0)]
    [Arguments(4000.0, 3.0, 700.0, 350.0)]
    [Arguments(10000.0, 2.0, 450.0, 420.0)]
    [Arguments(500.0, 1.5, 560.0, 380.0)]
    [Arguments(800.0, 0.5, 300.0, 200.0)]
    public void ZoomAt_keeps_the_point_under_the_cursor_fixed(double startScale, double factor, double x, double y)
    {
        var view = At(startScale);
        var before = view.Unproject(x, y);
        before.Should().NotBeNull();
        var zoomed = view.ZoomAt(factor, x, y);
        zoomed.ScalePxPerRad.Should().BeApproximately(startScale * factor, 1e-6);
        var after = zoomed.Project(before!.Value)!.Value;
        Math.Sqrt(Math.Pow(after.X - x, 2) + Math.Pow(after.Y - y, 2)).Should().BeLessThan(0.5);
    }

    [Test]
    public void Pan_moves_the_centre_point_by_the_drag()
    {
        foreach (var f in new[] { 0.0, 0.5, 1.0 })
        {
            var view = At(f == 0 ? 400 : ScaleForFlatness(f));
            var panned = view.Pan(37, -21);
            var s = panned.Project(view.Centre)!.Value;
            // Fixed north makes the screen axes rotate slightly as the centre moves (meridian convergence), so a 42 px
            // drag is matched to within 2 px; a 0.5 px drag is exact.
            s.X.Should().BeApproximately(500 + 37, 2);
            s.Y.Should().BeApproximately(400 - 21, 2);
            var tiny = view.Pan(0.3, -0.4).Project(view.Centre)!.Value;
            tiny.X.Should().BeApproximately(500.3, 0.01);
            tiny.Y.Should().BeApproximately(399.6, 0.01);
        }

        var v = At(400);
        v.Pan(0, 0).Centre.Should().Be(v.Centre);
        (v.Pan(10, 5).Centre.Length).Should().BeApproximately(1, 1e-12);
    }

    [Test]
    public void Pan_across_a_pole_keeps_a_valid_view()
    {
        var view = new MapViewState(new Vector3d(0, 0.999, 0.04), 400, 1000, 800, RegionAngle, TileAngle);
        var panned = view.Pan(0, 30);
        panned.Project(panned.Centre)!.Value.X.Should().BeApproximately(500, 1e-9);
        double.IsNaN(panned.Project(new Vector3d(1, 0, 0))?.X ?? 0).Should().BeFalse();
    }

    [Test]
    public void CentreOn_puts_the_direction_at_the_screen_centre()
    {
        var view = At(2000).CentreOn(new Vector3d(-1, 0.2, 0.3));
        var s = view.Project(new Vector3d(-1, 0.2, 0.3).Normalize())!.Value;
        s.X.Should().BeApproximately(500, 1e-9);
        s.Y.Should().BeApproximately(400, 1e-9);
    }

    [Test]
    public void FromYawPitch_matches_the_globe_view_centre_and_radius()
    {
        var g = new GlobeViewState(1000, 800, 35, -20, 1.5);
        var m = MapViewState.FromYawPitch(1000, 800, 35, -20, 1.5, RegionAngle, TileAngle);
        var d = g.ViewDirection;
        (m.Centre - new Vector3d(d.X, d.Y, d.Z)).Length.Should().BeLessThan(1e-6);
        m.ScalePxPerRad.Should().BeApproximately(g.Radius, 1e-3);

        // At flatness 0 the projection equals the orthographic one.
        var rng = new Random(3);
        for (var i = 0; i < 50; i++)
        {
            var p = RandomUnit(rng);
            var ok = g.TryProject(p, out var gx, out var gy);
            var s = m.Project(p);
            if (!ok || s is null) continue;
            s.Value.X.Should().BeApproximately(gx, 0.05);
            s.Value.Y.Should().BeApproximately(gy, 0.05);
        }
    }

    [Test]
    [Arguments(0.0)]
    [Arguments(0.5)]
    [Arguments(1.0)]
    public void ProjectFast_agrees_with_Project(double f)
    {
        var view = At(f == 0 ? 400 : ScaleForFlatness(f));
        var rng = new Random(11);
        var compared = 0;
        for (var i = 0; i < 3000; i++)
        {
            var d = RandomUnit(rng);
            var pf = new Vector3((float)d.X, (float)d.Y, (float)d.Z);
            var p = new Vector3d(pf.X, pf.Y, pf.Z);
            var slow = view.Project(p);
            var fast = view.ProjectFast(pf, out var x, out var y);
            if (slow is null && !fast) continue;
            if (slow is null || !fast)
            {
                // Within float error of the limit the two may differ; away from it they must agree.
                var theta = Math.Acos(Math.Clamp(Vector3d.Dot(p, view.Centre), -1, 1));
                Math.Abs(theta - view.MaxAngle).Should().BeLessThan(1e-4);
                continue;
            }

            compared++;
            Math.Abs(x - slow.Value.X).Should().BeLessThan(0.01);
            Math.Abs(y - slow.Value.Y).Should().BeLessThan(0.01);
        }

        compared.Should().BeGreaterThan(500);
    }

    [Test]
    public void ProjectFast_reports_hidden_points_on_the_sphere()
    {
        var view = At(400);
        var back = view.Centre * -1;
        view.ProjectFast(new Vector3((float)back.X, (float)back.Y, (float)back.Z), out _, out _).Should().BeFalse();
    }
}
