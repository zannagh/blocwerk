using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginSecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedAuthCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockoutUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotpLastUsedStep",
                table: "Users",
                type: "bigint",
                nullable: true);

            // Case-insensitive uniqueness on the login username, enforced at the DB as a functional
            // unique index on lower("LoginUsername"). This is the real backstop against a TOCTOU race
            // where two concurrent "Bob"/"bob" writes both pass the app-level pre-check; the existing
            // case-sensitive IX_Users_LoginUsername can't catch that. Filtered to non-null so the many
            // users with no password login don't collide.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_Users_LoginUsername_Lower\" " +
                "ON \"Users\" (lower(\"LoginUsername\")) " +
                "WHERE \"LoginUsername\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Users_LoginUsername_Lower\";");

            migrationBuilder.DropColumn(
                name: "FailedAuthCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockoutUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TotpLastUsedStep",
                table: "Users");
        }
    }
}
