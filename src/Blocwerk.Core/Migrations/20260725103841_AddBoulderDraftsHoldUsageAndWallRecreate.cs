using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBoulderDraftsHoldUsageAndWallRecreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WallResets_WallId",
                table: "WallResets");

            migrationBuilder.DropIndex(
                name: "IX_Holds_WallId",
                table: "Holds");

            migrationBuilder.AddColumn<string>(
                name: "PreviousPhotoContentType",
                table: "WallResets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDraft",
                table: "Boulders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Existing boulders were all set under the previous default of
            // "all kickboard footholds are on", so they backfill to true.
            migrationBuilder.AddColumn<bool>(
                name: "KickboardFootholdsOn",
                table: "Boulders",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "Boulders",
                type: "timestamp with time zone",
                nullable: true);

            // Every pre-existing boulder was published at creation time.
            migrationBuilder.Sql(@"UPDATE ""Boulders"" SET ""PublishedAt"" = ""CreatedAt"";");

            migrationBuilder.AddColumn<int>(
                name: "Usage",
                table: "BoulderHolds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_WallResets_WallId_Generation",
                table: "WallResets",
                columns: new[] { "WallId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Holds_WallId_Generation",
                table: "Holds",
                columns: new[] { "WallId", "Generation" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WallResets_WallId_Generation",
                table: "WallResets");

            migrationBuilder.DropIndex(
                name: "IX_Holds_WallId_Generation",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "PreviousPhotoContentType",
                table: "WallResets");

            migrationBuilder.DropColumn(
                name: "IsDraft",
                table: "Boulders");

            migrationBuilder.DropColumn(
                name: "KickboardFootholdsOn",
                table: "Boulders");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Boulders");

            migrationBuilder.DropColumn(
                name: "Usage",
                table: "BoulderHolds");

            migrationBuilder.CreateIndex(
                name: "IX_WallResets_WallId",
                table: "WallResets",
                column: "WallId");

            migrationBuilder.CreateIndex(
                name: "IX_Holds_WallId",
                table: "Holds",
                column: "WallId");
        }
    }
}
