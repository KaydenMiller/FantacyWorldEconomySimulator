using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhysicalGoods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Goods: add physical columns with a Medium-ish default, then backfill by size class.
            migrationBuilder.AddColumn<long>(name: "MassPerUnit", table: "goods", type: "INTEGER", nullable: false, defaultValue: 10000L);
            migrationBuilder.AddColumn<long>(name: "VolumePerUnit", table: "goods", type: "INTEGER", nullable: false, defaultValue: 20000L);
            migrationBuilder.Sql(
                "UPDATE goods SET MassPerUnit = CASE Size " +
                "WHEN 'Tiny' THEN 50 WHEN 'Small' THEN 1000 WHEN 'Medium' THEN 10000 WHEN 'Large' THEN 50000 WHEN 'Bulky' THEN 200000 ELSE 10000 END, " +
                "VolumePerUnit = CASE Size " +
                "WHEN 'Tiny' THEN 50 WHEN 'Small' THEN 1000 WHEN 'Medium' THEN 20000 WHEN 'Large' THEN 100000 WHEN 'Bulky' THEN 500000 ELSE 20000 END;");

            // Merchants: add weight/volume capacity, backfill from the old unit cap, THEN drop it.
            migrationBuilder.AddColumn<long>(name: "WeightCapacity", table: "merchants", type: "INTEGER", nullable: false, defaultValue: 600000L);
            migrationBuilder.AddColumn<long>(name: "VolumeCapacity", table: "merchants", type: "INTEGER", nullable: false, defaultValue: 1000000L);
            migrationBuilder.Sql("UPDATE merchants SET WeightCapacity = CargoCapacity * 10000, VolumeCapacity = CargoCapacity * 20000;");
            migrationBuilder.DropColumn(name: "CargoCapacity", table: "merchants");

            // Worlds: transport tuning + display units.
            migrationBuilder.AddColumn<long>(name: "VolumetricDivisor", table: "worlds", type: "INTEGER", nullable: false, defaultValue: 5000L);
            migrationBuilder.AddColumn<long>(name: "TransportRate", table: "worlds", type: "INTEGER", nullable: false, defaultValue: 1L);
            migrationBuilder.AddColumn<string>(name: "DisplayUnitSystem", table: "worlds", type: "TEXT", nullable: false, defaultValue: "Metric");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayUnitSystem",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "TransportRate",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "VolumetricDivisor",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "VolumeCapacity",
                table: "merchants");

            migrationBuilder.DropColumn(
                name: "WeightCapacity",
                table: "merchants");

            migrationBuilder.AddColumn<long>(
                name: "CargoCapacity",
                table: "merchants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.DropColumn(
                name: "MassPerUnit",
                table: "goods");

            migrationBuilder.DropColumn(
                name: "VolumePerUnit",
                table: "goods");
        }
    }
}
