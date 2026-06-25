using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMoneyLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "money_ledger_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Tick = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalSupply = table.Column<long>(type: "INTEGER", nullable: false),
                    NetDelta = table.Column<long>(type: "INTEGER", nullable: false),
                    Discrepancy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_money_ledger_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "money_ledger_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_money_ledger_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_money_ledger_lines_money_ledger_snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "money_ledger_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_money_ledger_lines_SnapshotId",
                table: "money_ledger_lines",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_money_ledger_snapshots_WorldId",
                table: "money_ledger_snapshots",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_money_ledger_snapshots_WorldId_Sequence",
                table: "money_ledger_snapshots",
                columns: new[] { "WorldId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "money_ledger_lines");

            migrationBuilder.DropTable(
                name: "money_ledger_snapshots");
        }
    }
}
