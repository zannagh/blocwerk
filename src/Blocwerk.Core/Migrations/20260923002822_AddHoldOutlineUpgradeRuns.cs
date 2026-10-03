using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldOutlineUpgradeRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HoldOutlineUpgradeRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncludedManual = table.Column<bool>(type: "boolean", nullable: false),
                    HoldIdsJson = table.Column<string>(type: "text", nullable: false),
                    EligibleCount = table.Column<int>(type: "integer", nullable: false),
                    OutlinedCount = table.Column<int>(type: "integer", nullable: false),
                    KeptCircleCount = table.Column<int>(type: "integer", nullable: false),
                    WithHolesCount = table.Column<int>(type: "integer", nullable: false),
                    FingerprintedCount = table.Column<int>(type: "integer", nullable: false),
                    MeasuredCount = table.Column<int>(type: "integer", nullable: false),
                    RevertedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevertedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldOutlineUpgradeRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HoldOutlineUpgradeRuns_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HoldOutlineUpgradeRuns_WallId_CreatedAt",
                table: "HoldOutlineUpgradeRuns",
                columns: new[] { "WallId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HoldOutlineUpgradeRuns");
        }
    }
}
