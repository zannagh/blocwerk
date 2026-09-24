using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyAllowWrite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowWrite",
                table: "ApiKeys",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowWrite",
                table: "ApiKeys");
        }
    }
}
