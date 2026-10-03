using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSplatMobileLod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MobileSizeBytes",
                table: "WallGeometrySplats",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MobileStoredPath",
                table: "WallGeometrySplats",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MobileSizeBytes",
                table: "WallGeometrySplats");

            migrationBuilder.DropColumn(
                name: "MobileStoredPath",
                table: "WallGeometrySplats");
        }
    }
}
