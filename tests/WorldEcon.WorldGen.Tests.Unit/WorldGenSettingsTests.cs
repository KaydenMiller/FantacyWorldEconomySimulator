using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class WorldGenSettingsTests
{
    private static IEnumerable<(PropertyInfo Section, PropertyInfo Leaf)> AllLeaves() =>
        WorldGenSettingsJson.Sections.SelectMany(s => WorldGenSettingsJson.Leaves(s.PropertyType).Select(l => (s, l)));

    // Fresh settings with one leaf replaced (sections are records with parameterless constructors and init setters).
    private static WorldGenSettings With(PropertyInfo section, PropertyInfo leaf, int value)
    {
        var settings = new WorldGenSettings();
        var copy = Activator.CreateInstance(section.PropertyType)!;
        foreach (var l in WorldGenSettingsJson.Leaves(section.PropertyType))
            l.SetValue(copy, l.GetValue(section.GetValue(settings)));
        leaf.SetValue(copy, value);
        section.SetValue(settings, copy);
        return settings;
    }

    public static IEnumerable<Func<(string Section, string Leaf, int Value)>> FarOutOfRange()
    {
        foreach (var (section, leaf) in AllLeaves())
        {
            yield return () => (section.Name, leaf.Name, int.MinValue);
            yield return () => (section.Name, leaf.Name, int.MaxValue);
        }
    }

    [Test]
    public void Every_section_and_leaf_is_an_int_property_with_a_section_record()
    {
        WorldGenSettingsJson.Sections.Select(s => s.Name).Should().Equal(
            "Grid", "Plates", "Tectonics", "Elevation", "SeaLevel", "Currents", "Winds", "Climate", "Biomes", "Hydrology", "Areas", "Pinning", "Tiles", "Roads");
        foreach (var section in WorldGenSettingsJson.Sections)
        {
            var all = section.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            all.Should().OnlyContain(p => p.PropertyType == typeof(int), $"{section.Name} leaves are ints so the JSON binder and validator cover them");
        }

        AllLeaves().Count().Should().Be(159);
    }

    [Test]
    public void Defaults_are_valid()
    {
        new WorldGenSettings().Validate().IsError.Should().BeFalse();
    }

    [Test]
    [MethodDataSource(nameof(FarOutOfRange))]
    public void Validate_rejects_a_far_out_of_range_value_for_every_leaf(string section, string leaf, int value)
    {
        var sectionProp = WorldGenSettingsJson.Sections.Single(s => s.Name == section);
        var leafProp = WorldGenSettingsJson.Leaves(sectionProp.PropertyType).Single(l => l.Name == leaf);

        var result = With(sectionProp, leafProp, value).Validate();

        result.IsError.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == $"settings.{section}.{leaf}");
        result.Errors.First(e => e.Code == $"settings.{section}.{leaf}").Description.Should().Contain($"{section}.{leaf}");
    }

    public static IEnumerable<Func<(WorldGenSettings Settings, string Code)>> CrossFieldViolations()
    {
        yield return () => (new WorldGenSettings { Plates = new() { MinSpeedMmPerYr = 101 } }, "settings.Plates.MaxSpeedMmPerYr");
        yield return () => (new WorldGenSettings { Elevation = new() { MinElevationM = 9_001 } }, "settings.Elevation.MaxElevationM");
        yield return () => (new WorldGenSettings { Currents = new() { TropicLatDeg = 61 } }, "settings.Currents.PolarLatDeg");
        yield return () => (new WorldGenSettings { Climate = new() { PrecipitationSoftKneeMm = 4001 } }, "settings.Climate.PrecipitationCeilingMm");
        yield return () => (new WorldGenSettings { Biomes = new() { HillsHeightM = 2_500 } }, "settings.Biomes.MountainHeightM");
        yield return () => (new WorldGenSettings { Biomes = new() { IceBelowDeciC = 0 } }, "settings.Biomes.TundraBelowDeciC");
        yield return () => (new WorldGenSettings { Biomes = new() { TundraBelowDeciC = 50 } }, "settings.Biomes.TaigaBelowDeciC");
        yield return () => (new WorldGenSettings { Biomes = new() { TaigaBelowDeciC = 210 } }, "settings.Biomes.TemperateMaxDeciC");
        yield return () => (new WorldGenSettings { Biomes = new() { ShrublandBelowMm = 1_000 } }, "settings.Biomes.GrasslandBelowMm");
        yield return () => (new WorldGenSettings { Biomes = new() { DesertBelowMm = 1_200, ShrublandBelowMm = 1_300, GrasslandBelowMm = 1_400 } }, "settings.Biomes.SavannaBelowMm");
        yield return () => (new WorldGenSettings { Pinning = new() { IdealMinDeciC = 251 } }, "settings.Pinning.IdealMaxDeciC");
        yield return () => (new WorldGenSettings { Pinning = new() { IdealMinMm = 1_501 } }, "settings.Pinning.IdealMaxMm");
        yield return () => (new WorldGenSettings { Grid = new() { Frequency = 256, RegionMultiplier = 16, TileMultiplier = 32 } }, "settings.Grid.TileMultiplier");
        yield return () => (new WorldGenSettings { Grid = new() { Frequency = 100, RegionMultiplier = 16, TileMultiplier = 10 } }, "settings.Grid.TileMultiplier");
        yield return () => (new WorldGenSettings { Tiles = new() { ReliefScaleMinBp = 501, ReliefScaleMaxBp = 500 } }, "settings.Tiles.ReliefScaleMaxBp");
        yield return () => (new WorldGenSettings { Tiles = new() { OrographicMinBp = 3_001, OrographicMaxBp = 3_000 } }, "settings.Tiles.OrographicMaxBp");
        yield return () => (new WorldGenSettings { Tiles = new() { RiverMinFlow = 50_000_001 } }, "settings.Tiles.MajorRiverFlow");
        yield return () => (new WorldGenSettings { Tiles = new() { HillsReliefM = 900 } }, "settings.Tiles.MountainReliefM");
    }

    [Test]
    [MethodDataSource(nameof(CrossFieldViolations))]
    public void Validate_enforces_cross_field_rules(WorldGenSettings settings, string code)
    {
        var result = settings.Validate();
        result.IsError.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(code);
    }

    [Test]
    public void A_cross_field_rule_is_skipped_when_one_side_is_out_of_range()
    {
        var result = new WorldGenSettings { Biomes = new() { IceBelowDeciC = 5_000 } }.Validate();
        result.Errors.Select(e => e.Code).Should().Equal("settings.Biomes.IceBelowDeciC");
    }

    [Test]
    public void Plate_count_upper_bound_follows_the_grid_cell_count()
    {
        new WorldGenSettings { Grid = new() { Frequency = 1 }, Plates = new() { Count = 12 } }.Validate().IsError.Should().BeFalse();
        new WorldGenSettings { Grid = new() { Frequency = 1 }, Plates = new() { Count = 13 } }.Validate()
            .Errors.Select(e => e.Code).Should().Equal("settings.Plates.Count");
    }

    [Test]
    public void Tile_frequency_limit_is_exact()
    {
        // 28 × 16 × 32 = 14,336 is within 14,654; 29 × 16 × 32 = 14,848 is not.
        new WorldGenSettings { Grid = new() { Frequency = 28, RegionMultiplier = 16, TileMultiplier = 32 } }.Validate().IsError.Should().BeFalse();
        new WorldGenSettings { Grid = new() { Frequency = 29, RegionMultiplier = 16, TileMultiplier = 32 } }.Validate()
            .Errors.Select(e => e.Code).Should().Equal("settings.Grid.TileMultiplier");
        (10L * GridSettings.MaxTileFrequency * GridSettings.MaxTileFrequency + 2).Should().BeLessThan(int.MaxValue);
        (10L * (GridSettings.MaxTileFrequency + 1) * (GridSettings.MaxTileFrequency + 1) + 2).Should().BeGreaterThan(int.MaxValue);
    }

    [Test]
    [Arguments(28, 16, 32)]  // 14,336
    [Arguments(29, 16, 32)]  // 14,848
    [Arguments(229, 2, 32)]  // 14,656
    [Arguments(229, 4, 16)]  // 14,656
    [Arguments(256, 1, 32)]  // 8,192
    [Arguments(64, 3, 8)]    // 1,536
    [Arguments(1, 1, 1)]
    public void Grid_validation_agrees_with_TileGrid_IsSupportedFrequency(int frequency, int region, int tile)
    {
        // Only Grid errors count: other sections have their own limits at small grids (e.g. plate count ≤ cell count).
        var result = new WorldGenSettings { Grid = new() { Frequency = frequency, RegionMultiplier = region, TileMultiplier = tile } }
            .Validate();
        bool valid = !result.IsError || !result.Errors.Any(e => e.Code.StartsWith("settings.Grid.", StringComparison.Ordinal));
        valid.Should().Be(TileGrid.IsSupportedFrequency(frequency, region, tile));
        GridSettings.MaxTileFrequency.Should().Be(TileGrid.MaxFrequency);
    }

    [Test]
    public void Default_grid_is_5_km_tiles()
    {
        var g = new GridSettings();
        (g.Frequency, g.RegionMultiplier, g.TileMultiplier).Should().Be((64, 3, 8));
    }

    [Test]
    public void Omitted_tiles_section_takes_defaults_and_tiles_keys_bind()
    {
        WorldGenSettingsJson.Deserialize("{}").Tiles.Should().Be(new TilesSettings());
        var s = WorldGenSettingsJson.Deserialize("""{ "Tiles": { "RiverMinFlow": 1000 }, "Grid": { "TileMultiplier": 4 } }""");
        s.Tiles.RiverMinFlow.Should().Be(1000);
        s.Tiles.LargeLakeMinKm2.Should().Be(500);
        s.Grid.TileMultiplier.Should().Be(4);
        WorldGenSettingsJson.Deserialize(WorldGenSettingsJson.Serialize(s)).Should().Be(s);
    }

    [Test]
    public void Local_map_section_is_no_longer_accepted()
    {
        var act = () => WorldGenSettingsJson.Deserialize("""{ "LocalMap": { "HexKm": 20 } }""");
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("unknown setting 'LocalMap'");
    }

    // ---- presets ----

    [Test]
    public void Presets_are_named_valid_and_differ_only_as_specified()
    {
        WorldGenPresets.Names.Should().Equal("earth-5km", "earth-10km", "small-5km");
        foreach (var name in WorldGenPresets.Names) WorldGenPresets.Get(name).Validate().IsError.Should().BeFalse(name);

        var earth = WorldGenPresets.Get("earth-5km");
        earth.Should().Be(new WorldGenSettings());
        (earth.Grid.PlanetRadiusKm, earth.Grid.Frequency, earth.Grid.RegionMultiplier, earth.Grid.TileMultiplier)
            .Should().Be((6_371, 64, 3, 8));
        WorldGenPresets.Get("earth-10km").Should().Be(new WorldGenSettings { Grid = new() { TileMultiplier = 4 } });
        var small = WorldGenPresets.Get("small-5km");
        small.Should().Be(new WorldGenSettings { Grid = new() { PlanetRadiusKm = 3_000, Frequency = 32, RegionMultiplier = 3, TileMultiplier = 8 } });
        small.Tiles.Should().Be(new TilesSettings());
    }

    [Test]
    public void Unknown_preset_names_the_valid_ones()
    {
        var act = () => WorldGenPresets.Get("moon");
        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("moon").And.Contain("earth-5km").And.Contain("earth-10km").And.Contain("small-5km");
    }

    [Test]
    public void Json_over_a_preset_replaces_only_the_named_properties_and_leaves_the_preset_unchanged()
    {
        var preset = WorldGenPresets.Get("small-5km");
        var merged = WorldGenSettingsJson.Deserialize("""{ "Grid": { "Frequency": 8 }, "Climate": { "MoistureIterations": 32 } }""", preset);

        merged.Should().Be(preset with
        {
            Grid = preset.Grid with { Frequency = 8 },
            Climate = preset.Climate with { MoistureIterations = 32 },
        });
        merged.Grid.PlanetRadiusKm.Should().Be(3_000, "a property the file does not name keeps the preset value");
        preset.Should().Be(WorldGenPresets.Get("small-5km"), "the base settings are not modified");
        WorldGenSettingsJson.Deserialize("{}", preset).Should().Be(preset);
    }

    // ---- JSON ----

    [Test]
    public void Empty_object_binds_to_defaults()
    {
        WorldGenSettingsJson.Deserialize("{}").Should().Be(new WorldGenSettings());
    }

    [Test]
    public void Omitted_sections_and_properties_take_their_defaults()
    {
        var s = WorldGenSettingsJson.Deserialize("""{ "Climate": { "MoistureIterations": 10 }, "Biomes": {} }""");

        s.Should().Be(new WorldGenSettings { Climate = new() { MoistureIterations = 10 } });
        s.Climate.MoistureSpreadBp.Should().Be(new ClimateSettings().MoistureSpreadBp);
    }

    [Test]
    public void Names_match_case_insensitively()
    {
        WorldGenSettingsJson.Deserialize("""{ "grid": { "frequency": 16 } }""")
            .Should().Be(new WorldGenSettings { Grid = new() { Frequency = 16 } });
    }

    [Test]
    public void Unknown_section_is_an_error_naming_it()
    {
        var act = () => WorldGenSettingsJson.Deserialize("""{ "Climat": { "MoistureIterations": 10 } }""");
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("unknown setting 'Climat'");
    }

    [Test]
    public void Unknown_property_is_an_error_naming_section_and_property()
    {
        var act = () => WorldGenSettingsJson.Deserialize("""{ "Climate": { "MoistureIteration": 10 } }""");
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("unknown setting 'Climate.MoistureIteration'");
    }

    [Test]
    [Arguments("""{ "Grid": { "Frequency": 16, "Frequency": 32 } }""", "duplicate setting 'Grid.Frequency'")]
    [Arguments("""{ "Grid": { "Frequency": 16, "frequency": 32 } }""", "duplicate setting 'Grid.Frequency'")]
    [Arguments("""{ "Grid": { "Frequency": 16 }, "grid": { "PlanetRadiusKm": 6000 } }""", "duplicate setting 'Grid'")]
    public void A_duplicate_key_is_an_error_naming_it(string json, string message)
    {
        var act = () => WorldGenSettingsJson.Deserialize(json);
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage(message);
    }

    [Test]
    public void Old_flat_property_names_are_unknown_settings()
    {
        var act = () => WorldGenSettingsJson.Deserialize("""{ "GridFrequency": 16 }""");
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("unknown setting 'GridFrequency'");
    }

    [Test]
    [Arguments("""{ "Grid": { "Frequency": 1.5 } }""")]
    [Arguments("""{ "Grid": { "Frequency": "16" } }""")]
    [Arguments("""{ "Grid": { "Frequency": null } }""")]
    [Arguments("""{ "Grid": { "Frequency": 3000000000 } }""")]
    public void Non_integer_values_are_an_error(string json)
    {
        var act = () => WorldGenSettingsJson.Deserialize(json);
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("setting 'Grid.Frequency' must be an integer*");
    }

    [Test]
    [Arguments("""{ "Grid": 16 }""")]
    [Arguments("""{ "Grid": null }""")]
    public void A_section_must_be_an_object(string json)
    {
        var act = () => WorldGenSettingsJson.Deserialize(json);
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("setting 'Grid' must be a JSON object*");
    }

    [Test]
    public void Default_settings_round_trip_through_json()
    {
        var json = WorldGenSettingsJson.Serialize(new WorldGenSettings(), indented: true);
        WorldGenSettingsJson.Deserialize(json).Should().Be(new WorldGenSettings());
    }

    [Test]
    public void Every_leaf_round_trips_with_a_non_default_value()
    {
        var settings = new WorldGenSettings();
        var i = 0;
        foreach (var (section, leaf) in AllLeaves())
        {
            var copy = section.GetValue(settings)!;
            leaf.SetValue(copy, 1_000 + i++);
        }

        var back = WorldGenSettingsJson.Deserialize(WorldGenSettingsJson.Serialize(settings));
        back.Should().Be(settings);
        back.Should().NotBe(new WorldGenSettings());
    }

    [Test]
    public void Serialized_json_lists_every_section_and_leaf()
    {
        using var doc = JsonDocument.Parse(WorldGenSettingsJson.Serialize(new WorldGenSettings()));
        foreach (var (section, leaf) in AllLeaves())
            doc.RootElement.GetProperty(section.Name).GetProperty(leaf.Name).GetInt32()
                .Should().Be((int)leaf.GetValue(section.GetValue(new WorldGenSettings()))!);
    }

    [Test]
    public void System_text_json_uses_the_same_form_for_the_type()
    {
        var settings = new WorldGenSettings { Areas = new() { SeaMaxCells = 7 } };
        var json = JsonSerializer.Serialize(settings);
        json.Should().Be(WorldGenSettingsJson.Serialize(settings));
        JsonSerializer.Deserialize<WorldGenSettings>(json).Should().Be(settings);
        var unknown = () => JsonSerializer.Deserialize<WorldGenSettings>("""{ "Areas": { "Typo": 1 } }""");
        unknown.Should().Throw<WorldGenSettingsJsonException>().WithMessage("unknown setting 'Areas.Typo'");
    }

    // ---- Behaviour of settings that replaced constants ----

    [Test]
    public void Saturation_power_table_for_a_ten_degree_doubling_matches_the_version_2_constants()
    {
        ClimateStage.PowersOfTwoPpm(10).Should().Equal(
            1_000_000, 1_071_773, 1_148_698, 1_231_144, 1_319_508, 1_414_214, 1_515_717, 1_624_505, 1_741_101, 1_866_066);
    }

    [Test]
    public void Biome_thresholds_come_from_settings()
    {
        BiomeStage.MainType(1_600, 0, 150, 600).Should().Be(Biome.Hills);
        BiomeStage.MainType(new BiomeSettings { HillsHeightM = 1_700 }, 1_600, 0, 150, 600).Should().Be(Biome.Grassland);
        BiomeStage.MainType(new BiomeSettings { GrasslandBelowMm = 600 }, 0, 0, 150, 650).Should().Be(Biome.TemperateForest);
        BiomeStage.MainType(new BiomeSettings { MountainHeightM = 1_550, HillsHeightM = 1_500 }, 1_600, 0, 150, 600).Should().Be(Biome.Mountain);
    }

    [Test]
    public void Non_default_settings_change_generated_output()
    {
        var baseline = new WorldGenSettings { Grid = new() { Frequency = 8, RegionMultiplier = 2, TileMultiplier = 2 } };
        var a = WorldGenerator.Generate(5, baseline).Stages;
        var b = WorldGenerator.Generate(5, baseline with { Elevation = new() { NoiseOctaves = 2 } }).Stages;
        var c = WorldGenerator.Generate(5, baseline with { Winds = new() { PeakStrength = 200 } }).Stages;
        var d = WorldGenerator.Generate(5, baseline with { Biomes = new() { IceBelowDeciC = -50, TundraBelowDeciC = 100, TaigaBelowDeciC = 200, TemperateMaxDeciC = 260 } }).Stages;

        Hash(b, WorldGenStage.Tectonics).Should().NotBe(Hash(a, WorldGenStage.Tectonics));
        Hash(c, WorldGenStage.Tectonics).Should().Be(Hash(a, WorldGenStage.Tectonics));
        Hash(c, WorldGenStage.Winds).Should().NotBe(Hash(a, WorldGenStage.Winds));
        Hash(d, WorldGenStage.TileHydrology).Should().Be(Hash(a, WorldGenStage.TileHydrology));
        Hash(d, WorldGenStage.TileLandform).Should().NotBe(Hash(a, WorldGenStage.TileLandform));
    }

    private static ulong Hash(IReadOnlyList<StageResult> stages, WorldGenStage stage) => stages.Single(s => s.Stage == stage).Hash;

    [Test]
    public void Roads_section_reads_from_json_and_rejects_unknown_names()
    {
        var s = WorldGenSettingsJson.Deserialize("{\"Roads\": {\"LakeMaxTier\": 2, \"RoadDiscountPct\": 80}}");
        (s.Roads.LakeMaxTier, s.Roads.RoadDiscountPct, s.Roads.BaseStepCost).Should().Be((2, 80, 100));
        var act = () => WorldGenSettingsJson.Deserialize("{\"Roads\": {\"Distance\": 1}}");
        act.Should().Throw<WorldGenSettingsJsonException>().WithMessage("*Roads.Distance*");
        WorldGenSettingsJson.Deserialize(WorldGenSettingsJson.Serialize(s)).Roads.Should().Be(s.Roads);
    }
}
