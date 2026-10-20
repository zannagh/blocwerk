using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallEnhancement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WallEnhancementRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    PanelGenerationsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentStep = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StepsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StepsFinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalizedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutcomeJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallEnhancementRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallEnhancementRuns_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallEnhancementItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    OtherHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallEnhancementItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallEnhancementItems_WallEnhancementRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "WallEnhancementRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallEnhancementItems_RunId_Kind_Decision",
                table: "WallEnhancementItems",
                columns: new[] { "RunId", "Kind", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_WallEnhancementRuns_WallId_Generation",
                table: "WallEnhancementRuns",
                columns: new[] { "WallId", "Generation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallEnhancementItems");

            migrationBuilder.DropTable(
                name: "WallEnhancementRuns");
        }
    }
}
