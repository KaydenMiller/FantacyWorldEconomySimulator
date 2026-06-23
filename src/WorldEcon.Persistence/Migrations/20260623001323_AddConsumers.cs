using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Need",
                table: "goods",
                type: "TEXT",
                nullable: false,
                defaultValue: "Essential");

            migrationBuilder.CreateTable(
                name: "consumers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seat = table.Column<Guid>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Budget = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consumers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_consumers_Seat",
                table: "consumers",
                column: "Seat");

            migrationBuilder.CreateIndex(
                name: "IX_consumers_WorldId",
                table: "consumers",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consumers");

            migrationBuilder.DropColumn(
                name: "Need",
                table: "goods");
        }
    }
}
