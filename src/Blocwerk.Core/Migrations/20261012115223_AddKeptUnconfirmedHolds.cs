using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddKeptUnconfirmedHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KeptHoldsGeneration",
                table: "Walls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClearedKept",
                table: "WallLookOverFindings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "WallLookOverFindings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClosedReason",
                table: "WallLookOverFindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Covered",
                table: "WallLookOverFindings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HoldKept",
                table: "WallLookOverFindings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "KeptUnconfirmedGeneration",
                table: "Holds",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeptHoldsGeneration",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "ClearedKept",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "ClosedReason",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "Covered",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "HoldKept",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "KeptUnconfirmedGeneration",
                table: "Holds");
        }
    }
}
