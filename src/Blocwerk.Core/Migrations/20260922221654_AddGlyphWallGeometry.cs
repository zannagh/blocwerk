using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGlyphWallGeometry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MarkerSegmentIndex",
                table: "WallSegments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MeasuredAngle",
                table: "WallSegments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MeasuredYaw",
                table: "WallSegments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GlyphsEnabled",
                table: "Walls",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MarkerSizeMm",
                table: "Walls",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AreaMm2",
                table: "Holds",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FacetId",
                table: "Holds",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FingerprintJson",
                table: "Holds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HeightMm",
                table: "Holds",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetricSource",
                table: "Holds",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutlineSource",
                table: "Holds",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlaneAMm",
                table: "Holds",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PlaneBMm",
                table: "Holds",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WidthMm",
                table: "Holds",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallGeometryModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    WidthMm = table.Column<double>(type: "double precision", nullable: true),
                    HeightMm = table.Column<double>(type: "double precision", nullable: true),
                    ReprojRmsPx = table.Column<double>(type: "double precision", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallGeometryModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallGeometryModels_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallMarkerObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallPanelId = table.Column<Guid>(type: "uuid", nullable: false),
                    PanelGeneration = table.Column<int>(type: "integer", nullable: false),
                    FromStagedPhoto = table.Column<bool>(type: "boolean", nullable: false),
                    MarkerId = table.Column<int>(type: "integer", nullable: false),
                    CornersJson = table.Column<string>(type: "text", nullable: false),
                    SidePx = table.Column<double>(type: "double precision", nullable: false),
                    Synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallMarkerObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallMarkerObservations_WallPanels_WallPanelId",
                        column: x => x.WallPanelId,
                        principalTable: "WallPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallGeometryModels_WallId_Active",
                table: "WallGeometryModels",
                column: "WallId",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_WallGeometryModels_WallId_CreatedAt",
                table: "WallGeometryModels",
                columns: new[] { "WallId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WallMarkerObservations_WallPanelId_PanelGeneration",
                table: "WallMarkerObservations",
                columns: new[] { "WallPanelId", "PanelGeneration" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallGeometryModels");

            migrationBuilder.DropTable(
                name: "WallMarkerObservations");

            migrationBuilder.DropColumn(
                name: "MarkerSegmentIndex",
                table: "WallSegments");

            migrationBuilder.DropColumn(
                name: "MeasuredAngle",
                table: "WallSegments");

            migrationBuilder.DropColumn(
                name: "MeasuredYaw",
                table: "WallSegments");

            migrationBuilder.DropColumn(
                name: "GlyphsEnabled",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "MarkerSizeMm",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "AreaMm2",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "FacetId",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "FingerprintJson",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "HeightMm",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "MetricSource",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "OutlineSource",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "PlaneAMm",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "PlaneBMm",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "WidthMm",
                table: "Holds");
        }
    }
}
