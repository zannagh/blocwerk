using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscardedDetectionFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscardReason",
                table: "WallLookOverFindings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Overlap",
                table: "WallLookOverFindings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShapeHoles",
                table: "WallLookOverFindings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShapePoints",
                table: "WallLookOverFindings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscardReason",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "Overlap",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "ShapeHoles",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "ShapePoints",
                table: "WallLookOverFindings");
        }
    }
}
