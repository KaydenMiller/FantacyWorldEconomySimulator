using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorldEcon.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "worlds",
                type: "TEXT",
                nullable: false,
                defaultValue: "{\"Denominations\":[{\"Name\":\"Copper\",\"Symbol\":\"c\",\"Units\":1},{\"Name\":\"Silver\",\"Symbol\":\"s\",\"Units\":10},{\"Name\":\"Gold\",\"Symbol\":\"g\",\"Units\":100},{\"Name\":\"Platinum\",\"Symbol\":\"p\",\"Units\":1000}]}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "worlds");
        }
    }
}
