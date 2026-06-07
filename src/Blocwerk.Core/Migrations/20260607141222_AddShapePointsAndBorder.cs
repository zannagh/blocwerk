using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddShapePointsAndBorder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BorderPoints",
                table: "Walls",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShapePoints",
                table: "Holds",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BorderPoints",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "ShapePoints",
                table: "Holds");
        }
    }
}
