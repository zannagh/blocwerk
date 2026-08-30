using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallMaintenanceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceByUserId",
                table: "Walls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UnderMaintenance",
                table: "Walls",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaintenanceByUserId",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "UnderMaintenance",
                table: "Walls");
        }
    }
}
