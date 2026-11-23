using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_routes_ToSettlementId",
                table: "routes");

            migrationBuilder.RenameColumn(
                name: "ToSettlementId",
                table: "routes",
                newName: "Surface");

            migrationBuilder.RenameColumn(
                name: "Terrain",
                table: "routes",
                newName: "Provenance");

            migrationBuilder.RenameColumn(
                name: "FromSettlementId",
                table: "routes",
                newName: "NodeB");

            migrationBuilder.RenameColumn(
                name: "Distance",
                table: "routes",
                newName: "TrafficCount");

            migrationBuilder.RenameIndex(
                name: "IX_routes_FromSettlementId",
                table: "routes",
                newName: "IX_routes_NodeB");

            migrationBuilder.AddColumn<long>(
                name: "DirtRoadSpeedMpd",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "KnowledgeRadiusTicks",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "MaxExplorePct",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxLossMarginPct",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "OpenWaterSpeedMpd",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PavedSpeedMpd",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SkySpeedMpd",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TrackSpeedMpd",
                table: "worlds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Difficulty",
                table: "routes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "LengthMetres",
                table: "routes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "MaxTier",
                table: "routes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinTier",
                table: "routes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "NodeA",
                table: "routes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "SpeedFactorAtoB",
                table: "routes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SpeedFactorBtoA",
                table: "routes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RiskAversion",
                table: "merchants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VehicleClassName",
                table: "merchants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "HomeReported",
                table: "caravans",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VehicleClassName",
                table: "caravans",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "caravan_legs",
                columns: table => new
                {
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    CaravanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    EnterTick = table.Column<long>(type: "INTEGER", nullable: false),
                    ExitTick = table.Column<long>(type: "INTEGER", nullable: false),
                    LengthMetres = table.Column<long>(type: "INTEGER", nullable: false),
                    OffsetMetres = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_caravan_legs", x => new { x.CaravanId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_caravan_legs_caravans_CaravanId",
                        column: x => x.CaravanId,
                        principalTable: "caravans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merchant_trips",
                columns: table => new
                {
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    MerchantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    From = table.Column<Guid>(type: "TEXT", nullable: false),
                    To = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleClass = table.Column<string>(type: "TEXT", nullable: false),
                    Ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    LengthMetres = table.Column<long>(type: "INTEGER", nullable: false),
                    ArriveTick = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merchant_trips", x => new { x.MerchantId, x.Slot });
                    table.ForeignKey(
                        name: "FK_merchant_trips_merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "route_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    TileId = table.Column<int>(type: "INTEGER", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    PortKind = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "route_tiles",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    TileId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_tiles", x => new { x.RouteId, x.Sequence });
                });

            migrationBuilder.CreateTable(
                name: "route_upgrades",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tick = table.Column<long>(type: "INTEGER", nullable: false),
                    FromSurface = table.Column<string>(type: "TEXT", nullable: false),
                    ToSurface = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_upgrades", x => new { x.RouteId, x.Tick });
                });

            migrationBuilder.CreateTable(
                name: "travel_knowledge",
                columns: table => new
                {
                    KnowerSettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromSettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToSettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleClass = table.Column<string>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    LengthMetres = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedTick = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_travel_knowledge", x => new { x.KnowerSettlementId, x.FromSettlementId, x.ToSettlementId, x.VehicleClass });
                });

            migrationBuilder.CreateTable(
                name: "vehicle_classes",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Tier = table.Column<int>(type: "INTEGER", nullable: false),
                    SpeedFactor = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxMassGrams = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxVolumeCm3 = table.Column<long>(type: "INTEGER", nullable: false),
                    VolumetricDivisor = table.Column<long>(type: "INTEGER", nullable: false),
                    RatePct = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vehicle_classes", x => new { x.WorldId, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "voyage_paths",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tiles = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_voyage_paths", x => x.RouteId);
                });

            migrationBuilder.CreateTable(
                name: "road_routes",
                columns: table => new
                {
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    RoadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_road_routes", x => new { x.RoadId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_road_routes_roads_RoadId",
                        column: x => x.RoadId,
                        principalTable: "roads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_routes_NodeA",
                table: "routes",
                column: "NodeA");

            migrationBuilder.CreateIndex(
                name: "IX_caravan_legs_RouteId",
                table: "caravan_legs",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_road_routes_RouteId",
                table: "road_routes",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_roads_WorldId",
                table: "roads",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_route_nodes_SettlementId",
                table: "route_nodes",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_route_nodes_WorldId_TileId",
                table: "route_nodes",
                columns: new[] { "WorldId", "TileId" });

            migrationBuilder.CreateIndex(
                name: "IX_route_tiles_TileId",
                table: "route_tiles",
                column: "TileId");

            migrationBuilder.CreateIndex(
                name: "IX_travel_knowledge_WorldId",
                table: "travel_knowledge",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "caravan_legs");

            migrationBuilder.DropTable(
                name: "merchant_trips");

            migrationBuilder.DropTable(
                name: "road_routes");

            migrationBuilder.DropTable(
                name: "route_nodes");

            migrationBuilder.DropTable(
                name: "route_tiles");

            migrationBuilder.DropTable(
                name: "route_upgrades");

            migrationBuilder.DropTable(
                name: "travel_knowledge");

            migrationBuilder.DropTable(
                name: "vehicle_classes");

            migrationBuilder.DropTable(
                name: "voyage_paths");

            migrationBuilder.DropTable(
                name: "roads");

            migrationBuilder.DropIndex(
                name: "IX_routes_NodeA",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "DirtRoadSpeedMpd",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "KnowledgeRadiusTicks",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "MaxExplorePct",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "MaxLossMarginPct",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "OpenWaterSpeedMpd",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "PavedSpeedMpd",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "SkySpeedMpd",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "TrackSpeedMpd",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "Difficulty",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "LengthMetres",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "MaxTier",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "MinTier",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "NodeA",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "SpeedFactorAtoB",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "SpeedFactorBtoA",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "RiskAversion",
                table: "merchants");

            migrationBuilder.DropColumn(
                name: "VehicleClassName",
                table: "merchants");

            migrationBuilder.DropColumn(
                name: "HomeReported",
                table: "caravans");

            migrationBuilder.DropColumn(
                name: "VehicleClassName",
                table: "caravans");

            migrationBuilder.RenameColumn(
                name: "TrafficCount",
                table: "routes",
                newName: "Distance");

            migrationBuilder.RenameColumn(
                name: "Surface",
                table: "routes",
                newName: "ToSettlementId");

            migrationBuilder.RenameColumn(
                name: "Provenance",
                table: "routes",
                newName: "Terrain");

            migrationBuilder.RenameColumn(
                name: "NodeB",
                table: "routes",
                newName: "FromSettlementId");

            migrationBuilder.RenameIndex(
                name: "IX_routes_NodeB",
                table: "routes",
                newName: "IX_routes_FromSettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_routes_ToSettlementId",
                table: "routes",
                column: "ToSettlementId");
        }
    }
}
