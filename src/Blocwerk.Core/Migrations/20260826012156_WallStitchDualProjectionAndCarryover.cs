using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class WallStitchDualProjectionAndCarryover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CamerasJson",
                table: "Walls",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FlatMasterPath",
                table: "Walls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NaturalMasterPath",
                table: "Walls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PhotoAlternate",
                table: "Walls",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoAlternateContentType",
                table: "Walls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PhotoCurvature",
                table: "Walls",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhotoProjection",
                table: "Walls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "PhotoWallAngleDegrees",
                table: "Walls",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedCamerasJson",
                table: "Walls",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedFlatMasterPath",
                table: "Walls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedNaturalMasterPath",
                table: "Walls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "StagedPhotoAlternate",
                table: "Walls",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedPhotoAlternateContentType",
                table: "Walls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StagedPhotoCurvature",
                table: "Walls",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StagedPhotoProjection",
                table: "Walls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "StagedPhotoWallAngleDegrees",
                table: "Walls",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallStitchJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SidecarJobId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    RequestedProjection = table.Column<int>(type: "integer", nullable: false),
                    WallAngleDegrees = table.Column<double>(type: "double precision", nullable: false),
                    TransferHolds = table.Column<bool>(type: "boolean", nullable: false),
                    PhotoCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DiagnosticsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallStitchJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallStitchJobs_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallStitchJobs_WallId_CreatedAt",
                table: "WallStitchJobs",
                columns: new[] { "WallId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallStitchJobs");

            migrationBuilder.DropColumn(
                name: "CamerasJson",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "FlatMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "NaturalMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoAlternate",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoAlternateContentType",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoCurvature",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoProjection",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoWallAngleDegrees",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedCamerasJson",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedFlatMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedNaturalMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoAlternate",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoAlternateContentType",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoCurvature",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoProjection",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoWallAngleDegrees",
                table: "Walls");
        }
    }
}
