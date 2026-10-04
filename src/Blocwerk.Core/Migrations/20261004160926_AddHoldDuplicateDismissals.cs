using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldDuplicateDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HoldDuplicateDismissals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldAId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldBId = table.Column<Guid>(type: "uuid", nullable: false),
                    DismissedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldDuplicateDismissals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HoldDuplicateDismissals_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HoldDuplicateDismissals_WallId_HoldAId_HoldBId",
                table: "HoldDuplicateDismissals",
                columns: new[] { "WallId", "HoldAId", "HoldBId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HoldDuplicateDismissals");
        }
    }
}
