using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class SurfaceTests
{
    private static StepState Still(CrustKind west, CrustKind east, TectonicsSettings? t = null) =>
        TwoPlates.Make(west, east, t: t, speedMmPerYr: 0);

    [Test]
    public void Erosion_lowers_mountains_by_the_e_fold_factor()
    {
        var s = Still(CrustKind.Continental, CrustKind.Continental, new TectonicsSettings { ErosionEFoldMy = 100, StepMy = 2 });
        var c = 0;
        var slot = s.SlotOfCell[c];
        s.Pool.ThicknessDam[slot] = CrustRecord.ContinentalThicknessDam + 1_000;
        Surface.Erode(s);
        var expected = CrustRecord.ContinentalThicknessDam + 1_000 * Math.Exp(-2.0 / 100);
        ((double)s.Pool.ThicknessDam[slot]).Should().BeApproximately(expected, 1.0);
    }

    [Test]
    public void Eroded_volume_lands_on_lower_neighbours_as_sediment()
    {
        var s = Still(CrustKind.Continental, CrustKind.Continental, new TectonicsSettings { ErosionEFoldMy = 10 });
        var c = 0;
        s.Pool.ThicknessDam[s.SlotOfCell[c]] = CrustRecord.ContinentalThicknessDam + 2_000;
        Surface.Erode(s);
        var neighbours = s.Grid.Neighbours(c).ToArray();
        neighbours.Sum(nb => (int)s.Pool.SedimentDam[s.SlotOfCell[nb]]).Should().BeGreaterThan(0);
        neighbours.Should().OnlyContain(nb => s.Pool.MaxBurialDam[s.SlotOfCell[nb]] >= s.Pool.SedimentDam[s.SlotOfCell[nb]]);
        s.Pool.SedimentDam[s.SlotOfCell[c]].Should().Be(0, "the eroding cell itself gets none");
    }

    [Test]
    public void Exposure_counts_only_in_the_belts()
    {
        var s = Still(CrustKind.Continental, CrustKind.Continental, new TectonicsSettings { StepMy = 2 });
        Surface.Expose(s);
        for (var c = 0; c < s.Grid.CellCount; c++)
        {
            var lat = Math.Abs(Math.Asin(Math.Clamp(s.Geo.Centre(c).Z, -1, 1)) * 180 / Math.PI);
            var slot = s.SlotOfCell[c];
            var wet = lat < Surface.WetTropicsMaxLat || (lat >= Surface.WetTemperateMinLat && lat <= Surface.WetTemperateMaxLat);
            s.Pool.CoalExposureMy[slot].Should().Be((ushort)(wet ? 2 : 0), $"cell at {lat:F1}°");
        }
    }

    [Test]
    public void Evaporite_exposure_needs_the_arid_belt_and_a_basin()
    {
        var s = Still(CrustKind.Continental, CrustKind.Continental);
        foreach (var slot in s.SlotOfCell) s.Pool.RiftAgeMy[slot] = 0;
        Surface.Expose(s);
        for (var c = 0; c < s.Grid.CellCount; c++)
        {
            var lat = Math.Abs(Math.Asin(Math.Clamp(s.Geo.Centre(c).Z, -1, 1)) * 180 / Math.PI);
            var arid = lat >= Surface.AridMinLat && lat <= Surface.AridMaxLat;
            (s.Pool.EvaporiteExposureMy[s.SlotOfCell[c]] > 0).Should().Be(arid);
        }
    }

    [Test]
    public void Ages_advance_and_never_reach_never()
    {
        var s = Still(CrustKind.Continental, CrustKind.Continental, new TectonicsSettings { StepMy = 2 });
        var slot = s.SlotOfCell[0];
        s.Pool.LastOrogenyAgeMy[slot] = 0;
        s.Pool.FormedAgeMy[slot] = CrustRecord.Never - 1;
        Surface.AgeRecord(s);
        s.Pool.LastOrogenyAgeMy[slot].Should().Be(2);
        s.Pool.FormedAgeMy[slot].Should().Be(CrustRecord.Never - 1);
        s.Pool.RiftAgeMy[slot].Should().Be(CrustRecord.Never);
    }
}
