using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedHoldSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedHoldSyncVersion",
                table: "Walls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkedHoldWinnerCol",
                table: "Walls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkedHoldWinnerRow",
                table: "Walls",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkedHoldSyncVersion",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "LinkedHoldWinnerCol",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "LinkedHoldWinnerRow",
                table: "Walls");
        }
    }
}
