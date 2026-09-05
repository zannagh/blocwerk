using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBetaVideoHls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing clips default to false: they keep serving their progressive MP4/original until an
            // admin re-encode produces a ladder (which sets this true). New Ready clips set it explicitly.
            migrationBuilder.AddColumn<bool>(
                name: "HasHls",
                table: "BetaVideos",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasHls",
                table: "BetaVideos");
        }
    }
}
