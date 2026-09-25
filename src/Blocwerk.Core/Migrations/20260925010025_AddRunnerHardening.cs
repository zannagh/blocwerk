using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRunnerHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GpuRunnerSharedOptIns");

            migrationBuilder.AddColumn<int>(
                name: "ShutdownCount",
                table: "GpuJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GpuRunnerApprovals",
                columns: table => new
                {
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuRunnerApprovals", x => new { x.WallId, x.RunnerId });
                    table.ForeignKey(
                        name: "FK_GpuRunnerApprovals_GpuRunners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "GpuRunners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GpuRunnerApprovals_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpuRunnerApprovals_RunnerId",
                table: "GpuRunnerApprovals",
                column: "RunnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GpuRunnerApprovals");

            migrationBuilder.DropColumn(
                name: "ShutdownCount",
                table: "GpuJobs");

            migrationBuilder.CreateTable(
                name: "GpuRunnerSharedOptIns",
                columns: table => new
                {
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OptedInByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuRunnerSharedOptIns", x => x.WallId);
                    table.ForeignKey(
                        name: "FK_GpuRunnerSharedOptIns_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }
    }
}
