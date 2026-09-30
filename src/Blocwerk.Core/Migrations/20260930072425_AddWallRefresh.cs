using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WallRefreshes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaptureStarted = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    VideosJson = table.Column<string>(type: "text", nullable: true),
                    PanelPicksJson = table.Column<string>(type: "text", nullable: true),
                    StepsJson = table.Column<string>(type: "text", nullable: true),
                    SummaryJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallRefreshes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallRefreshes_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallRefreshes_WallId_Open",
                table: "WallRefreshes",
                column: "WallId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2, 3, 4, 5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallRefreshes");
        }
    }
}
