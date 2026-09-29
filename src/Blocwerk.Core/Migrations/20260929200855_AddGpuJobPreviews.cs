using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuJobPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstalledPreviewPath",
                table: "GpuJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviewBaseSplatId",
                table: "GpuJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PreviewBytes",
                table: "GpuJobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewFinishJobId",
                table: "GpuJobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewFormat",
                table: "GpuJobs",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviewInstalledStep",
                table: "GpuJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewPath",
                table: "GpuJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviewStep",
                table: "GpuJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefinishStateJson",
                table: "GpuJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalSteps",
                table: "GpuJobs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InstalledPreviewPath",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewBaseSplatId",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewBytes",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewFinishJobId",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewFormat",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewInstalledStep",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewPath",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "PreviewStep",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "RefinishStateJson",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "TotalSteps",
                table: "GpuJobs");
        }
    }
}
