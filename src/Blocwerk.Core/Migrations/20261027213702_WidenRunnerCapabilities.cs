using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class WidenRunnerCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Capabilities",
                table: "GpuRunners",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL refuses to narrow a column that holds longer values, so a list over 64 characters is cut back to
            // whole names first (a runner sends its capabilities again with its next hello).
            migrationBuilder.Sql(
                "UPDATE \"GpuRunners\" SET \"Capabilities\" = LEFT(regexp_replace(LEFT(\"Capabilities\", 65), ',[^,]*$', ''), 64) " +
                "WHERE LENGTH(\"Capabilities\") > 64;");
            migrationBuilder.AlterColumn<string>(
                name: "Capabilities",
                table: "GpuRunners",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
