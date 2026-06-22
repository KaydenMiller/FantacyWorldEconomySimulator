using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;

namespace WorldEcon.Persistence.Tests.Unit;

public class GeographyV2RoundTripTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task OceanRegion_NoCountry_SpansMultipleContinents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var grimovar = Continent.Create(world.Id, "Grimovar").Value;
            var iergoald = Continent.Create(world.Id, "Iergoald").Value;
            var ocean = Region.Create(world.Id, "Ocean of Lost Souls", RegionKind.Ocean).Value;
            var link1 = RegionContinent.Create(world.Id, ocean.Id, grimovar.Id).Value;
            var link2 = RegionContinent.Create(world.Id, ocean.Id, iergoald.Id).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.AddRange(grimovar, iergoald);
                ctx.Regions.Add(ocean);
                ctx.RegionContinents.AddRange(link1, link2);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var r = await ctx.Regions.SingleAsync();
                r.CountryId.Should().BeNull();
                r.Kind.Should().Be(RegionKind.Ocean);
                var continents = await ctx.RegionContinents.Where(x => x.RegionId == ocean.Id).ToListAsync();
                continents.Should().HaveCount(2);
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task NestedRegions_ParentSpansChildren_ChildSharedByTwoParents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var yellowstone = Region.Create(world.Id, "Yellowstone", RegionKind.Forest).Value;
            var stateA = Region.Create(world.Id, "State A", RegionKind.Land).Value;
            var stateB = Region.Create(world.Id, "State B", RegionKind.Land).Value;
            // Yellowstone spans State A and State B; State A is also (independently) part of a wider region.
            var wider = Region.Create(world.Id, "The Frontier", RegionKind.Land).Value;
            var c1 = RegionContainment.Create(world.Id, yellowstone.Id, stateA.Id).Value;
            var c2 = RegionContainment.Create(world.Id, yellowstone.Id, stateB.Id).Value;
            var c3 = RegionContainment.Create(world.Id, wider.Id, stateA.Id).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Regions.AddRange(yellowstone, stateA, stateB, wider);
                ctx.RegionContainments.AddRange(c1, c2, c3);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var yellowstoneChildren = await ctx.RegionContainments
                    .Where(x => x.ParentRegionId == yellowstone.Id).ToListAsync();
                yellowstoneChildren.Should().HaveCount(2);

                var stateAParents = await ctx.RegionContainments
                    .Where(x => x.ChildRegionId == stateA.Id).ToListAsync();
                stateAParents.Should().HaveCount(2); // Yellowstone + The Frontier
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task RuinedSettlement_AndContestedClaims_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "Praxus").Value;
            var thaloria = Country.Create(world.Id, continent.Id, "Thaloria").Value;
            var kelFabrel = Country.Create(world.Id, continent.Id, "Kel Fabrel").Value;
            var region = Region.Create(world.Id, "The Darkwood Deep", RegionKind.Forest).Value;
            var xia = Settlement.Create(world.Id, region.Id, "Xia", SettlementType.City, 0, 0, 12000).Value;
            var zeigelith = Settlement.Create(world.Id, region.Id, "Zeigelith", SettlementType.City, 1, 1, 0).Value;
            zeigelith.SetState(SettlementState.Ruined);

            var controls = TerritorialClaim.CreateForSettlement(world.Id, thaloria.Id, xia.Id, ClaimType.Controls).Value;
            var disputes = TerritorialClaim.CreateForSettlement(world.Id, kelFabrel.Id, xia.Id, ClaimType.Disputes).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.AddRange(thaloria, kelFabrel);
                ctx.Regions.Add(region);
                ctx.Settlements.AddRange(xia, zeigelith);
                ctx.TerritorialClaims.AddRange(controls, disputes);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var ruin = await ctx.Settlements.FirstAsync(s => s.Id == zeigelith.Id);
                ruin.State.Should().Be(SettlementState.Ruined);

                var claims = await ctx.TerritorialClaims
                    .Where(c => c.TargetKind == ClaimTargetKind.Settlement && c.TargetId == xia.Id.Value)
                    .ToListAsync();
                claims.Should().HaveCount(2);
                claims.Select(c => c.ClaimType).Should().Contain([ClaimType.Controls, ClaimType.Disputes]);
            }
        }
        finally { File.Delete(path); }
    }
}
