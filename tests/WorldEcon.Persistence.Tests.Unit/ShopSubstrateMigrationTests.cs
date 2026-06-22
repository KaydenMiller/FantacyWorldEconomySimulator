using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Persistence.Tests.Unit;

public class ShopSubstrateMigrationTests
{
    [Test]
    public async Task Migration_ReownsSettlementMarketStock_IntoPublicMarketShop_Conserving()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shopmig-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            // Migrate to just BEFORE ShopSubstrate. Use the migration immediately prior
            // (AddWorldCurrency); adjust the id if the chain changes.
            await migrator.MigrateAsync("20260622073704_AddWorldCurrency");

            var worldId = Guid.NewGuid();
            var settlementId = Guid.NewGuid();
            var goodId = Guid.NewGuid();
            // Insert a SettlementMarket stockpile via raw SQL (pre-substrate shape).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO stockpiles (Id, WorldId, OwnerKind, OwnerId, GoodId, Quantity, CostBasis, MarketPrice) " +
                "VALUES ({0},{1},'SettlementMarket',{2},{3},{4},{5},{6})",
                Guid.NewGuid(), worldId, settlementId, goodId, 77L, 20L, 18L);

            // Run ShopSubstrate.
            await migrator.MigrateAsync(); // to latest

            var sp = await db.Stockpiles.SingleAsync();
            sp.OwnerKind.Should().Be(StockpileOwnerKind.Shop);
            sp.Quantity.Should().Be(77);
            sp.CostBasis.Should().Be(new Money(20));
            sp.MarketPrice.Should().Be(new Money(18));

            var shop = await db.Shops.SingleAsync(s => s.Kind == ShopKind.PublicMarket);
            shop.SettlementId.Value.Should().Be(settlementId);
            sp.OwnerId.Should().Be(shop.Id.Value);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Migration_TwoSettlementsTwoGoods_NoCrossAttribution_OneShopEach()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shopmig2-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("20260622073704_AddWorldCurrency");

            var worldId = Guid.NewGuid();
            var s1 = Guid.NewGuid();
            var s2 = Guid.NewGuid();
            var g1 = Guid.NewGuid();
            var g2 = Guid.NewGuid();

            // 2 settlements × 2 goods = 4 SettlementMarket stockpiles, distinct quantities.
            async Task Insert(Guid settle, Guid good, long qty) =>
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO stockpiles (Id, WorldId, OwnerKind, OwnerId, GoodId, Quantity, CostBasis, MarketPrice) " +
                    "VALUES ({0},{1},'SettlementMarket',{2},{3},{4},10,5)",
                    Guid.NewGuid(), worldId, settle, good, qty);
            await Insert(s1, g1, 11);
            await Insert(s1, g2, 12);
            await Insert(s2, g1, 21);
            await Insert(s2, g2, 22);

            await migrator.MigrateAsync(); // ShopSubstrate

            // Exactly one PublicMarket shop per settlement.
            var pubShops = await db.Shops.Where(x => x.Kind == ShopKind.PublicMarket).ToListAsync();
            pubShops.Should().HaveCount(2);
            var s1Shop = pubShops.Single(x => x.SettlementId.Value == s1);
            var s2Shop = pubShops.Single(x => x.SettlementId.Value == s2);

            var sps = await db.Stockpiles.ToListAsync();
            sps.Should().HaveCount(4);
            sps.Should().OnlyContain(x => x.OwnerKind == StockpileOwnerKind.Shop);
            // Each settlement's goods are re-owned to THAT settlement's shop (no cross-attribution).
            sps.Where(x => x.OwnerId == s1Shop.Id.Value).Select(x => x.Quantity).Should().BeEquivalentTo(new[] { 11L, 12L });
            sps.Where(x => x.OwnerId == s2Shop.Id.Value).Select(x => x.Quantity).Should().BeEquivalentTo(new[] { 21L, 22L });
        }
        finally { File.Delete(path); }
    }
}
