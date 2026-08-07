using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityAndGrouping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProgressionGroupBy",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 1); // ProgressionGroupBy.Week — sensible default for existing users

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "PullupSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "HangboardSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "Attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastEventAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Activities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Activities_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PullupSessions_ActivityId",
                table: "PullupSessions",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_HangboardSessions_ActivityId",
                table: "HangboardSessions",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_ActivityId",
                table: "Attempts",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_UserId_StartedAt",
                table: "Activities",
                columns: new[] { "UserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_WallId",
                table: "Activities",
                column: "WallId");

            migrationBuilder.AddForeignKey(
                name: "FK_Attempts_Activities_ActivityId",
                table: "Attempts",
                column: "ActivityId",
                principalTable: "Activities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_HangboardSessions_Activities_ActivityId",
                table: "HangboardSessions",
                column: "ActivityId",
                principalTable: "Activities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PullupSessions_Activities_ActivityId",
                table: "PullupSessions",
                column: "ActivityId",
                principalTable: "Activities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Attempts_Activities_ActivityId",
                table: "Attempts");

            migrationBuilder.DropForeignKey(
                name: "FK_HangboardSessions_Activities_ActivityId",
                table: "HangboardSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_PullupSessions_Activities_ActivityId",
                table: "PullupSessions");

            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_PullupSessions_ActivityId",
                table: "PullupSessions");

            migrationBuilder.DropIndex(
                name: "IX_HangboardSessions_ActivityId",
                table: "HangboardSessions");

            migrationBuilder.DropIndex(
                name: "IX_Attempts_ActivityId",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "ProgressionGroupBy",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "PullupSessions");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "HangboardSessions");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "Attempts");
        }
    }
}
