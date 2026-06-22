using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Persistence.Tests.Unit;

public class WorldCurrencyPersistenceTests
{
    private static WorldDbContext NewContextOnFile(string path)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new WorldDbContext(options);
    }

    [Test]
    public async Task World_Currency_SurvivesRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_currency_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("CurrencyTest", 1UL, CalendarDefinition.Default, "1.0.0").Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var loaded = await ctx.Worlds.SingleAsync();

                // Denominations survive
                loaded.Currency.Denominations.Should().HaveCount(4);
                loaded.Currency.Denominations[0].Symbol.Should().Be("c");
                loaded.Currency.Denominations[0].Units.Should().Be(1);
                loaded.Currency.Denominations[1].Symbol.Should().Be("s");
                loaded.Currency.Denominations[1].Units.Should().Be(10);
                loaded.Currency.Denominations[2].Symbol.Should().Be("g");
                loaded.Currency.Denominations[2].Units.Should().Be(100);
                loaded.Currency.Denominations[3].Symbol.Should().Be("p");
                loaded.Currency.Denominations[3].Units.Should().Be(1000);

                // Format works correctly after reload
                loaded.Currency.Format(new Money(321)).Should().Be("3g 2s 1c");
                loaded.Currency.Format(new Money(300)).Should().Be("3g");
                loaded.Currency.Format(new Money(5)).Should().Be("5c");
                loaded.Currency.Format(new Money(0)).Should().Be("0c");
                loaded.Currency.Format(new Money(-321)).Should().Be("-3g 2s 1c");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
