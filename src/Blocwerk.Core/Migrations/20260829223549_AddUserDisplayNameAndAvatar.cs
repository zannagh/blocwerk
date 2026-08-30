using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDisplayNameAndAvatar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarContentType",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "AvatarImage",
                table: "Users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomDisplayName",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarContentType",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AvatarImage",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CustomDisplayName",
                table: "Users");
        }
    }
}
