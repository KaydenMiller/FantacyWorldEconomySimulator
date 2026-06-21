using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "caravans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitCostBasis = table.Column<long>(type: "INTEGER", nullable: false),
                    DepartTick = table.Column<long>(type: "INTEGER", nullable: false),
                    ArriveTick = table.Column<long>(type: "INTEGER", nullable: false),
                    Delivered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_caravans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "merchants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seat = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capital = table.Column<long>(type: "INTEGER", nullable: false),
                    CargoCapacity = table.Column<long>(type: "INTEGER", nullable: false),
                    Reach = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merchants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_caravans_ArriveTick",
                table: "caravans",
                column: "ArriveTick");

            migrationBuilder.CreateIndex(
                name: "IX_caravans_DestinationId",
                table: "caravans",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_caravans_OwnerId",
                table: "caravans",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_caravans_WorldId",
                table: "caravans",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_merchants_Seat",
                table: "merchants",
                column: "Seat");

            migrationBuilder.CreateIndex(
                name: "IX_merchants_WorldId",
                table: "merchants",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "caravans");

            migrationBuilder.DropTable(
                name: "merchants");
        }
    }
}
