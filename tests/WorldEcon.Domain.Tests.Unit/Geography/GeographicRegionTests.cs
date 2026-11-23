using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Globe;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class GeographicRegionTests
{
    [Test]
    public void Create_RejectsBlankName()
        => GeographicRegion.Create(WorldId.New(), "  ", GeographicRegionKind.Sea).IsError.Should().BeTrue();

    [Test]
    public void Create_TrimsNameAndDefaultsToGenerated()
    {
        var r = GeographicRegion.Create(WorldId.New(), " Sea of X ", GeographicRegionKind.Sea).Value;
        r.Name.Should().Be("Sea of X");
        r.Kind.Should().Be(GeographicRegionKind.Sea);
        r.NameAuthored.Should().BeFalse();
    }

    [Test]
    public void Rename_UpdatesNameAndAuthoredFlag_RejectsBlank()
    {
        var r = GeographicRegion.Create(WorldId.New(), "A", GeographicRegionKind.Plain).Value;
        r.Rename("B", authored: true).IsError.Should().BeFalse();
        r.Name.Should().Be("B");
        r.NameAuthored.Should().BeTrue();
        r.Rename(" ", authored: false).IsError.Should().BeTrue();
        r.Name.Should().Be("B");
    }

    [Test]
    [Arguments((short)0)]
    [Arguments((short)10_001)]
    [Arguments((short)-1)]
    public void Cell_Create_RejectsOutOfRangeCoverage(short bp)
        => GeographicRegionCell.Create(GeographicRegionId.New(), new GlobeCellId(3), bp).IsError.Should().BeTrue();

    [Test]
    [Arguments((short)1)]
    [Arguments((short)10_000)]
    public void Cell_Create_AcceptsBoundaryCoverage(short bp)
    {
        var id = GeographicRegionId.New();
        var c = GeographicRegionCell.Create(id, new GlobeCellId(3), bp).Value;
        c.GeographicRegionId.Should().Be(id);
        c.Cell.Should().Be(new GlobeCellId(3));
        c.CoverageBp.Should().Be(bp);
    }

    [Test]
    public void Containment_RejectsSelfLoop()
    {
        var id = GeographicRegionId.New();
        GeographicRegionContainment.Create(WorldId.New(), id, id).IsError.Should().BeTrue();
    }

    [Test]
    public void Containment_AcceptsDistinct()
    {
        var p = GeographicRegionId.New();
        var c = GeographicRegionId.New();
        var r = GeographicRegionContainment.Create(WorldId.New(), p, c).Value;
        r.ParentRegionId.Should().Be(p);
        r.ChildRegionId.Should().Be(c);
    }

    [Test]
    public void World_Defaults_GlobeSettings()
    {
        var w = World.Create("W", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        w.GridFrequency.Should().Be(64);
        w.PlanetRadiusKm.Should().Be(6371);
        w.WorldGenVersion.Should().Be(0);
    }

    [Test]
    public void World_SetGlobe_Applies()
    {
        var w = World.Create("W", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        w.SetGlobe(32, 5000, 2).IsError.Should().BeFalse();
        w.GridFrequency.Should().Be(32);
        w.PlanetRadiusKm.Should().Be(5000);
        w.WorldGenVersion.Should().Be(2);
    }

    [Test]
    [Arguments(0, 6371, 1)]
    [Arguments(64, 0, 1)]
    [Arguments(64, 6371, -1)]
    public void World_SetGlobe_RejectsInvalid_AndLeavesStateUnchanged(int freq, int radius, int ver)
    {
        var w = World.Create("W", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        w.SetGlobe(freq, radius, ver).IsError.Should().BeTrue();
        w.GridFrequency.Should().Be(64);
        w.PlanetRadiusKm.Should().Be(6371);
        w.WorldGenVersion.Should().Be(0);
    }

    [Test]
    public void World_SetSeaLevel_DefaultsToZero_AndApplies_IncludingNegative()
    {
        var w = World.Create("W", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        w.SeaLevelM.Should().Be(0);
        w.SetSeaLevel(-120);
        w.SeaLevelM.Should().Be(-120);
        w.SetSeaLevel(35);
        w.SeaLevelM.Should().Be(35);
    }

    [Test]
    public void Settlement_PinTo_SetsCellId()
    {
        var s = Settlement.Create(WorldId.New(), PoliticalRegionId.New(), "Town", SettlementType.Town, 0, 0, 10).Value;
        s.CellId.Should().BeNull();
        s.PinTo(new GlobeCellId(42)).IsError.Should().BeFalse();
        s.CellId.Should().Be(new GlobeCellId(42));
        s.PinTo(new GlobeCellId(0)).IsError.Should().BeFalse();
        s.CellId.Should().Be(new GlobeCellId(0));
    }

    [Test]
    public void Settlement_PinTo_RejectsNegativeCellId_AndLeavesCellUnchanged()
    {
        var s = Settlement.Create(WorldId.New(), PoliticalRegionId.New(), "Town", SettlementType.Town, 0, 0, 10).Value;
        s.PinTo(new GlobeCellId(7));
        var r = s.PinTo(new GlobeCellId(-1));
        r.IsError.Should().BeTrue();
        r.FirstError.Code.Should().Be("settlement.cell.negative");
        s.CellId.Should().Be(new GlobeCellId(7));
    }

    [Test]
    public void Settlement_PinTo_WithTile_SetsTileId_RejectsNegativeTile_AndClearsTileWithoutOne()
    {
        var s = Settlement.Create(WorldId.New(), PoliticalRegionId.New(), "Town", SettlementType.Town, 0, 0, 10).Value;
        s.TileId.Should().BeNull();
        s.PinTo(new GlobeCellId(7), 1234).IsError.Should().BeFalse();
        (s.CellId, s.TileId).Should().Be((new GlobeCellId(7), 1234));

        var r = s.PinTo(new GlobeCellId(8), -1);
        r.IsError.Should().BeTrue();
        r.FirstError.Code.Should().Be("settlement.tile.negative");
        (s.CellId, s.TileId).Should().Be((new GlobeCellId(7), 1234), "a rejected pin leaves cell and tile unchanged");

        s.PinTo(new GlobeCellId(9)).IsError.Should().BeFalse();
        (s.CellId, s.TileId).Should().Be((new GlobeCellId(9), (int?)null));
    }

    [Test]
    public void World_SetGlobe_RejectsASecondCall_OnceAGlobeIsGenerated()
    {
        var w = World.Create("W", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        w.SetGlobe(32, 5000, 2).IsError.Should().BeFalse();
        var r = w.SetGlobe(16, 4000, 2);
        r.IsError.Should().BeTrue();
        r.FirstError.Code.Should().Be("world.globe.generated");
        w.GridFrequency.Should().Be(32);
        w.PlanetRadiusKm.Should().Be(5000);
    }

    [Test]
    public void World_SetGlobe_WithVersionZero_CanBeCalledAgain()
    {
        var w = World.Create("W", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        w.SetGlobe(32, 5000, 0).IsError.Should().BeFalse();
        w.SetGlobe(16, 4000, 2).IsError.Should().BeFalse();
        w.GridFrequency.Should().Be(16);
    }
}
