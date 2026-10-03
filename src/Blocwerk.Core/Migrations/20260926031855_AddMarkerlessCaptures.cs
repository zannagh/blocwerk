using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMarkerlessCaptures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DerivedFromModelId",
                table: "WallGeometryModels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FrameSource",
                table: "WallGeometryModels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "AnchorCaptureId",
                table: "WallCaptures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GeometryMode",
                table: "WallCaptures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScaleReferenceJson",
                table: "WallCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SfmJobId",
                table: "WallCaptures",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DerivedFromModelId",
                table: "WallGeometryModels");

            migrationBuilder.DropColumn(
                name: "FrameSource",
                table: "WallGeometryModels");

            migrationBuilder.DropColumn(
                name: "AnchorCaptureId",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "GeometryMode",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "ScaleReferenceJson",
                table: "WallCaptures");

            migrationBuilder.DropColumn(
                name: "SfmJobId",
                table: "WallCaptures");
        }
    }
}
