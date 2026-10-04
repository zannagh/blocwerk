using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRunnerTexturesJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Capabilities",
                table: "GpuRunners",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TexturesMemoryMb",
                table: "GpuRunners",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "GpuJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequiredMemoryMb",
                table: "GpuJobs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Capabilities",
                table: "GpuRunners");

            migrationBuilder.DropColumn(
                name: "TexturesMemoryMb",
                table: "GpuRunners");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "GpuJobs");

            migrationBuilder.DropColumn(
                name: "RequiredMemoryMb",
                table: "GpuJobs");
        }
    }
}
