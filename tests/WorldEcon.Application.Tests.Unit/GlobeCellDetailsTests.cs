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

public class GlobeCellGeologyTests
{
    [Test]
    public void Cell_details_include_crust_kind_age_orogeny_and_boundary_values()
    {
        var l = WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 8 } }).Layers;
        var cell = 7;
        var d = GlobeCellDetails.Build(l, cell, []);
        d.Geology.Should().NotBeNull();
        d.Geology!.Kind.Should().Be(l.Crust!.Kind[cell]);
        d.Geology.ThicknessKm.Should().BeApproximately(l.Crust.ThicknessDam[cell] / 100.0, 1e-9);
        d.Geology.FormedAgeMy.Should().Be(l.Crust.FormedAgeMy[cell]);
        d.Geology.Orogeny.Should().Be(l.Crust.Orogeny[cell]);
        d.Geology.CollisionStrengthPermille.Should().Be(l.CollisionStrengthPermille[cell]);
        d.Geology.BoundaryDistanceCells.Should().Be(l.BoundaryDistanceCells[cell]);
    }

    [Test]
    public void A_world_without_a_record_has_no_geology()
    {
        var l = WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 8 } }).Layers;
        l.Crust = null;
        GlobeCellDetails.Build(l, 0, []).Geology.Should().BeNull();
    }
}

public class CellGeologyDescribeTests
{
    [Test]
    public void Describe_lists_crust_orogeny_rift_sediment_exposure_and_boundary()
    {
        var g = new CellGeology(WorldEcon.WorldGen.Tectonics.CrustKind.Continental, 52.5, 1200, WorldEcon.WorldGen.Tectonics.OrogenyKind.Andean, 30, 2, 28,
            WorldEcon.WorldGen.Tectonics.CrustFlags.Suture, null, null, 1.25, 2.5, 40, 0, 750, 0);
        var rows = g.Describe();
        rows.Select(r => r.Label).Should().Equal("Crust", "Mountain building", "Rift", "Sediment", "Coal / salt exposure", "Plate boundary");
        rows[0].Value.Should().Be("Continental, 52.5 km thick, formed 1,200 My ago");
        rows[1].Value.Should().Be("Andean, last 30 My ago (2 episodes), arc magma 28 My ago; suture");
        rows[2].Value.Should().Be("none");
        rows[3].Value.Should().Be("1.25 km (deepest burial 2.50 km)");
        rows[4].Value.Should().Be("coal 40 My, evaporite 0 My");
        rows[5].Value.Should().Be("collision strength 750 ‰, on the boundary");
    }
}
