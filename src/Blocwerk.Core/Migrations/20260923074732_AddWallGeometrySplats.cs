using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallGeometrySplats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SplatJobId",
                table: "WallCaptures",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallGeometrySplats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FrameJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallGeometrySplats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallGeometrySplats_WallGeometryModels_GeometryModelId",
                        column: x => x.GeometryModelId,
                        principalTable: "WallGeometryModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallGeometrySplats_GeometryModelId",
                table: "WallGeometrySplats",
                column: "GeometryModelId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallGeometrySplats");

            migrationBuilder.DropColumn(
                name: "SplatJobId",
                table: "WallCaptures");
        }
    }
}
