using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCaptureDeviceGravity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DeviceGravityX",
                table: "WallCapturePhotos",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeviceGravityY",
                table: "WallCapturePhotos",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeviceGravityZ",
                table: "WallCapturePhotos",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceGravityX",
                table: "WallCapturePhotos");

            migrationBuilder.DropColumn(
                name: "DeviceGravityY",
                table: "WallCapturePhotos");

            migrationBuilder.DropColumn(
                name: "DeviceGravityZ",
                table: "WallCapturePhotos");
        }
    }
}
