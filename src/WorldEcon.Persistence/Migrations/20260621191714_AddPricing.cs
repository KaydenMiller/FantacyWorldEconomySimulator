using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ElasticityExponent",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxPriceMultBp",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinPriceMultBp",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "MarketPrice",
                table: "stockpiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ElasticityExponent",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "MaxPriceMultBp",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "MinPriceMultBp",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "MarketPrice",
                table: "stockpiles");
        }
    }
}
