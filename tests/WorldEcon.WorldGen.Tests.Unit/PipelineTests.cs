using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tests.Unit;

public class PipelineTests
{
    [Test]
    public void StageSeeds_differ_by_stage_and_are_stable()
    {
        StageSeed.For(42, WorldGenStage.Plates).Should().NotBe(StageSeed.For(42, WorldGenStage.Elevation));
        StageSeed.For(42, WorldGenStage.Plates).Should().Be(StageSeed.For(42, WorldGenStage.Plates));
    }

    [Test]
    public void LayerHash_is_order_sensitive()
    {
        LayerHash.Of(new[] { 1, 2 }).Should().NotBe(LayerHash.Of(new[] { 2, 1 }));
        LayerHash.Of(new[] { 1, 2 }).Should().Be(LayerHash.Of(new[] { 1, 2 }));
        LayerHash.Of(new short[] { 1, 2 }).Should().NotBe(LayerHash.Of(new short[] { 2, 1 }));
        LayerHash.Of(new byte[] { 1, 2 }).Should().NotBe(LayerHash.Of(new byte[] { 2, 1 }));
    }

    [Test]
    public void Generate_returns_grid_and_runs_registered_stages()
    {
        var world = WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 4 } });
        world.Layers.Grid.CellCount.Should().Be(162);
        world.Layers.Geometry.Should().NotBeNull();
        world.GeneratorVersion.Should().Be(WorldGenerator.GeneratorVersion);
        world.Stages.Select(r => r.Stage).Should().Equal(WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate,
            WorldGenStage.TileGrid, WorldGenStage.TileElevation, WorldGenStage.TileSeaLevel, WorldGenStage.TileClimate, WorldGenStage.TileHydrology, WorldGenStage.TileLandform,
            WorldGenStage.Summary, WorldGenStage.Areas);
        world.Tiles.Should().NotBeNull();
        world.Regions.Should().NotBeNull();
        world.Lakes.Should().BeSameAs(world.Tiles!.Lakes);
        world.Layers.SeaLevelM.Should().Be(world.Tiles.SeaLevelM);

        var globe = WorldGenerator.GenerateGlobe(42, new WorldGenSettings { Grid = new() { Frequency = 4 } });
        globe.Stages.Select(r => r.Stage).Should().Equal(WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate);
        globe.GeneratorVersion.Should().Be(0);
        globe.Tiles.Should().BeNull();
    }

    [Test]
    public void Progress_reports_every_stage_in_order_with_non_decreasing_fractions_ending_at_one()
    {
        var reports = new List<(WorldGenStage Stage, double Fraction)>();
        var world = WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 4 } }, new WorldGenOptions(Progress: new SyncProgress(reports.Add)));
        // Stages report in order; a long stage (Tectonics) also reports progress within itself, so collapse repeats.
        var order = reports.Select(r => r.Stage).Where((s, i) => i == 0 || reports[i - 1].Stage != s).ToList();
        order.Take(world.Stages.Count).Should().Equal(world.Stages.Select(s => s.Stage));
        reports.Select(r => r.Fraction).Should().BeInAscendingOrder();
        reports[0].Fraction.Should().Be(0);
        reports[^1].Should().Be((WorldGenStage.Areas, 1.0));
    }

    private sealed class SyncProgress(Action<(WorldGenStage, double)> report) : IProgress<(WorldGenStage Stage, double Fraction)>
    {
        public void Report((WorldGenStage Stage, double Fraction) value) => report(value);
    }

    [Test]
    public void Default_settings_are_valid()
    {
        new WorldGenSettings().Validate().IsError.Should().BeFalse();
        new WorldGenSettings { Grid = new() { Frequency = 1 }, Plates = new() { Count = 12 } }.Validate().IsError.Should().BeFalse();
    }

    public static IEnumerable<Func<(WorldGenSettings Settings, string Property)>> InvalidSettings()
    {
        yield return () => (new WorldGenSettings { Grid = new() { Frequency = 0 } }, "Grid.Frequency");
        yield return () => (new WorldGenSettings { Grid = new() { Frequency = 257 } }, "Grid.Frequency");
        yield return () => (new WorldGenSettings { Grid = new() { PlanetRadiusKm = 0 } }, "Grid.PlanetRadiusKm");
        yield return () => (new WorldGenSettings { Plates = new() { Count = 0 } }, "Plates.Count");
        yield return () => (new WorldGenSettings { Grid = new() { Frequency = 1 }, Plates = new() { Count = 13 } }, "Plates.Count");
        yield return () => (new WorldGenSettings { Plates = new() { ContinentalFractionBp = 10_001 } }, "Plates.ContinentalFractionBp");
        yield return () => (new WorldGenSettings { SeaLevel = new() { LandFractionBp = -1 } }, "SeaLevel.LandFractionBp");
        yield return () => (new WorldGenSettings { Tectonics = new() { StepMy = 0 } }, "Tectonics.StepMy");
        yield return () => (new WorldGenSettings { Tectonics = new() { TopographyNoiseM = -1 } }, "Tectonics.TopographyNoiseM");
        yield return () => (new WorldGenSettings { Climate = new() { MoistureIterations = 0 } }, "Climate.MoistureIterations");
        yield return () => (new WorldGenSettings { Climate = new() { MoistureBaseLossBp = 10_001 } }, "Climate.MoistureBaseLossBp");
        yield return () => (new WorldGenSettings { Climate = new() { MoistureOrographicLossBpPerKm = -1 } }, "Climate.MoistureOrographicLossBpPerKm");
        yield return () => (new WorldGenSettings { Climate = new() { MoistureFullScaleBp = 0 } }, "Climate.MoistureFullScaleBp");
        yield return () => (new WorldGenSettings { Climate = new() { MoistureSpreadBp = -1 } }, "Climate.MoistureSpreadBp");
        yield return () => (new WorldGenSettings { Climate = new() { PrecipitationSoftKneeMm = 20_001 } }, "Climate.PrecipitationSoftKneeMm");
        yield return () => (new WorldGenSettings { Hydrology = new() { EvaporationMmPerDegree = -1 } }, "Hydrology.EvaporationMmPerDegree");
        yield return () => (new WorldGenSettings { Hydrology = new() { RiverTopTierPercentileBp = -1 } }, "Hydrology.RiverTopTierPercentileBp");
        yield return () => (new WorldGenSettings { Hydrology = new() { RiverTopTierPercentileBp = 10_001 } }, "Hydrology.RiverTopTierPercentileBp");
        yield return () => (new WorldGenSettings { Areas = new() { MemberThresholdBp = 0 } }, "Areas.MemberThresholdBp");
        yield return () => (new WorldGenSettings { Areas = new() { MinAreaCells = 0 } }, "Areas.MinAreaCells");
        yield return () => (new WorldGenSettings { Areas = new() { SeaMaxCells = -1 } }, "Areas.SeaMaxCells");
        yield return () => (new WorldGenSettings { Pinning = new() { MinGroupSeparationRings = 0 } }, "Pinning.MinGroupSeparationRings");
    }

    [Test]
    [MethodDataSource(nameof(InvalidSettings))]
    public void Validate_rejects_out_of_range_values_naming_the_property(WorldGenSettings settings, string property)
    {
        var result = settings.Validate();
        result.IsError.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be($"settings.{property}");
        result.Errors[0].Description.Should().Contain(property);
    }

    [Test]
    public void Validate_reports_every_invalid_property()
    {
        var result = new WorldGenSettings { Grid = new() { PlanetRadiusKm = 0 }, Areas = new() { MinAreaCells = 0, MemberThresholdBp = 0 } }.Validate();
        result.Errors.Select(e => e.Code).Should().Equal("settings.Grid.PlanetRadiusKm", "settings.Areas.MemberThresholdBp", "settings.Areas.MinAreaCells");
    }

    [Test]
    public void Generate_throws_on_invalid_settings_before_building_the_grid()
    {
        var act = () => WorldGenerator.Generate(1, new WorldGenSettings { Grid = new() { Frequency = 0 } });
        act.Should().Throw<InvalidOperationException>().WithMessage("*Grid.Frequency*");
        var plates = () => WorldGenerator.Generate(1, new WorldGenSettings { Grid = new() { Frequency = 1 }, Plates = new() { Count = 0 } });
        plates.Should().Throw<InvalidOperationException>().WithMessage("*Plates.Count*");
    }
}
