using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuRunners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GpuRunners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedWithOtherWalls = table.Column<bool>(type: "boolean", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastJobAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GpuName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VramMb = table.Column<int>(type: "integer", nullable: true),
                    MaxQuality = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MemoryBudgetMb = table.Column<int>(type: "integer", nullable: true),
                    Trainer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RunnerVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Platform = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuRunners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GpuRunners_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GpuRunnerSharedOptIns",
                columns: table => new
                {
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptedInByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "GpuJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quality = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ClaimedByRunnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    Stage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    LostLeaseCount = table.Column<int>(type: "integer", nullable: false),
                    BundlePath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BundleBytes = table.Column<long>(type: "bigint", nullable: false),
                    BundleSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreparedPath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResultPath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResultBytes = table.Column<long>(type: "bigint", nullable: true),
                    ResultFormat = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ResultStatsJson = table.Column<string>(type: "text", nullable: true),
                    FinishJobId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InstalledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GpuJobs_GpuRunners_ClaimedByRunnerId",
                        column: x => x.ClaimedByRunnerId,
                        principalTable: "GpuRunners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GpuJobs_WallCaptures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "WallCaptures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GpuJobs_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GpuRunnerWalls",
                columns: table => new
                {
                    RunnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuRunnerWalls", x => new { x.RunnerId, x.WallId });
                    table.ForeignKey(
                        name: "FK_GpuRunnerWalls_GpuRunners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "GpuRunners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GpuRunnerWalls_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpuJobs_CaptureId",
                table: "GpuJobs",
                column: "CaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuJobs_ClaimedByRunnerId",
                table: "GpuJobs",
                column: "ClaimedByRunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuJobs_Status_CreatedAt",
                table: "GpuJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuJobs_WallId",
                table: "GpuJobs",
                column: "WallId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuRunners_KeyHash",
                table: "GpuRunners",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpuRunners_OwnerUserId",
                table: "GpuRunners",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuRunnerWalls_WallId",
                table: "GpuRunnerWalls",
                column: "WallId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GpuJobs");

            migrationBuilder.DropTable(
                name: "GpuRunnerSharedOptIns");

            migrationBuilder.DropTable(
                name: "GpuRunnerWalls");

            migrationBuilder.DropTable(
                name: "GpuRunners");
        }
    }
}
