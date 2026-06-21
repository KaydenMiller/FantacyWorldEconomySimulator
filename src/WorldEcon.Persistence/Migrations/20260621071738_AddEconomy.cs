using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEconomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "goods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    BaseValue = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseUnit = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<string>(type: "TEXT", nullable: false),
                    ShelfLifeTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Divisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MarkupBp = table.Column<int>(type: "INTEGER", nullable: false),
                    Till = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shops", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stockpiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKind = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    CostBasis = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stockpiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_goods_WorldId",
                table: "goods",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_shops_SettlementId",
                table: "shops",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_shops_WorldId",
                table: "shops",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_stockpiles_OwnerKind_OwnerId_GoodId",
                table: "stockpiles",
                columns: new[] { "OwnerKind", "OwnerId", "GoodId" });

            migrationBuilder.CreateIndex(
                name: "IX_stockpiles_WorldId",
                table: "stockpiles",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "goods");

            migrationBuilder.DropTable(
                name: "shops");

            migrationBuilder.DropTable(
                name: "stockpiles");
        }
    }
}
