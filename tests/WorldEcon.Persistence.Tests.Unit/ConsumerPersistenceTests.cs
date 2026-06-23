using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Persistence.Tests.Unit;

public class ConsumerPersistenceTests
{
    [Test]
    public async Task Consumer_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cons-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldId(Guid.NewGuid());
            var seat = new SettlementId(Guid.NewGuid());
            db.Consumers.Add(RepresentativeConsumer.Create(worldId, seat, 1000, new Money(500)).Value);
            await db.SaveChangesAsync();

            var c = await db.Consumers.SingleAsync();
            c.Size.Should().Be(1000);
            c.Budget.Should().Be(new Money(500));
            c.Seat.Should().Be(seat);
        }
        finally { File.Delete(path); }
    }
}
