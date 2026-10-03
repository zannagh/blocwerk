using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGeometryTextureSourceMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SourceMapSizeBytes",
                table: "WallGeometryTextures",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceMapStoredPath",
                table: "WallGeometryTextures",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceMapSizeBytes",
                table: "WallGeometryTextures");

            migrationBuilder.DropColumn(
                name: "SourceMapStoredPath",
                table: "WallGeometryTextures");
        }
    }
}
