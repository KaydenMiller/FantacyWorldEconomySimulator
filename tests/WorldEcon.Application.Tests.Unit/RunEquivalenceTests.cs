using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Application.Simulation;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Tests.Unit;

/// <summary>
/// A run stepped boundary by boundary (as Play does) leaves the same economy as one advance of the same length. The
/// simulation is not bit-for-bit repeatable today (rows created during a run get random ids, and phases visit some rows
/// in id order), so two identical advances already differ in row contents and by a few rows in some tables; this test
/// compares the world row exactly, requires both runs to have traded (caravans exist) and the row count of every other
/// economy table to be within 25 % (at least 3 rows). It is a smoke test on real phases: before per-boundary saves, 60
/// one-day steps left 20 caravans where one 60-day advance left none. The exact rule (rows added at a boundary are
/// visible to queries at the next, however the run is stepped) is pinned by EngineRunTests.AddedRows_VisibleAtNextBoundary_SameForStepsAndOneAdvance.
/// </summary>
public class RunEquivalenceTests
{
    private const long Days = 60;

    // Row counts compared within a tolerance (see the class summary).
    private static readonly string[] Tables =
    [
        "stockpiles", "shops", "merchants", "merchant_trips", "caravans", "caravan_legs", "consumers", "work_orders",
        "travel_knowledge", "shop_price_beliefs", "money_ledger_snapshots", "money_ledger_lines", "log_events",
    ];

    [Test]
    public async Task SeveralSteps_EqualOneAdvance()
    {
        var seeded = await SampleWorld.SeedTempDbAsync();
        var a = SampleWorld.Copy(seeded);
        var b = SampleWorld.Copy(seeded);
        try
        {
            (await WorldFileJournal.TryEnableWalAsync(a)).Should().BeTrue();
            long start;
            await using (var runner = (await SimulationRunner.OpenAsync(a)).Value)
            {
                start = runner.CurrentTick;
                var end = start + Days * Tick.DefaultMinutesPerDay;
                var steps = 0;
                while (runner.CurrentTick < end)
                {
                    var step = await runner.StepToAsync(Math.Min(runner.NextBoundaryTick, end));
                    step.IsError.Should().BeFalse();
                    steps++;
                }

                steps.Should().BeGreaterThanOrEqualTo((int)Days);
                await runner.EndAsync();
            }

            await using (var ctx = SampleWorld.NewContext(b))
            {
                var sim = await SimulationContext.LoadAsync(ctx, (await ctx.Worlds.FirstAsyncNoTracking()).Id);
                await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Days * Tick.DefaultMinutesPerDay);
            }

            SqliteConnection.ClearAllPools();
            (await DumpAsync(a, "worlds")).Should().Equal(await DumpAsync(b, "worlds"));
            (await CountAsync(a, "caravans")).Should().BeGreaterThan(0, "the sample world trades within 60 days");
            (await CountAsync(b, "caravans")).Should().BeGreaterThan(0, "the sample world trades within 60 days");
            foreach (var table in Tables)
            {
                var stepped = await CountAsync(a, table);
                var single = await CountAsync(b, table);
                stepped.Should().BeCloseTo(single, (ulong)Math.Max(3, single / 4), $"row count of {table}");
            }
        }
        finally
        {
            SampleWorld.Delete(seeded);
            SampleWorld.Delete(a);
            SampleWorld.Delete(b);
        }
    }

    private static async Task<long> CountAsync(string path, string table)
    {
        await using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Every row of <paramref name="table"/> as text, sorted.</summary>
    private static async Task<List<string>> DumpAsync(string path, string table)
    {
        await using var conn = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table}";
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var cells = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                cells.Add($"{reader.GetName(i)}={(reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))}");
            }

            rows.Add(string.Join("|", cells));
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }
}

internal static class WorldQueryExtensions
{
    public static Task<Domain.Geography.World> FirstAsyncNoTracking(this Microsoft.EntityFrameworkCore.DbSet<Domain.Geography.World> worlds)
        => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(worlds));
}
