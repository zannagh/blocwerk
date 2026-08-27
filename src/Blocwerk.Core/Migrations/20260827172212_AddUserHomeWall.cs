using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserHomeWall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HomeWallId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_HomeWallId",
                table: "Users",
                column: "HomeWallId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Walls_HomeWallId",
                table: "Users",
                column: "HomeWallId",
                principalTable: "Walls",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Walls_HomeWallId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_HomeWallId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "HomeWallId",
                table: "Users");
        }
    }
}
