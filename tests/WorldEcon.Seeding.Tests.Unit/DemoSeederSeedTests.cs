using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Persistence;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Seeding.Tests.Unit;

/// <summary><see cref="DemoSeeder"/> with a chosen seed and with a world generated beforehand (tuning mode's Save as world).</summary>
public class DemoSeederSeedTests
{
    private static readonly WorldGenSettings Small = new() { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };

    private static async Task<T> WithDb<T>(Func<string, Task<T>> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"demo_seed_{Guid.NewGuid():N}.db");
        try
        {
            return await body(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Test]
    public void Default_seed_is_42()
    {
        DemoSeeder.DefaultSeed.Should().Be(42UL);
    }

    [Test]
    public async Task Default_creates_world_with_seed_42()
    {
        var (world, globe) = await WithDb(p => DemoSeeder.CreateAsync(p, Small));
        world.Seed.Should().Be(42UL);
        globe.Generated.WorldSeed.Should().Be(42UL);
    }

    [Test]
    public async Task Other_seed_creates_world_with_that_seed_and_its_terrain()
    {
        var (world, globe) = await WithDb(p => DemoSeeder.CreateAsync(p, Small, seed: 7));
        world.Seed.Should().Be(7UL);
        globe.Generated.Stages.Select(s => s.Hash).Should().Equal(WorldGenerator.Generate(7, Small).Stages.Select(s => s.Hash));
    }

    [Test]
    public async Task Pre_generated_world_is_used_without_generating_again()
    {
        var generated = WorldGenerator.Generate(7, Small);
        var (world, globe) = await WithDb(p => DemoSeeder.CreateAsync(p, Small, seed: 7, generated: generated));
        world.Seed.Should().Be(7UL);
        globe.Generated.Should().BeSameAs(generated);
    }

    [Test]
    public async Task Pre_generated_world_with_another_seed_is_refused()
    {
        var generated = WorldGenerator.Generate(7, Small);
        var act = () => WithDb(p => DemoSeeder.CreateAsync(p, Small, seed: 42, generated: generated));
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*seed 7*seed 42*");
    }

    [Test]
    public void GenerateGlobe_world_records_its_seed()
    {
        WorldGenerator.GenerateGlobe(9, Small).WorldSeed.Should().Be(9UL);
    }
}
