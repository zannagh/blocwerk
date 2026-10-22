using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class LinkActivityEntryToAttempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AttemptId",
                table: "ActivityLog",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLog_AttemptId",
                table: "ActivityLog",
                column: "AttemptId");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityLog_Attempts_AttemptId",
                table: "ActivityLog",
                column: "AttemptId",
                principalTable: "Attempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityLog_Attempts_AttemptId",
                table: "ActivityLog");

            migrationBuilder.DropIndex(
                name: "IX_ActivityLog_AttemptId",
                table: "ActivityLog");

            migrationBuilder.DropColumn(
                name: "AttemptId",
                table: "ActivityLog");
        }
    }
}
