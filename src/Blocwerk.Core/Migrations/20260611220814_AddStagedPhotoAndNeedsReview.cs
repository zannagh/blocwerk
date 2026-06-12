using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddStagedPhotoAndNeedsReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StagedAt",
                table: "Walls",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StagedByUserId",
                table: "Walls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "StagedPhoto",
                table: "Walls",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedPhotoContentType",
                table: "Walls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "Holds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "Boulders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StagedAt",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedByUserId",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhoto",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "StagedPhotoContentType",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "Boulders");
        }
    }
}
