using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddVolumeFlatSidesAndRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "FlatFitRmsMm",
                table: "WallVolumes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasFlatSides",
                table: "WallVolumes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HeightFieldJson",
                table: "WallVolumes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRemoved",
                table: "WallVolumes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedAt",
                table: "WallVolumes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VolumesHaveFlatSides",
                table: "Walls",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlatFitRmsMm",
                table: "WallVolumes");

            migrationBuilder.DropColumn(
                name: "HasFlatSides",
                table: "WallVolumes");

            migrationBuilder.DropColumn(
                name: "HeightFieldJson",
                table: "WallVolumes");

            migrationBuilder.DropColumn(
                name: "IsRemoved",
                table: "WallVolumes");

            migrationBuilder.DropColumn(
                name: "RemovedAt",
                table: "WallVolumes");

            migrationBuilder.DropColumn(
                name: "VolumesHaveFlatSides",
                table: "Walls");
        }
    }
}
