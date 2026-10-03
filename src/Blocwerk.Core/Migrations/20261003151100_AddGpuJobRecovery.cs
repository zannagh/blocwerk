using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuJobRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CheckpointStep",
                table: "GpuJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimToken",
                table: "GpuJobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailedRunnerIdsJson",
                table: "GpuJobs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeartbeatAt",
                table: "GpuJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PauseCount",
                table: "GpuJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReattachCount",
                table: "GpuJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointStep",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "FailedRunnerIdsJson",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PauseCount",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "ReattachCount",
                table: "GpuJobs");
        }
    }
}
