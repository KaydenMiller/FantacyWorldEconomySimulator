using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShopSubstrate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "shops",
                type: "TEXT",
                nullable: false,
                defaultValue: "Retail");

            migrationBuilder.AddColumn<Guid>(
                name: "ProducerShopId",
                table: "resource_endowments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProducerShopId",
                table: "production_nodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_shops_SettlementId_Kind",
                table: "shops",
                columns: new[] { "SettlementId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_resource_endowments_ProducerShopId",
                table: "resource_endowments",
                column: "ProducerShopId");

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_ProducerShopId",
                table: "production_nodes",
                column: "ProducerShopId");

            // Phase 1 substrate: retire the SettlementMarket pool. For each settlement that holds
            // market stockpiles, create a PublicMarket shop and re-own its market stock to it
            // (conserving quantity, cost basis, and market price). Producer shops are created lazily
            // by the engine on first production, so they are not created here.
            migrationBuilder.Sql(@"
INSERT INTO shops (Id, WorldId, SettlementId, Name, MarkupBp, Till, Kind)
SELECT
  lower(
    substr(hex(randomblob(4)),1,8) || '-' ||
    substr(hex(randomblob(2)),1,4) || '-4' ||
    substr(hex(randomblob(2)),2,3) || '-' ||
    substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)),2,3) || '-' ||
    substr(hex(randomblob(6)),1,12)
  ),
  WorldId, OwnerId, 'Town Market', 0, 0, 'PublicMarket'
FROM (SELECT DISTINCT WorldId, OwnerId FROM stockpiles WHERE OwnerKind = 'SettlementMarket');");

            migrationBuilder.Sql(@"
UPDATE stockpiles
SET OwnerId = (SELECT s.Id FROM shops s WHERE s.Kind = 'PublicMarket' AND s.SettlementId = stockpiles.OwnerId),
    OwnerKind = 'Shop'
WHERE OwnerKind = 'SettlementMarket';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shops_SettlementId_Kind",
                table: "shops");

            migrationBuilder.DropIndex(
                name: "IX_resource_endowments_ProducerShopId",
                table: "resource_endowments");

            migrationBuilder.DropIndex(
                name: "IX_production_nodes_ProducerShopId",
                table: "production_nodes");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "shops");

            migrationBuilder.DropColumn(
                name: "ProducerShopId",
                table: "resource_endowments");

            migrationBuilder.DropColumn(
                name: "ProducerShopId",
                table: "production_nodes");
        }
    }
}
