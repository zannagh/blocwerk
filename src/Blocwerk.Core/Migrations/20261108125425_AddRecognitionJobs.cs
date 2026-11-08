using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRecognitionJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecognitionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Lane = table.Column<int>(type: "integer", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    InputPath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    InputBytes = table.Column<long>(type: "bigint", nullable: false),
                    InputSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequiredMemoryMb = table.Column<int>(type: "integer", nullable: true),
                    LeaseSeconds = table.Column<int>(type: "integer", nullable: false),
                    ClaimedByRunnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailedRunnerIdsJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    Stage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CallerKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecognitionJobs_GpuRunners_ClaimedByRunnerId",
                        column: x => x.ClaimedByRunnerId,
                        principalTable: "GpuRunners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecognitionJobs_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionJobs_CallerKey",
                table: "RecognitionJobs",
                column: "CallerKey");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionJobs_ClaimedByRunnerId",
                table: "RecognitionJobs",
                column: "ClaimedByRunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionJobs_Status_Lane_CreatedAt",
                table: "RecognitionJobs",
                columns: new[] { "Status", "Lane", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionJobs_WallId",
                table: "RecognitionJobs",
                column: "WallId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecognitionJobs");
        }
    }
}
