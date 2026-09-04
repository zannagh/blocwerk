using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBetaVideoEncodingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncodingError",
                table: "BetaVideos",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            // Backfill: seed EVERY EXISTING clip to Ready (BetaVideoEncodingStatus.Ready == 2), NOT
            // Pending. Existing clips already play their original today, so marking them Pending would
            // (a) show a spurious "optimizing" state until the single worker drained the whole backlog,
            // and (b) permanently break any clip that FAILS normalization — a regression from playable.
            // Seeding Ready keeps them serving their original immediately and keeps the worker from
            // auto-enqueuing them (it only picks up Pending rows). New uploads are still Pending: EF
            // always sends the entity value (BetaVideo defaults EncodingStatus to Pending, and
            // AddVideoFromFileAsync sets it explicitly), so this column default only ever backfills the
            // rows that already existed. Admins re-encode legacy clips on demand via the admin button.
            migrationBuilder.AddColumn<int>(
                name: "EncodingStatus",
                table: "BetaVideos",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastEncodedUtc",
                table: "BetaVideos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BetaVideos_EncodingStatus",
                table: "BetaVideos",
                column: "EncodingStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BetaVideos_EncodingStatus",
                table: "BetaVideos");

            migrationBuilder.DropColumn(
                name: "EncodingError",
                table: "BetaVideos");

            migrationBuilder.DropColumn(
                name: "EncodingStatus",
                table: "BetaVideos");

            migrationBuilder.DropColumn(
                name: "LastEncodedUtc",
                table: "BetaVideos");
        }
    }
}
