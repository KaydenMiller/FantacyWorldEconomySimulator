using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeographyV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "settlements",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "CountryId",
                table: "regions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "regions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "region_containments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentRegionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChildRegionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_region_containments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "region_continents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RegionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContinentId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_region_continents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "territorial_claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CountryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_territorial_claims", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_region_containments_ChildRegionId",
                table: "region_containments",
                column: "ChildRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_region_containments_ParentRegionId",
                table: "region_containments",
                column: "ParentRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_region_containments_WorldId",
                table: "region_containments",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_region_continents_ContinentId",
                table: "region_continents",
                column: "ContinentId");

            migrationBuilder.CreateIndex(
                name: "IX_region_continents_RegionId",
                table: "region_continents",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_region_continents_WorldId",
                table: "region_continents",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_territorial_claims_CountryId",
                table: "territorial_claims",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_territorial_claims_TargetKind_TargetId",
                table: "territorial_claims",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_territorial_claims_WorldId",
                table: "territorial_claims",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "region_containments");

            migrationBuilder.DropTable(
                name: "region_continents");

            migrationBuilder.DropTable(
                name: "territorial_claims");

            migrationBuilder.DropColumn(
                name: "State",
                table: "settlements");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "regions");

            migrationBuilder.AlterColumn<Guid>(
                name: "CountryId",
                table: "regions",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
