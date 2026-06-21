using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Facility = table.Column<string>(type: "TEXT", nullable: false),
                    ThroughputCap = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Facility = table.Column<string>(type: "TEXT", nullable: false),
                    Lines = table.Column<string>(type: "TEXT", nullable: false),
                    LaborCost = table.Column<long>(type: "INTEGER", nullable: false),
                    TicksToProduce = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "resource_endowments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GoodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Abundance = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_endowments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "work_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductionNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartTick = table.Column<long>(type: "INTEGER", nullable: false),
                    CompleteTick = table.Column<long>(type: "INTEGER", nullable: false),
                    CommittedInputCost = table.Column<long>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_orders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_RecipeId",
                table: "production_nodes",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_SettlementId",
                table: "production_nodes",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_production_nodes_WorldId",
                table: "production_nodes",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_recipes_WorldId",
                table: "recipes",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_resource_endowments_SettlementId",
                table: "resource_endowments",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_resource_endowments_WorldId",
                table: "resource_endowments",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_CompleteTick",
                table: "work_orders",
                column: "CompleteTick");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_ProductionNodeId",
                table: "work_orders",
                column: "ProductionNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_WorldId",
                table: "work_orders",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_nodes");

            migrationBuilder.DropTable(
                name: "recipes");

            migrationBuilder.DropTable(
                name: "resource_endowments");

            migrationBuilder.DropTable(
                name: "work_orders");
        }
    }
}
