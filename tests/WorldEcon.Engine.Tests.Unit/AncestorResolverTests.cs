using FluentAssertions;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;

namespace WorldEcon.Engine.Tests.Unit;

public class AncestorResolverTests
{
    [Test]
    public async Task AncestorsOf_Settlement_ReturnsSettlementRegionCountryContinent()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var resolver = new AncestorResolver(s.Db, s.World.Id);
            var chain = await resolver.AncestorsOf(s.Settlement.Id);

            chain.Should().Contain((LogScopeKind.Settlement, s.Settlement.Id.Value));
            chain.Should().Contain((LogScopeKind.Region, s.Region.Id.Value));
            chain.Should().Contain((LogScopeKind.Country, s.Country.Id.Value));
            chain.Should().Contain((LogScopeKind.Continent, s.Continent.Id.Value));
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
