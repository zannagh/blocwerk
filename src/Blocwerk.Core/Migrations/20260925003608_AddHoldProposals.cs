using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HoldProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    FacetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    A = table.Column<double>(type: "double precision", nullable: false),
                    B = table.Column<double>(type: "double precision", nullable: false),
                    H = table.Column<double>(type: "double precision", nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Y = table.Column<double>(type: "double precision", nullable: false),
                    Z = table.Column<double>(type: "double precision", nullable: false),
                    SizeMm = table.Column<double>(type: "double precision", nullable: false),
                    Views = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    BestPhoto = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BestPx = table.Column<double>(type: "double precision", nullable: false),
                    BestPy = table.Column<double>(type: "double precision", nullable: false),
                    BestRadiusPx = table.Column<double>(type: "double precision", nullable: false),
                    PanelId = table.Column<Guid>(type: "uuid", nullable: true),
                    PanelX = table.Column<double>(type: "double precision", nullable: true),
                    PanelY = table.Column<double>(type: "double precision", nullable: true),
                    PanelRadius = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HoldProposals_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HoldProposals_WallId_Status",
                table: "HoldProposals",
                columns: new[] { "WallId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HoldProposals");
        }
    }
}
