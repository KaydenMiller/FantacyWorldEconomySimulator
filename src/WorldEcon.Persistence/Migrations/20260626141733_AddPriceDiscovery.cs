using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill existing worlds with the gentle defaults (10% / 10% / 20%).
            migrationBuilder.AddColumn<long>(
                name: "BeliefNarrowFractionBasisPoints",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000L);

            migrationBuilder.AddColumn<long>(
                name: "BeliefShiftFractionBasisPoints",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2000L);

            migrationBuilder.AddColumn<long>(
                name: "BeliefWidenFractionBasisPoints",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000L);

            // A valid fallback (1.5× base); existing goods are then backfilled by need tier below.
            migrationBuilder.AddColumn<long>(
                name: "PeakWillingnessMultipleBasisPoints",
                table: "goods",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15000L);

            // Tier-derived backfill for existing goods (Need is stored as a string).
            migrationBuilder.Sql(
                "UPDATE goods SET PeakWillingnessMultipleBasisPoints = CASE Need " +
                "WHEN 'Essential' THEN 40000 WHEN 'Standard' THEN 18000 WHEN 'Comfort' THEN 13000 " +
                "ELSE 15000 END;");

            migrationBuilder.CreateTable(
                name: "shop_price_beliefs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShopId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Low = table.Column<long>(type: "INTEGER", nullable: false),
                    High = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shop_price_beliefs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shop_price_beliefs_ShopId_GoodId",
                table: "shop_price_beliefs",
                columns: new[] { "ShopId", "GoodId" });

            migrationBuilder.CreateIndex(
                name: "IX_shop_price_beliefs_WorldId",
                table: "shop_price_beliefs",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shop_price_beliefs");

            migrationBuilder.DropColumn(
                name: "BeliefNarrowFractionBasisPoints",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "BeliefShiftFractionBasisPoints",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "BeliefWidenFractionBasisPoints",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "PeakWillingnessMultipleBasisPoints",
                table: "goods");
        }
    }
}
