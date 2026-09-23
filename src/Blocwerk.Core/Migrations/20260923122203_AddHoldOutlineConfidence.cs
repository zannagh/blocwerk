using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldOutlineConfidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShapeDone",
                table: "WallUpdateSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ShapeError",
                table: "WallUpdateSessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShapeFinishedAt",
                table: "WallUpdateSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShapeOverwriteManual",
                table: "WallUpdateSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ShapeScope",
                table: "WallUpdateSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShapeSkippedManual",
                table: "WallUpdateSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShapeStartedAt",
                table: "WallUpdateSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShapeStatus",
                table: "WallUpdateSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShapeTotal",
                table: "WallUpdateSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "OutlineConfidence",
                table: "Holds",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallUpdateShapeProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    PanelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    AnchorX = table.Column<double>(type: "double precision", nullable: false),
                    AnchorY = table.Column<double>(type: "double precision", nullable: false),
                    ImageWidth = table.Column<int>(type: "integer", nullable: false),
                    ImageHeight = table.Column<int>(type: "integer", nullable: false),
                    ShapeJson = table.Column<string>(type: "text", nullable: true),
                    HolesJson = table.Column<string>(type: "text", nullable: true),
                    PreviousShapeJson = table.Column<string>(type: "text", nullable: true),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    AdjustedShapeJson = table.Column<string>(type: "text", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallUpdateShapeProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallUpdateShapeProposals_Holds_HoldId",
                        column: x => x.HoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateShapeProposals_WallUpdateSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "WallUpdateSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateShapeProposals_HoldId",
                table: "WallUpdateShapeProposals",
                column: "HoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateShapeProposals_SessionId_HoldId",
                table: "WallUpdateShapeProposals",
                columns: new[] { "SessionId", "HoldId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallUpdateShapeProposals");

            migrationBuilder.DropColumn(
                name: "ShapeDone",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeError",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeFinishedAt",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeOverwriteManual",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeScope",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeSkippedManual",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeStartedAt",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeStatus",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "ShapeTotal",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "OutlineConfidence",
                table: "Holds");
        }
    }
}
