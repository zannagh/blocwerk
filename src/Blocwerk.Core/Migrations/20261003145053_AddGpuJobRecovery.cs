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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointStep",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "FailedRunnerIdsJson",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                table: "GpuJobs");
        }
    }
}
