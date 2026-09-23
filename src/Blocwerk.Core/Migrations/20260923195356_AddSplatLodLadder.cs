using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSplatLodLadder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LodLevelsJson",
                table: "WallGeometrySplats",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SplatCount",
                table: "WallGeometrySplats",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LodLevelsJson",
                table: "WallGeometrySplats");

            migrationBuilder.DropColumn(
                name: "SplatCount",
                table: "WallGeometrySplats");
        }
    }
}
