using Path = System.IO.Path;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;

namespace WorldEcon.Persistence.Tests.Unit;

public class RepositoryTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task SettlementRepository_AddGetList_ByWorld_IsDeterministicallyOrdered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "C").Value;
            var country = Country.Create(world.Id, continent.Id, "Co").Value;
            var region = Region.Create(world.Id, country.Id, "R").Value;
            var s1 = Settlement.Create(world.Id, region.Id, "A", SettlementType.Town, 0, 0, 100).Value;
            var s2 = Settlement.Create(world.Id, region.Id, "B", SettlementType.Town, 1, 1, 200).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.Regions.Add(region);
                ctx.Settlements.AddRange(s1, s2);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var repo = new SettlementRepository(ctx);

                var fetched = await repo.GetAsync(s1.Id);
                fetched!.Name.Should().Be("A");

                var all = await repo.ListByWorldAsync(world.Id);
                all.Should().HaveCount(2);
                var again = await repo.ListByWorldAsync(world.Id);
                again.Select(x => x.Id).Should().Equal(all.Select(x => x.Id));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
