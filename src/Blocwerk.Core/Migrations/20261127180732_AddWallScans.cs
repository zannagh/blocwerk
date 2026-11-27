using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveScanCaptureId",
                table: "Walls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdateMode",
                table: "Walls",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "WallScans",
                columns: table => new
                {
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    DeviceModel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PointCloudPath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PointCloudBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallScans", x => x.CaptureId);
                    table.ForeignKey(
                        name: "FK_WallScans_WallCaptures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "WallCaptures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallScans");

            migrationBuilder.DropColumn(
                name: "ActiveScanCaptureId",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "UpdateMode",
                table: "Walls");
        }
    }
}
