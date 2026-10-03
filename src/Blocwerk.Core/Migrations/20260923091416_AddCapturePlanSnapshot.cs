using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCapturePlanSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlacementCheckJson",
                table: "WallCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanJson",
                table: "WallCaptures",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlacementCheckJson",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "PlanJson",
                table: "WallCaptures");
        }
    }
}
