using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSplatUncleanedCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "UncleanedSizeBytes",
                table: "WallGeometrySplats",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UncleanedStoredPath",
                table: "WallGeometrySplats",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UncleanedSizeBytes",
                table: "WallGeometrySplats");

            migrationBuilder.DropColumn(
                name: "UncleanedStoredPath",
                table: "WallGeometrySplats");
        }
    }
}
