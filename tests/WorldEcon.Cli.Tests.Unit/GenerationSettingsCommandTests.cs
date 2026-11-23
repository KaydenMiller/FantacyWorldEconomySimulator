using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Cli.Tests.Unit;

/// <summary>
/// <c>globe defaults</c>, <c>new --generation</c> and <c>globe settings</c> through <see cref="CommandRunner"/>.
/// Output is read from TUnit's per-test console capture. The tests do not run in parallel because each one runs the CLI
/// end to end against its own temporary database and files.
/// </summary>
[NotInParallel]
public class GenerationSettingsCommandTests
{
    // TUnit captures console output per test; the slice written during this call is the command's output.
    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var context = TestContext.Current!;
        var outStart = context.Output.GetStandardOutput().Length;
        var errStart = context.Output.GetErrorOutput().Length;
        var exit = await CommandRunner.RunAsync(args);
        return (exit, context.Output.GetStandardOutput()[outStart..], context.Output.GetErrorOutput()[errStart..]);
    }

    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), $"cli_{Guid.NewGuid():N}{extension}");

    private static void DeleteDb(string path)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Test]
    public async Task Globe_defaults_prints_json_that_parses_back_to_the_defaults()
    {
        var (exit, output, _) = await RunAsync("globe", "defaults");

        exit.Should().Be(0);
        WorldGenSettingsJson.Deserialize(output).Should().Be(new WorldGenSettings());
    }

    [Test]
    public async Task New_with_a_generation_file_records_the_overrides_and_globe_settings_prints_them()
    {
        var db = TempPath(".db");
        var file = TempPath(".json");
        await File.WriteAllTextAsync(file, """{ "Grid": { "Frequency": 16 }, "Climate": { "MoistureIterations": 32 } }""");
        try
        {
            var (newExit, newOut, newErr) = await RunAsync("new", db, "--generation", file);
            newExit.Should().Be(0, newErr);
            newOut.Should().Contain("Globe cells: 2,562");

            var (exit, output, _) = await RunAsync("globe", "settings", db);

            exit.Should().Be(0);
            WorldGenSettingsJson.Deserialize(output).Should().Be(
                new WorldGenSettings { Grid = new() { Frequency = 16 }, Climate = new() { MoistureIterations = 32 } });
        }
        finally
        {
            DeleteDb(db);
            File.Delete(file);
        }
    }

    [Test]
    [Arguments("""{ "Grid": { "Frequncy": 16 } }""", "*unknown setting 'Grid.Frequncy'*")]
    [Arguments("""{ "Grid": { "Frequency": 0 } }""", "*Grid.Frequency is 0*")]
    [Arguments("""{ "Grid": """, "*invalid*")]
    public async Task New_rejects_a_bad_generation_file_before_creating_the_database(string json, string message)
    {
        var db = TempPath(".db");
        var file = TempPath(".json");
        await File.WriteAllTextAsync(file, json);
        try
        {
            var (exit, _, err) = await RunAsync("new", db, "--generation", file);

            exit.Should().Be(1);
            err.Should().Match(message);
            File.Exists(db).Should().BeFalse();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task New_rejects_unexpected_arguments()
    {
        var (exit, _, err) = await RunAsync("new", TempPath(".db"), "--generaton", "x.json");

        exit.Should().Be(1);
        err.Should().Contain("new <dbPath> [--preset earth-5km|earth-10km|small-5km] [--generation <file.json>]");
    }
}
