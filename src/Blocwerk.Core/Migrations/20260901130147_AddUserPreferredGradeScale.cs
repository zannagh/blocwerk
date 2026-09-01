using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPreferredGradeScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fontainebleau is the app-wide default grade scale, so existing users get true here (the
            // CLR property initializer only governs newly-constructed entities, not this backfill).
            migrationBuilder.AddColumn<bool>(
                name: "PreferFontGrades",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferFontGrades",
                table: "Users");
        }
    }
}
