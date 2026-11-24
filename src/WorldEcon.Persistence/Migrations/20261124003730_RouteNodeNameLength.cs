using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RouteNodeNameLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_route_nodes_name_length",
                table: "route_nodes",
                sql: "\"Kind\" = 'Settlement' OR \"Name\" IS NULL OR length(\"Name\") <= 64");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_route_nodes_name_length",
                table: "route_nodes");
        }
    }
}
