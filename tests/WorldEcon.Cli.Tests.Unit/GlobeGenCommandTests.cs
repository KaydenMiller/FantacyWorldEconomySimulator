using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace WorldEcon.Cli.Tests.Unit;

/// <summary><c>globe gen</c> (generation without a database, checkpoints) and <c>new --seed --resume</c>.</summary>
[NotInParallel]
public class GlobeGenCommandTests
{
    private const string SmallJson = """{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 } }""";

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var context = TestContext.Current!;
        var outStart = context.Output.GetStandardOutput().Length;
        var errStart = context.Output.GetErrorOutput().Length;
        var exit = await CommandRunner.RunAsync(args);
        return (exit, context.Output.GetStandardOutput()[outStart..], context.Output.GetErrorOutput()[errStart..]);
    }

    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), $"cli_gen_{Guid.NewGuid():N}{extension}");

    private static async Task<string> SmallSettingsAsync()
    {
        var file = TempPath(".json");
        await File.WriteAllTextAsync(file, SmallJson);
        return file;
    }

    // "Plates  0123abcd... 12 ms (loaded)" → (stage, hash, loaded)
    private static List<(string Stage, string Hash, bool Loaded)> StageLines(string output) =>
        Regex.Matches(output, @"^\s*(\w+)\s+([0-9a-f]{16})\s+\d+ ms( \(loaded\))?\s*$", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success)).ToList();

    [Test]
    public async Task Gen_save_after_then_resume_prints_loaded_stages_and_same_hashes()
    {
        var settings = await SmallSettingsAsync();
        var ckpt = TempPath(".wgckpt");
        try
        {
            var (exit, output, err) = await RunAsync("globe", "gen", "--generation", settings, "--save-after", "Tectonics", ckpt, "--stop-after", "Climate");
            exit.Should().Be(0, err);
            File.Exists(ckpt).Should().BeTrue();
            var fresh = StageLines(output);
            fresh.Select(l => l.Stage).Should().Equal("Tectonics", "SeaLevel", "Currents", "Winds", "Climate");
            fresh.Should().OnlyContain(l => !l.Loaded);

            var (exit2, output2, err2) = await RunAsync("globe", "gen", "--resume", ckpt, "--stop-after", "Climate");
            exit2.Should().Be(0, err2);
            var resumed = StageLines(output2);
            resumed.Select(l => l.Hash).Should().Equal(fresh.Select(l => l.Hash));
            resumed.Select(l => l.Loaded).Should().Equal(true, false, false, false, false);
        }
        finally
        {
            File.Delete(settings);
            File.Delete(ckpt);
        }
    }

    [Test]
    public async Task Gen_resume_with_different_seed_is_refused()
    {
        var settings = await SmallSettingsAsync();
        var ckpt = TempPath(".wgckpt");
        try
        {
            (await RunAsync("globe", "gen", "--generation", settings, "--save-after", "Tectonics", ckpt, "--stop-after", "Tectonics")).Exit.Should().Be(0);
            var (exit, _, err) = await RunAsync("globe", "gen", "--resume", ckpt, "--seed", "7", "--stop-after", "SeaLevel");
            exit.Should().Be(1);
            err.Should().Contain("seed differs (42 → 7)");
        }
        finally
        {
            File.Delete(settings);
            File.Delete(ckpt);
        }
    }

    [Test]
    public async Task Gen_resume_with_changed_upstream_setting_names_stage_and_section()
    {
        var settings = await SmallSettingsAsync();
        var changed = TempPath(".json");
        var ckpt = TempPath(".wgckpt");
        await File.WriteAllTextAsync(changed, """{ "SeaLevel": { "LandFractionBp": 3200 } }""");
        try
        {
            (await RunAsync("globe", "gen", "--generation", settings, "--save-after", "Tectonics", ckpt, "--stop-after", "Tectonics")).Exit.Should().Be(0);
            var (exit, _, err) = await RunAsync("globe", "gen", "--resume", ckpt, "--generation", changed, "--stop-after", "Climate");
            exit.Should().Be(1);
            err.Should().Contain("Tectonics: section SeaLevel differs");
        }
        finally
        {
            File.Delete(settings);
            File.Delete(changed);
            File.Delete(ckpt);
        }
    }

    [Test]
    public async Task Gen_save_after_a_tile_stage_is_refused()
    {
        var (exit, _, err) = await RunAsync("globe", "gen", "--save-after", "TileHydrology", TempPath(".wgckpt"));
        exit.Should().Be(1);
        err.Should().Contain("only globe stages (Tectonics, SeaLevel, Currents, Winds, Climate) can be checkpointed");
    }

    [Test]
    public async Task Gen_resume_of_a_corrupt_file_names_the_file()
    {
        var bad = TempPath(".wgckpt");
        await File.WriteAllTextAsync(bad, "garbage");
        try
        {
            var (exit, _, err) = await RunAsync("globe", "gen", "--resume", bad);
            exit.Should().Be(1);
            err.Should().Contain(bad).And.Contain("not a checkpoint file");
        }
        finally
        {
            File.Delete(bad);
        }
    }

    [Test]
    public async Task Gen_png_writes_one_file_per_globe_stage()
    {
        var settings = await SmallSettingsAsync();
        var dir = Directory.CreateTempSubdirectory("cli_gen_png_").FullName;
        try
        {
            var (exit, _, err) = await RunAsync("globe", "gen", "--generation", settings, "--stop-after", "Winds", "--png", "auto", dir);
            exit.Should().Be(0, err);
            Directory.GetFiles(dir, "*.png").Select(Path.GetFileName).Order().Should().Equal(
                "01-Tectonics.png", "02-SeaLevel.png", "03-Currents.png", "04-Winds.png");
        }
        finally
        {
            File.Delete(settings);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task New_with_seed_creates_world_with_that_seed()
    {
        var settings = await SmallSettingsAsync();
        var db = TempPath(".db");
        try
        {
            var (exit, output, err) = await RunAsync("new", db, "--generation", settings, "--seed", "7");
            exit.Should().Be(0, err);
            output.Should().Contain("seed 7)");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(settings);
            File.Delete(db);
        }
    }

    [Test]
    public async Task New_with_resume_uses_the_checkpoint_and_refuses_another_seed()
    {
        var settings = await SmallSettingsAsync();
        var ckpt = TempPath(".wgckpt");
        var db = TempPath(".db");
        var db2 = TempPath(".db");
        try
        {
            (await RunAsync("globe", "gen", "--generation", settings, "--save-after", "Climate", ckpt, "--stop-after", "Climate")).Exit.Should().Be(0);
            var (exit, output, err) = await RunAsync("new", db, "--resume", ckpt);
            exit.Should().Be(0, err);
            output.Should().Contain("seed 42)").And.Contain("Globe cells: 642");

            var (exit2, _, err2) = await RunAsync("new", db2, "--resume", ckpt, "--seed", "7");
            exit2.Should().Be(1);
            err2.Should().Contain("seed differs");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(settings);
            File.Delete(ckpt);
            File.Delete(db);
            File.Delete(db2);
        }
    }

    [Test]
    public async Task Gen_metrics_prints_landmasses_and_belt_reference()
    {
        var settings = await SmallSettingsAsync();
        try
        {
            var (exit, output, err) = await RunAsync("globe", "gen", "--generation", settings, "--stop-after", "Climate", "--metrics");
            exit.Should().Be(0, err);
            output.Should().Contain("Landmasses (over 1% of land):").And.Contain("Himalaya 151–350 km");
        }
        finally
        {
            File.Delete(settings);
        }
    }
}
