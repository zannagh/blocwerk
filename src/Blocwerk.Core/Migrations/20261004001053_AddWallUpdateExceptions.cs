using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallUpdateExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SummaryRequestedAt",
                table: "WallRefreshes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallUpdateExceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OldHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    StagedHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    PanelId = table.Column<Guid>(type: "uuid", nullable: true),
                    X = table.Column<double>(type: "double precision", nullable: true),
                    Y = table.Column<double>(type: "double precision", nullable: true),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: true),
                    FacetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    A = table.Column<double>(type: "double precision", nullable: true),
                    B = table.Column<double>(type: "double precision", nullable: true),
                    PhotoScore = table.Column<double>(type: "double precision", nullable: true),
                    TextureScore = table.Column<double>(type: "double precision", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallUpdateExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallUpdateExceptions_Holds_OldHoldId",
                        column: x => x.OldHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateExceptions_Holds_StagedHoldId",
                        column: x => x.StagedHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WallUpdateExceptions_WallUpdateSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "WallUpdateSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateExceptions_OldHoldId",
                table: "WallUpdateExceptions",
                column: "OldHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateExceptions_SessionId_Kind",
                table: "WallUpdateExceptions",
                columns: new[] { "SessionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_WallUpdateExceptions_StagedHoldId",
                table: "WallUpdateExceptions",
                column: "StagedHoldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallUpdateExceptions");

            migrationBuilder.DropColumn(
                name: "SummaryRequestedAt",
                table: "WallRefreshes");
        }
    }
}
