using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMarkerRevisionEffectiveFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EffectiveFrom",
                table: "WallMarkerPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompatibleRevisionFrom",
                table: "WallMarkerObservations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompatibleRevisionTo",
                table: "WallMarkerObservations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "WallMarkerPlans");

            migrationBuilder.DropColumn(
                name: "CompatibleRevisionFrom",
                table: "WallMarkerObservations");

            migrationBuilder.DropColumn(
                name: "CompatibleRevisionTo",
                table: "WallMarkerObservations");
        }
    }
}
