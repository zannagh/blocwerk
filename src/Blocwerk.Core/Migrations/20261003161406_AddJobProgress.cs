using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJobProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FollowUpRunningSince",
                table: "WallCaptures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimelineJson",
                table: "WallCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "WallCaptures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Loss",
                table: "GpuJobs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SplatCount",
                table: "GpuJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Step",
                table: "GpuJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StepAnchor",
                table: "GpuJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StepAnchorAt",
                table: "GpuJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WallCaptures_CompletedAt",
                table: "WallCaptures",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WallCaptures_FollowUpRunningSince",
                table: "WallCaptures",
                column: "FollowUpRunningSince");

            migrationBuilder.CreateIndex(
                name: "IX_WallCaptures_UpdatedAt",
                table: "WallCaptures",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GpuJobs_CompletedAt",
                table: "GpuJobs",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WallCaptures_CompletedAt",
                table: "WallCaptures");

            migrationBuilder.DropIndex(
                name: "IX_WallCaptures_FollowUpRunningSince",
                table: "WallCaptures");

            migrationBuilder.DropIndex(
                name: "IX_WallCaptures_UpdatedAt",
                table: "WallCaptures");

            migrationBuilder.DropIndex(
                name: "IX_GpuJobs_CompletedAt",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "FollowUpRunningSince",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "TimelineJson",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "Loss",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "SplatCount",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "Step",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "StepAnchor",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "StepAnchorAt",
                table: "GpuJobs");
        }
    }
}
