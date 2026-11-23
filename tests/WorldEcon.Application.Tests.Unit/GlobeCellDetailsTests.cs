using FluentAssertions;
using WorldEcon.Application.Globe;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Application.Tests.Unit;

public class GlobeCellDetailsTests
{
    private static readonly WorldLayers World8 =
        WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 8 } }).Layers;

    [Test]
    public void Build_copies_the_cell_layers_and_lists_its_areas_and_settlements()
    {
        var l = World8;
        var area = l.Areas.First(a => a.Cells.Count > 1);
        var (cell, coverage) = area.Cells[0];
        var settlements = new List<GlobeSettlement> { new(Guid.NewGuid(), "Here", cell), new(Guid.NewGuid(), "Elsewhere", (cell + 1) % l.Grid.CellCount) };

        var d = GlobeCellDetails.Build(l, cell, settlements);

        d.Cell.Should().Be(cell);
        (d.LatDeg, d.LonDeg).Should().Be(l.Geometry.LatLon(cell));
        d.ElevationM.Should().Be(l.ElevationM[cell]);
        d.Biome.Should().Be((Biome)l.BiomeOf[cell]);
        d.TemperatureDeciC.Should().Be(l.TemperatureDeciC[cell]);
        d.PrecipitationMm.Should().Be(l.PrecipitationMm[cell]);
        d.Composition.Should().Equal(l.Composition[cell]);
        d.PlateId.Should().Be(l.PlateOf[cell]);
        d.Neighbours.Should().Equal(l.Grid.Neighbours(cell).ToArray());
        d.Areas.Should().ContainEquivalentOf(new CellAreaEntry(area.Index, area.Kind, area.Name, coverage));
        d.Areas.Select(a => (a.Kind, a.Name)).Should().BeInAscendingOrder(Comparer<(WorldEcon.SharedKernel.Globe.GeographicRegionKind, string)>.Create(
            (x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : string.CompareOrdinal(x.Item2, y.Item2)));
        d.Settlements.Should().Equal("Here");
    }

    [Test]
    public void Build_rejects_a_cell_outside_the_grid()
    {
        var act = () => GlobeCellDetails.Build(World8, World8.Grid.CellCount, []);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
