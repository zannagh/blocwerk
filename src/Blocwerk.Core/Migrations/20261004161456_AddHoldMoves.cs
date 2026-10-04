using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldMoves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MoveDistanceMm",
                table: "HoldGenerationLinks",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MoveOutcome",
                table: "HoldGenerationLinks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MoveRotationDeg",
                table: "HoldGenerationLinks",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MoveSource",
                table: "HoldGenerationLinks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BoulderHoldMoves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoulderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    NewHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Usage = table.Column<int>(type: "integer", nullable: false),
                    DistanceMm = table.Column<double>(type: "double precision", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    RotationDeg = table.Column<double>(type: "double precision", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    FromGeneration = table.Column<int>(type: "integer", nullable: false),
                    ToGeneration = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoulderHoldMoves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoulderHoldMoves_Boulders_BoulderId",
                        column: x => x.BoulderId,
                        principalTable: "Boulders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BoulderHoldMoves_Holds_NewHoldId",
                        column: x => x.NewHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BoulderHoldMoves_Holds_OldHoldId",
                        column: x => x.OldHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoulderHoldMoves_BoulderId",
                table: "BoulderHoldMoves",
                column: "BoulderId");

            migrationBuilder.CreateIndex(
                name: "IX_BoulderHoldMoves_NewHoldId",
                table: "BoulderHoldMoves",
                column: "NewHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_BoulderHoldMoves_OldHoldId",
                table: "BoulderHoldMoves",
                column: "OldHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_BoulderHoldMoves_WallId",
                table: "BoulderHoldMoves",
                column: "WallId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoulderHoldMoves");

            migrationBuilder.DropColumn(
                name: "MoveDistanceMm",
                table: "HoldGenerationLinks");

            migrationBuilder.DropColumn(
                name: "MoveOutcome",
                table: "HoldGenerationLinks");

            migrationBuilder.DropColumn(
                name: "MoveRotationDeg",
                table: "HoldGenerationLinks");

            migrationBuilder.DropColumn(
                name: "MoveSource",
                table: "HoldGenerationLinks");
        }
    }
}
