using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTrainingReplayKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "PullupSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "HangboardSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "ClimbingSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PullupSessions_UserId_ClientRequestId",
                table: "PullupSessions",
                columns: new[] { "UserId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HangboardSessions_UserId_ClientRequestId",
                table: "HangboardSessions",
                columns: new[] { "UserId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClimbingSessions_UserId_ClientRequestId",
                table: "ClimbingSessions",
                columns: new[] { "UserId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PullupSessions_UserId_ClientRequestId",
                table: "PullupSessions");

            migrationBuilder.DropIndex(
                name: "IX_HangboardSessions_UserId_ClientRequestId",
                table: "HangboardSessions");

            migrationBuilder.DropIndex(
                name: "IX_ClimbingSessions_UserId_ClientRequestId",
                table: "ClimbingSessions");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "PullupSessions");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "HangboardSessions");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "ClimbingSessions");
        }
    }
}
