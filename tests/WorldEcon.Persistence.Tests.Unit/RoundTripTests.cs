using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;

namespace WorldEcon.Persistence.Tests.Unit;

public class RoundTripTests
{
    private static WorldDbContext NewContextOnFile(string path)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new WorldDbContext(options);
    }

    [Test]
    public async Task World_AndGeography_PersistAndReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 4242UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "Mundus").Value;
            var country = Country.Create(world.Id, continent.Id, "Highmark").Value;
            var region = PoliticalRegion.Create(world.Id, "The Reach", country.Id).Value;
            var hammerfell = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 10, 20, 50_000).Value;
            var riverwood = Settlement.Create(world.Id, region.Id, "Riverwood", SettlementType.Village, 12, 25, 800).Value;
            var nodeA = RouteNode.Create(world.Id, RouteNodeKind.Settlement, 10, hammerfell.Id, "Hammerfell").Value;
            var nodeB = RouteNode.Create(world.Id, RouteNodeKind.Settlement, 11, riverwood.Id, "Riverwood").Value;
            var route = Route.Create(world.Id, nodeA.Id, nodeB.Id, RouteCategory.Land, RouteSurface.DirtRoad, 100, 3,
                120_000, 100, 100, 1, 99, RouteProvenance.Generated).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.PoliticalRegions.Add(region);
                ctx.Settlements.AddRange(hammerfell, riverwood);
                ctx.RouteNodes.AddRange(nodeA, nodeB);
                ctx.Routes.Add(route);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var w = await ctx.Worlds.SingleAsync();
                w.Name.Should().Be("Aerth");
                w.Seed.Should().Be(4242UL);
                w.CurrentTick.Should().Be(Tick.Zero);
                w.Calendar.Months.Should().HaveCount(12); // JSON round-trip survived

                (await ctx.Settlements.CountAsync()).Should().Be(2);
                var loadedRoute = await ctx.Routes.SingleAsync();
                loadedRoute.NodeA.Should().Be(nodeA.Id);
                loadedRoute.Category.Should().Be(RouteCategory.Land);

                w.Id.Should().Be(world.Id);                       // get-only Id survives via backing field
                loadedRoute.Id.Should().Be(route.Id);
                var loadedSettlementIds = await ctx.Settlements.Select(s => s.Id).ToListAsync();
                loadedSettlementIds.Should().BeEquivalentTo(new[] { hammerfell.Id, riverwood.Id });
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
