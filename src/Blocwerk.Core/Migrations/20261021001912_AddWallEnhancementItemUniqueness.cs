using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallEnhancementItemUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WallEnhancementItems_RunId_Kind_HoldId_Written",
                table: "WallEnhancementItems",
                columns: new[] { "RunId", "Kind", "HoldId" },
                unique: true,
                filter: "\"Kind\" IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WallEnhancementItems_RunId_Kind_HoldId_Written",
                table: "WallEnhancementItems");
        }
    }
}
