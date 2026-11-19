using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBoulderParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentBoulderId",
                table: "Boulders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Boulders_ParentBoulderId",
                table: "Boulders",
                column: "ParentBoulderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Boulders_Boulders_ParentBoulderId",
                table: "Boulders",
                column: "ParentBoulderId",
                principalTable: "Boulders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Boulders_Boulders_ParentBoulderId",
                table: "Boulders");

            migrationBuilder.DropIndex(
                name: "IX_Boulders_ParentBoulderId",
                table: "Boulders");

            migrationBuilder.DropColumn(
                name: "ParentBoulderId",
                table: "Boulders");
        }
    }
}
