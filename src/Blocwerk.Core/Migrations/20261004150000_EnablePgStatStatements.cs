using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// Creates the <c>pg_stat_statements</c> extension (per-statement timings, read by /administration/db-stats), but only
    /// when the server actually preloads it (<c>shared_preload_libraries</c>) and ships it. Without the preload the extension
    /// can be created but every query on it fails, so a dev or CI database without the preload is left alone instead of
    /// getting a half-working view. Idempotent. Production: the compose file adds the preload, so the postgres restart must
    /// come before (or with) the first start of the app version carrying this migration; otherwise run
    /// <c>create extension if not exists pg_stat_statements;</c> by hand afterwards (docker/prod/README.md, "Diagnostics").
    /// </summary>
    [DbContext(typeof(Data.BlocwerkDbContext))]
    [Migration("20261004150000_EnablePgStatStatements")]
    public partial class EnablePgStatStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF 'pg_stat_statements' = ANY (string_to_array(regexp_replace(current_setting('shared_preload_libraries'), '[\s""]', '', 'g'), ','))
       AND EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_stat_statements') THEN
        CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
    END IF;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The extension is left in place: dropping it would discard collected timings and may be shared.
        }
    }
}
