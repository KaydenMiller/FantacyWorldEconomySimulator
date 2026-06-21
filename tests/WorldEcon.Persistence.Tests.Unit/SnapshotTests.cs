using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Snapshots;

namespace WorldEcon.Persistence.Tests.Unit;

public class SnapshotTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static async Task<(WorldId world, RegionId region)> SeedAsync(string path)
    {
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "C").Value;
        var country = Country.Create(world.Id, continent.Id, "Co").Value;
        var region = Region.Create(world.Id, country.Id, "R").Value;
        var s = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(s);
        await ctx.SaveChangesAsync();
        return (world.Id, region.Id);
    }

    [Test]
    public async Task Snapshot_ProducesIndependentCopy_AndCompareDetectsDivergence()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.db");
        var snapPath = Path.Combine(dir, "snap.db");
        try
        {
            var (worldId, regionId) = await SeedAsync(mainPath);

            await new SqliteSnapshotService().CaptureAsync(mainPath, snapPath);
            File.Exists(snapPath).Should().BeTrue();

            await using (var ctx = NewContextOnFile(mainPath))
            {
                ctx.Settlements.Add(Settlement.Create(worldId, regionId, "Riverwood", SettlementType.Village, 1, 1, 800).Value);
                await ctx.SaveChangesAsync();
            }

            var diff = await new StructuralCompareService().CompareAsync(snapPath, mainPath, worldId);
            diff.AddedSettlements.Should().ContainSingle(name => name == "Riverwood");
            diff.RemovedSettlements.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Branch_DivergesIndependently_FromParent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.db");
        var branchPath = Path.Combine(dir, "branch.db");
        try
        {
            var (worldId, regionId) = await SeedAsync(mainPath);

            await new FileBranchService(new SqliteSnapshotService()).BranchAsync(mainPath, branchPath);

            await using (var ctx = NewContextOnFile(branchPath))
            {
                ctx.Settlements.Add(Settlement.Create(worldId, regionId, "BranchTown", SettlementType.Town, 5, 5, 1000).Value);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(mainPath))
                (await ctx.Settlements.CountAsync()).Should().Be(1);

            await using (var ctx = NewContextOnFile(branchPath))
                (await ctx.Settlements.CountAsync()).Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Compare_DetectsChangedSettlement()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.db");
        var snapPath = Path.Combine(dir, "snap.db");
        try
        {
            var (worldId, _) = await SeedAsync(mainPath);
            await new SqliteSnapshotService().CaptureAsync(mainPath, snapPath);

            // Mutate the seeded "Hammerfell" settlement's population in main only (raw SQL — no domain mutator yet).
            await using (var ctx = NewContextOnFile(mainPath))
            {
                await ctx.Database.ExecuteSqlRawAsync(
                    "UPDATE settlements SET \"Population\" = 99999 WHERE \"Name\" = 'Hammerfell';");
            }

            var diff = await new StructuralCompareService().CompareAsync(snapPath, mainPath, worldId);
            diff.ChangedSettlements.Should().ContainSingle(name => name == "Hammerfell");
            diff.AddedSettlements.Should().BeEmpty();
            diff.RemovedSettlements.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Snapshot_OverExistingDest_AfterDestWasOpened_Succeeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.db");
        var snapPath = Path.Combine(dir, "snap.db");
        try
        {
            await SeedAsync(mainPath);
            var svc = new SqliteSnapshotService();

            await svc.CaptureAsync(mainPath, snapPath);

            // Open the dest as a context (pooled), then dispose — leaves a pooled handle.
            await using (var ctx = NewContextOnFile(snapPath))
                _ = await ctx.Settlements.CountAsync();

            // Re-snapshot over the existing dest must succeed (no IOException from a stale pooled handle).
            var act = async () => await svc.CaptureAsync(mainPath, snapPath);
            await act.Should().NotThrowAsync();
            File.Exists(snapPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
