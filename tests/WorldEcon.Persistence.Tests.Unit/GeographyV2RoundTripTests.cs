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
            var ocean = PoliticalRegion.Create(world.Id, "Ocean of Lost Souls").Value;
            var link1 = PoliticalRegionContinent.Create(world.Id, ocean.Id, grimovar.Id).Value;
            var link2 = PoliticalRegionContinent.Create(world.Id, ocean.Id, iergoald.Id).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.AddRange(grimovar, iergoald);
                ctx.PoliticalRegions.Add(ocean);
                ctx.PoliticalRegionContinents.AddRange(link1, link2);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var r = await ctx.PoliticalRegions.SingleAsync();
                r.CountryId.Should().BeNull();
                        var continents = await ctx.PoliticalRegionContinents.Where(x => x.PoliticalRegionId == ocean.Id).ToListAsync();
                continents.Should().HaveCount(2);
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
            var region = PoliticalRegion.Create(world.Id, "The Darkwood Deep").Value;
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
                ctx.PoliticalRegions.Add(region);
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
