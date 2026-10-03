using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// AddApiKeyAllowWrite turned on write access for every non-revoked personal key of an app admin, expired
    /// ones included, and later revocations kept the flag. Clears it on every key that can no longer authenticate
    /// (revoked or expired), so the flag only ever shows on a live key. Active keys are left alone: whether
    /// they keep write access is their owner's decision.
    /// </summary>
    public partial class ClearWriteOnInactiveApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "ApiKeys" SET "AllowWrite" = FALSE
                WHERE "AllowWrite" AND ("RevokedAt" IS NOT NULL OR ("ExpiresAt" IS NOT NULL AND "ExpiresAt" <= now()));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: a revoked or expired key cannot authenticate, with or without the flag.
        }
    }
}
