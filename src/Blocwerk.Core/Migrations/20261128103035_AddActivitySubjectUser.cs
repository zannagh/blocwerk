using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddActivitySubjectUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SubjectUserId",
                table: "ActivityLog",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLog_SubjectUserId",
                table: "ActivityLog",
                column: "SubjectUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityLog_Users_SubjectUserId",
                table: "ActivityLog",
                column: "SubjectUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityLog_Users_SubjectUserId",
                table: "ActivityLog");

            migrationBuilder.DropIndex(
                name: "IX_ActivityLog_SubjectUserId",
                table: "ActivityLog");

            migrationBuilder.DropColumn(
                name: "SubjectUserId",
                table: "ActivityLog");
        }
    }
}
