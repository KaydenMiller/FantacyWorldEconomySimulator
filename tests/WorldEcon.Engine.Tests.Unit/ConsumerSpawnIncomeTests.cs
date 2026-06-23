using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumerSpawnIncomeTests
{
    [Test]
    public async Task Advance_Week_SpawnsConsumers_AndGrantsIncome()
    {
        var s = await LogTestWorld.CreateAsync(); // settlement population 50000
        try
        {
            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerWeek);

            var consumers = await s.Db.Consumers.Where(c => c.Seat == s.Settlement.Id).ToListAsync();
            consumers.Should().NotBeEmpty();                              // population/Size consumers
            consumers.Sum(c => c.Size).Should().BeGreaterThan(0);
            consumers.Should().OnlyContain(c => c.Budget.Units > 0);      // income granted
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
