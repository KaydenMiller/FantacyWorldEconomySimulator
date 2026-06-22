using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLogEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "log_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredTick = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Magnitude = table.Column<string>(type: "TEXT", nullable: false),
                    OriginKind = table.Column<string>(type: "TEXT", nullable: false),
                    OriginId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsPlayerAction = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "log_event_scopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LogEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeKind = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log_event_scopes", x => x.Id);
                });

            // Fold the legacy DM/party audit log into the unified LogEvent stream. Historical actions
            // become Major, player-action events at World scope (precise per-settlement scope for old
            // actions is not reconstructable from ArgsJson; new actions get proper scoping going forward).
            migrationBuilder.Sql(@"
INSERT INTO log_events (Id, WorldId, Sequence, OccurredTick, Type, Magnitude, OriginKind, OriginId, IsPlayerAction, PayloadJson, Message, RecordedAtUtc)
SELECT Id, WorldId, Sequence, AppliedTick, 'PartyAction', 'Major', 'World', WorldId, 1, ArgsJson, Description, RecordedAtUtc
FROM dm_actions;");

            migrationBuilder.Sql(@"
INSERT INTO log_event_scopes (Id, WorldId, LogEventId, ScopeKind, ScopeId, Sequence)
SELECT
  lower(
    substr(hex(randomblob(4)),1,8) || '-' ||
    substr(hex(randomblob(2)),1,4) || '-4' ||
    substr(hex(randomblob(2)),2,3) || '-' ||
    substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)),2,3) || '-' ||
    substr(hex(randomblob(6)),1,12)
  ),
  WorldId, Id, 'World', WorldId, Sequence
FROM dm_actions;");

            migrationBuilder.DropTable(
                name: "dm_actions");

            migrationBuilder.CreateIndex(
                name: "IX_log_event_scopes_LogEventId",
                table: "log_event_scopes",
                column: "LogEventId");

            migrationBuilder.CreateIndex(
                name: "IX_log_event_scopes_ScopeKind_ScopeId_Sequence",
                table: "log_event_scopes",
                columns: new[] { "ScopeKind", "ScopeId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_log_events_WorldId",
                table: "log_events",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_log_events_WorldId_Sequence",
                table: "log_events",
                columns: new[] { "WorldId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "log_event_scopes");

            migrationBuilder.DropTable(
                name: "log_events");

            migrationBuilder.CreateTable(
                name: "dm_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppliedTick = table.Column<long>(type: "INTEGER", nullable: false),
                    ArgsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    WorldId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dm_actions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dm_actions_WorldId",
                table: "dm_actions",
                column: "WorldId");

            migrationBuilder.CreateIndex(
                name: "IX_dm_actions_WorldId_Sequence",
                table: "dm_actions",
                columns: new[] { "WorldId", "Sequence" });
        }
    }
}
