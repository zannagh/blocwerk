using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallUpdateSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WallUpdateSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    StagedGeneration = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Phase = table.Column<int>(type: "integer", nullable: false),
                    NeighbourIndex = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActiveByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallUpdateSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallUpdateSessions_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallUpdateHoldDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairedHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    CarryKind = table.Column<int>(type: "integer", nullable: false),
                    Discarded = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallUpdateHoldDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallUpdateHoldDecisions_Holds_HoldId",
                        column: x => x.HoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateHoldDecisions_Holds_PairedHoldId",
                        column: x => x.PairedHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateHoldDecisions_WallUpdateSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "WallUpdateSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallUpdateNeighbourDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PanelId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentreHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    Moved = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallUpdateNeighbourDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallUpdateNeighbourDecisions_Holds_CentreHoldId",
                        column: x => x.CentreHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateNeighbourDecisions_Holds_HoldId",
                        column: x => x.HoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateNeighbourDecisions_WallPanels_PanelId",
                        column: x => x.PanelId,
                        principalTable: "WallPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateNeighbourDecisions_WallUpdateSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "WallUpdateSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateHoldDecisions_HoldId",
                table: "WallUpdateHoldDecisions",
                column: "HoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateHoldDecisions_PairedHoldId",
                table: "WallUpdateHoldDecisions",
                column: "PairedHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateHoldDecisions_SessionId_Kind_HoldId",
                table: "WallUpdateHoldDecisions",
                columns: new[] { "SessionId", "Kind", "HoldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateNeighbourDecisions_CentreHoldId",
                table: "WallUpdateNeighbourDecisions",
                column: "CentreHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateNeighbourDecisions_HoldId",
                table: "WallUpdateNeighbourDecisions",
                column: "HoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateNeighbourDecisions_PanelId",
                table: "WallUpdateNeighbourDecisions",
                column: "PanelId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateNeighbourDecisions_SessionId_PanelId",
                table: "WallUpdateNeighbourDecisions",
                columns: new[] { "SessionId", "PanelId" });

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateSessions_CreatedAt",
                table: "WallUpdateSessions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateSessions_WallId_Status",
                table: "WallUpdateSessions",
                columns: new[] { "WallId", "Status" });

            // "At most one OPEN session per wall", enforced by the store rather than by a comment: the
            // stage path reads-then-writes without a transaction, so only a constraint can settle two
            // admins starting an update at the same moment. Partial (Status = 0 = Open) so the
            // Promoted/Discarded history rows stay unconstrained. Postgres and SQLite share this syntax.
            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateSessions_WallId_Open",
                table: "WallUpdateSessions",
                column: "WallId",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallUpdateHoldDecisions");

            migrationBuilder.DropTable(
                name: "WallUpdateNeighbourDecisions");

            migrationBuilder.DropTable(
                name: "WallUpdateSessions");
        }
    }
}
