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

            // App administrators' existing account keys keep doing what they did before write keys existed.
            migrationBuilder.Sql(
                """
                UPDATE "ApiKeys" AS k SET "AllowWrite" = TRUE
                FROM "Users" AS u
                WHERE k."UserId" = u."Id" AND u."Role" = 2 AND k."Scope" = 1 AND k."RevokedAt" IS NULL;
                """);
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
