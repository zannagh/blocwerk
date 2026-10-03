using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallVolumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VolumePlacementJson",
                table: "Holds",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallVolumes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    FacetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    FootprintJson = table.Column<string>(type: "text", nullable: false),
                    SurfaceJson = table.Column<string>(type: "text", nullable: false),
                    AreaM2 = table.Column<double>(type: "double precision", nullable: false),
                    HeightMm = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    HoldCount = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallVolumes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallVolumes_WallGeometryModels_GeometryModelId",
                        column: x => x.GeometryModelId,
                        principalTable: "WallGeometryModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallVolumes_GeometryModelId_Index",
                table: "WallVolumes",
                columns: new[] { "GeometryModelId", "Index" });

            migrationBuilder.CreateIndex(
                name: "IX_WallVolumes_WallId",
                table: "WallVolumes",
                column: "WallId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallVolumes");

            migrationBuilder.DropColumn(
                name: "VolumePlacementJson",
                table: "Holds");
        }
    }
}
