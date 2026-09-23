using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGeometryTextureMask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MaskSizeBytes",
                table: "WallGeometryTextures",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaskStoredPath",
                table: "WallGeometryTextures",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaskSizeBytes",
                table: "WallGeometryTextures");

            migrationBuilder.DropColumn(
                name: "MaskStoredPath",
                table: "WallGeometryTextures");
        }
    }
}
