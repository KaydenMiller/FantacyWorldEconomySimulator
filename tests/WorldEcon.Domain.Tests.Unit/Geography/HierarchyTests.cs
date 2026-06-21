using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class HierarchyTests
{
    [Test]
    public void World_Create_SetsFieldsAndStartsAtTickZero()
    {
        var result = World.Create("Aerth", seed: 1234UL, CalendarDefinition.Default, rulesetVersion: "1.0.0");
        result.IsError.Should().BeFalse();
        var w = result.Value;
        w.Name.Should().Be("Aerth");
        w.Seed.Should().Be(1234UL);
        w.CurrentTick.Should().Be(Tick.Zero);
        w.RulesetVersion.Should().Be("1.0.0");
    }

    [Test]
    public void World_Create_RejectsBlankName()
        => World.Create("  ", 1UL, CalendarDefinition.Default, "1.0.0").IsError.Should().BeTrue();

    [Test]
    public void Continent_Create_RejectsBlankName()
        => Continent.Create(WorldId.New(), " ").IsError.Should().BeTrue();

    [Test]
    public void Country_Create_LinksToContinentAndWorld()
    {
        var wid = WorldId.New();
        var cid = ContinentId.New();
        var c = Country.Create(wid, cid, "Highmark").Value;
        c.WorldId.Should().Be(wid);
        c.ContinentId.Should().Be(cid);
        c.Name.Should().Be("Highmark");
    }

    [Test]
    public void Region_Create_LinksToCountryAndWorld()
    {
        var wid = WorldId.New();
        var coid = CountryId.New();
        var r = Region.Create(wid, coid, "The Reach").Value;
        r.WorldId.Should().Be(wid);
        r.CountryId.Should().Be(coid);
    }
}
