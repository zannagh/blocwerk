using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCaptureVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "VideoDurationSeconds",
                table: "WallCaptures",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoFileName",
                table: "WallCaptures",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoFramesJson",
                table: "WallCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VideoSizeBytes",
                table: "WallCaptures",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoStoredPath",
                table: "WallCaptures",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VideoDurationSeconds",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "VideoFileName",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "VideoFramesJson",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "VideoSizeBytes",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "VideoStoredPath",
                table: "WallCaptures");
        }
    }
}
