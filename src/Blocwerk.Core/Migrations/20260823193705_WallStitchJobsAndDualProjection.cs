using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class WallStitchJobsAndDualProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AngledMasterPath",
                table: "Walls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrthoMasterPath",
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

            migrationBuilder.AddColumn<int>(
                name: "PhotoProjection",
                table: "Walls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "PhotoVerticalScale",
                table: "Walls",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PhotoWallAngleDegrees",
                table: "Walls",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedAngledMasterPath",
                table: "Walls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedOrthoMasterPath",
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

            migrationBuilder.AddColumn<int>(
                name: "StagedPhotoProjection",
                table: "Walls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "StagedPhotoVerticalScale",
                table: "Walls",
                type: "double precision",
                nullable: true);

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
                name: "AngledMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "OrthoMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoAlternate",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoAlternateContentType",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoProjection",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoVerticalScale",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "PhotoWallAngleDegrees",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedAngledMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedOrthoMasterPath",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoAlternate",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoAlternateContentType",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoProjection",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoVerticalScale",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoWallAngleDegrees",
                table: "Walls");
        }
    }
}
