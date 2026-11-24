using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class CrustRecordTests
{
    private static (GlobeGrid Grid, GlobeGeometry Geo) Globe(int frequency)
    {
        var g = GlobeGrid.Build(frequency);
        return (g, GlobeGeometry.Compute(g));
    }

    private static InitialCrustResult Build(int arrangement, int frequency = 32, int cratons = 10, int fractionBp = 4000, ulong seed = 42)
    {
        var (grid, geo) = Globe(frequency);
        var t = new TectonicsSettings { StartArrangement = arrangement, CratonCount = cratons };
        var p = new PlateSettings { ContinentalFractionBp = fractionBp };
        return InitialCrust.Build(grid, geo, t, p, seed);
    }

    private static int Components(GlobeGrid grid, Func<int, bool> member)
    {
        var seen = new bool[grid.CellCount];
        var count = 0;
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (seen[c] || !member(c)) continue;
            count++;
            var stack = new Stack<int>([c]);
            seen[c] = true;
            while (stack.TryPop(out var x))
                foreach (var nb in grid.Neighbours(x))
                    if (!seen[nb] && member(nb)) { seen[nb] = true; stack.Push(nb); }
        }

        return count;
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public void Cratons_cover_the_continental_share(int arrangement)
    {
        var r = Build(arrangement);
        var continental = r.Record.Kind.Take(r.Record.Count).Count(k => k == CrustKind.Continental);
        (continental / (double)r.Record.Count).Should().BeApproximately(0.40, 0.01);
    }

    [Test]
    public void Scattered_cratons_keep_minimum_separation()
    {
        var r = Build(1);
        var (grid, geo) = Globe(32);
        r.CratonSeeds.Should().HaveCount(10);
        for (var i = 0; i < r.CratonSeeds.Count; i++)
        for (var j = i + 1; j < r.CratonSeeds.Count; j++)
        {
            var angle = Math.Acos(Math.Clamp(Vector3d.Dot(geo.Centre(r.CratonSeeds[i]), geo.Centre(r.CratonSeeds[j])), -1, 1));
            angle.Should().BeGreaterThanOrEqualTo(r.MinSeparationRad - 1e-9);
        }

        r.MinSeparationRad.Should().BeGreaterThan(0);
    }

    [Test]
    public void Supercontinent_cratons_form_one_connected_landmass()
    {
        var r = Build(0);
        var (grid, _) = Globe(32);
        Components(grid, c => r.Record.Kind[c] == CrustKind.Continental).Should().Be(1);
    }

    [Test]
    public void Scattered_cratons_form_more_than_one_landmass()
    {
        var r = Build(1);
        var (grid, _) = Globe(32);
        Components(grid, c => r.Record.Kind[c] == CrustKind.Continental).Should().BeGreaterThan(1);
    }

    [Test]
    public void Craton_ages_are_older_than_the_history_and_ocean_younger()
    {
        var r = Build(0);
        var history = new TectonicsSettings().SimulatedMy;
        for (var c = 0; c < r.Record.Count; c++)
        {
            if (r.Record.Kind[c] == CrustKind.Continental) r.Record.FormedAgeMy[c].Should().BeGreaterThan((ushort)history);
            else r.Record.FormedAgeMy[c].Should().BeLessThanOrEqualTo(200);
            r.Record.LastOrogenyAgeMy[c].Should().Be(CrustRecord.Never);
        }
    }

    [Test]
    public void Continental_crust_is_thicker_than_oceanic()
    {
        var r = Build(0);
        r.Record.ThicknessDam[r.CratonSeeds[0]].Should().BeGreaterThan(CrustRecord.OceanicThicknessDam);
    }

    [Test]
    public void Same_seed_same_crust()
    {
        Build(1).Record.Hash().Should().Be(Build(1).Record.Hash());
        Build(1, seed: 7).Record.Hash().Should().NotBe(Build(1).Record.Hash());
    }

    [Test]
    public void Gap_fill_is_age_zero_ocean_and_marks_passive_margins()
    {
        var r = new CrustRecord(4);
        var a = r.Allocate();
        var b = r.Allocate();
        r.GapFill(a, besideRiftedContinent: false);
        r.GapFill(b, besideRiftedContinent: true);
        r.Kind[a].Should().Be(CrustKind.Oceanic);
        r.FormedAgeMy[a].Should().Be(0);
        r.ThicknessDam[a].Should().Be(CrustRecord.OceanicThicknessDam);
        r.PassiveMarginAgeMy[a].Should().Be(CrustRecord.Never);
        r.PassiveMarginAgeMy[b].Should().Be(0);
    }

    [Test]
    public void Allocate_grows_and_free_slots_are_reused()
    {
        var r = new CrustRecord(1);
        var a = r.Allocate();
        var b = r.Allocate();
        r.Capacity.Should().BeGreaterThanOrEqualTo(2);
        r.Free(a);
        r.Allocate().Should().Be(a);
        b.Should().NotBe(a);
    }

    [Test]
    public void Gather_builds_a_per_cell_record_and_is_independent()
    {
        var r = new CrustRecord(3);
        var s0 = r.Allocate();
        var s1 = r.Allocate();
        r.GapFill(s0, false);
        r.GapFill(s1, false);
        r.ThicknessDam[s1] = 3500;
        var cells = r.Gather([s1, s0, s1]);
        cells.Count.Should().Be(3);
        cells.ThicknessDam.Take(3).Should().Equal((ushort)3500, CrustRecord.OceanicThicknessDam, (ushort)3500);
        cells.ThicknessDam[0] = 1;
        r.ThicknessDam[s1].Should().Be(3500);
    }

    [Test]
    public void CloneGlobe_copies_the_record()
    {
        var grid = GlobeGrid.Build(4);
        var l = new WorldLayers(grid, GlobeGeometry.Compute(grid)) { Crust = Build(0, frequency: 4, cratons: 2).Record };
        var copy = l.CloneGlobe();
        copy.Crust.Should().NotBeSameAs(l.Crust);
        copy.Crust!.Hash().Should().Be(l.Crust!.Hash());
    }
}
