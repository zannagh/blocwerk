using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuJobPreviewLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviewAcceptedAt",
                table: "GpuJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviewCount",
                table: "GpuJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviewAcceptedAt",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewCount",
                table: "GpuJobs");
        }
    }
}
