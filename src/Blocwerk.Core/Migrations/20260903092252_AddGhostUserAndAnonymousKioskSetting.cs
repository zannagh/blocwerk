using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGhostUserAndAnonymousKioskSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowAnonymousKioskSetting",
                table: "Walls",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "AvatarContentType", "AvatarImage", "CreatedAt", "CustomDisplayName", "DeletedAt", "DisplayName", "Email", "EmailVerified", "FailedAuthCount", "HomeWallId", "Identifier", "LockoutUntil", "LoginUsername", "PasswordHash", "PreferFontGrades", "ProgressionGroupBy", "ProgressionWindowDays", "Role", "TotpEnabled", "TotpLastUsedStep", "TotpSecretProtected" },
                values: new object[] { new Guid("00000000-0000-4000-8000-000000000001"), null, null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "Ghost", null, false, 0, null, "system__ghost", null, null, null, true, 1, 60, 1, false, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Users",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-4000-8000-000000000001"));

            migrationBuilder.DropColumn(
                name: "AllowAnonymousKioskSetting",
                table: "Walls");
        }
    }
}
