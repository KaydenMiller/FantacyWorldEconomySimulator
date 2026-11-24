using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.TestFixtures;

namespace WorldEcon.Application.Tests.Unit;

public class CaravanPositionsQueryTests
{
    [Test]
    public async Task Run_ReturnsUndeliveredOnly_WithLegs()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var r = (await CaravanPositionsQuery.RunAsync(w.Path)).Value;
        r.Caravans.Select(c => c.Id).Should().BeEquivalentTo([w.Outbound.Id.Value, w.Inbound.Id.Value]);
        var outbound = r.Caravans.Single(c => c.Id == w.Outbound.Id.Value);
        outbound.Legs.Should().Equal(
            new CaravanLegInfo(0, w.RouteAJ.Id.Value, TravelDirection.AtoB, 9_000, 11_000, 50_000, 0),
            new CaravanLegInfo(1, w.RouteJB.Id.Value, TravelDirection.AtoB, 11_000, 13_000, 50_000, 0));
    }

    [Test]
    public async Task Run_CurrentTick_IsWorldTick()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        (await CaravanPositionsQuery.RunAsync(w.Path)).Value.CurrentTick.Should().Be(EconomyTestWorld.Now);
        await using (var ctx = EconomyTestWorld.Ctx(w.Path))
        {
            var world = await ctx.Worlds.SingleAsync();
            world.AdvanceTo(new Tick(12_345));
            await ctx.SaveChangesAsync();
        }

        (await CaravanPositionsQuery.RunAsync(w.Path)).Value.CurrentTick.Should().Be(12_345);
    }

    [Test]
    public async Task Run_Fields_Resolved()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var c = (await CaravanPositionsQuery.RunAsync(w.Path)).Value.Caravans.Single(x => x.Id == w.Inbound.Id.Value);
        c.Should().BeEquivalentTo(new
        {
            OwnerId = w.Merchant.Id.Value, SeatName = "Ashford",
            OriginId = w.Brindle.Id.Value, OriginName = "Brindle",
            DestinationId = w.Ashford.Id.Value, DestinationName = "Ashford",
            GoodId = w.Cloth.Id.Value, GoodName = "Cloth", Quantity = 5L, UnitCostBasis = 150L,
            VehicleClass = "HandCart", DepartTick = 9_500L, ArriveTick = 11_500L, ReturnTick = 13_500L,
        });
    }

    [Test]
    public async Task Run_OrderedById()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var ids = (await CaravanPositionsQuery.RunAsync(w.Path)).Value.Caravans.Select(c => c.Id).ToList();
        ids.Should().BeInAscendingOrder();
    }

    [Test]
    public async Task Run_NoWorld_Error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_empty_{Guid.NewGuid():N}.db");
        try
        {
            await using (var ctx = EconomyTestWorld.Ctx(path)) await ctx.Database.MigrateAsync();
            (await CaravanPositionsQuery.RunAsync(path)).FirstError.Code.Should().Be("world.notFound");
        }
        finally { File.Delete(path); }
    }
}
