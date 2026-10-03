using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldRelocationProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RelocationsProposedAt",
                table: "WallUpdateSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallUpdateRelocationProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldHoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    NewHoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Margin = table.Column<double>(type: "double precision", nullable: false),
                    Metric = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallUpdateRelocationProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallUpdateRelocationProposals_Holds_NewHoldId",
                        column: x => x.NewHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateRelocationProposals_Holds_OldHoldId",
                        column: x => x.OldHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateRelocationProposals_WallUpdateSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "WallUpdateSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateRelocationProposals_NewHoldId",
                table: "WallUpdateRelocationProposals",
                column: "NewHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateRelocationProposals_OldHoldId",
                table: "WallUpdateRelocationProposals",
                column: "OldHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateRelocationProposals_SessionId_NewHoldId",
                table: "WallUpdateRelocationProposals",
                columns: new[] { "SessionId", "NewHoldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateRelocationProposals_SessionId_OldHoldId",
                table: "WallUpdateRelocationProposals",
                columns: new[] { "SessionId", "OldHoldId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallUpdateRelocationProposals");

            migrationBuilder.DropColumn(
                name: "RelocationsProposedAt",
                table: "WallUpdateSessions");
        }
    }
}
