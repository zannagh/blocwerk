using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuRunnerFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GpuRunnerFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuRunnerFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GpuRunnerFailures_GpuJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "GpuJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GpuRunnerFailures_GpuRunners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "GpuRunners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpuRunnerFailures_JobId",
                table: "GpuRunnerFailures",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuRunnerFailures_RunnerId_At",
                table: "GpuRunnerFailures",
                columns: new[] { "RunnerId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GpuRunnerFailures");
        }
    }
}
