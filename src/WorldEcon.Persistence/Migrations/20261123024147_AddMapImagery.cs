using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMapImagery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "map_imagery",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_map_imagery", x => x.Id);
                    table.CheckConstraint("CK_map_imagery_Id", "\"Id\" IN (0, 1)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "map_imagery");
        }
    }
}
