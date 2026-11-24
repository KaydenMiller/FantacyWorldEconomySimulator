using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tests.Unit;

public class RemainingTimeTests
{
    private sealed class SyncProgress(Action<(WorldGenStage, double)> report) : IProgress<(WorldGenStage Stage, double Fraction)>
    {
        public void Report((WorldGenStage Stage, double Fraction) value) => report(value);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    public void Below_five_percent_is_estimating()
    {
        var clock = new FakeClock();
        var r = new RemainingTime(clock);
        clock.Now = clock.Now.AddSeconds(3);
        r.Text(0.04).Should().Be("estimating…");
    }

    [Test]
    public void Half_done_after_ten_seconds_is_about_ten_seconds()
    {
        var clock = new FakeClock();
        var r = new RemainingTime(clock);
        clock.Now = clock.Now.AddSeconds(10);
        r.Text(0.5).Should().Be("about 10 s remaining");
    }

    [Test]
    public void Minutes_are_shown_with_seconds()
    {
        var clock = new FakeClock();
        var r = new RemainingTime(clock);
        clock.Now = clock.Now.AddSeconds(40);
        r.Text(0.25).Should().Be("about 2 min 0 s remaining");
    }

    [Test]
    public void Never_negative_at_or_after_one()
    {
        var clock = new FakeClock();
        var r = new RemainingTime(clock);
        clock.Now = clock.Now.AddSeconds(10);
        r.Text(1.0).Should().Be("about 0 s remaining");
        clock.Now = clock.Now.AddSeconds(2);
        r.Text(1.3).Should().Be("about 0 s remaining");
    }

    [Test]
    public void Updates_at_most_once_per_second()
    {
        var clock = new FakeClock();
        var r = new RemainingTime(clock);
        clock.Now = clock.Now.AddSeconds(10);
        var first = r.Text(0.5);
        clock.Now = clock.Now.AddMilliseconds(500);
        r.Text(0.9).Should().Be(first, "less than a second since the last update");
        clock.Now = clock.Now.AddMilliseconds(600);
        r.Text(0.9).Should().NotBe(first);
    }

    [Test]
    public void Progress_is_non_decreasing_within_the_tectonics_stage_and_ends_at_one()
    {
        var reports = new List<(WorldGenStage Stage, double Fraction)>();
        WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 8, RegionMultiplier = 2, TileMultiplier = 2 } },
            new WorldGenOptions(Progress: new SyncProgress(reports.Add)));
        reports.Count(r => r.Stage == WorldGenStage.Tectonics).Should().BeGreaterThan(10, "the stage reports every step");
        reports.Select(r => r.Fraction).Should().BeInAscendingOrder();
        reports[^1].Fraction.Should().Be(1.0);
    }
}
