using FluentAssertions;
using WorldEcon.Gui.ViewModels;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Gui.Tests.Unit;

public class GenerationSettingsViewModelTests
{
    [Test]
    public void Defaults_are_seed_42_and_earth_5km()
    {
        var vm = new GenerationSettingsViewModel();
        vm.SeedText.Should().Be("42");
        vm.Seed.Should().Be(42UL);
        vm.Preset.Should().Be(WorldGenPresets.Earth5Km);
        vm.Presets.Should().Equal(WorldGenPresets.Names);
        vm.SettingsFile.Should().BeNull();
        vm.CanGenerate.Should().BeTrue();
        vm.Error.Should().BeNull();
    }

    [Test]
    [Arguments("-1")]
    [Arguments("abc")]
    [Arguments("")]
    [Arguments(" 42")]
    [Arguments("18446744073709551616")]
    public void Invalid_seed_text_disables_generate(string text)
    {
        var vm = new GenerationSettingsViewModel { SeedText = text };
        vm.CanGenerate.Should().BeFalse();
        vm.Error.Should().Contain("seed");
    }

    [Test]
    public void Max_ulong_seed_is_accepted()
    {
        var vm = new GenerationSettingsViewModel { SeedText = "18446744073709551615" };
        vm.CanGenerate.Should().BeTrue();
        vm.Seed.Should().Be(ulong.MaxValue);
    }

    [Test]
    public void Fixing_the_seed_clears_the_error()
    {
        var vm = new GenerationSettingsViewModel { SeedText = "x" };
        vm.SeedText = "7";
        vm.Error.Should().BeNull();
        vm.CanGenerate.Should().BeTrue();
    }

    [Test]
    public void Build_layers_file_over_preset()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gen_{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "Plates": { "Count": 12 } }""");
        try
        {
            var vm = new GenerationSettingsViewModel { Preset = WorldGenPresets.Small5Km, SettingsFile = file };
            var s = vm.Build();
            s.Plates.Count.Should().Be(12);
            s.Grid.Frequency.Should().Be(32);
            vm.Error.Should().BeNull();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void Build_with_invalid_file_sets_error_and_throws()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gen_{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "Plates": { "Nope": 1 } }""");
        try
        {
            var vm = new GenerationSettingsViewModel { SettingsFile = file };
            var act = () => vm.Build();
            act.Should().Throw<InvalidOperationException>();
            vm.Error.Should().Contain("Nope");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void Build_with_out_of_range_value_sets_error()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gen_{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "Plates": { "Count": 0 } }""");
        try
        {
            var vm = new GenerationSettingsViewModel { SettingsFile = file };
            var act = () => vm.Build();
            act.Should().Throw<InvalidOperationException>();
            vm.Error.Should().Contain("Plates.Count");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void Missing_file_sets_error()
    {
        var vm = new GenerationSettingsViewModel { SettingsFile = "/nonexistent/settings.json" };
        var act = () => vm.Build();
        act.Should().Throw<InvalidOperationException>();
        vm.Error.Should().Contain("does not exist");
    }

    [Test]
    public void ClearFile_removes_the_file()
    {
        var vm = new GenerationSettingsViewModel { SettingsFile = "x.json" };
        vm.ClearFile();
        vm.SettingsFile.Should().BeNull();
    }
}
