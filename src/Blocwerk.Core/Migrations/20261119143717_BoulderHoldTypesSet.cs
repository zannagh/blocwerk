using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// Adds a boulder's <c>HoldTypes</c> flags (<c>BoulderHoldType</c>) and copies every single <c>DominantHoldType</c>
    /// (a <c>HoldHandType</c>) to its flag. The old column stays, unmapped by the entity, for one release so a rollback to
    /// 0.7.1 still reads it; a later release drops it. Down writes the set back where it is exactly one grip (anything else
    /// has no single type and becomes none) and drops <c>HoldTypes</c>.
    /// </summary>
    public partial class BoulderHoldTypesSet : Migration
    {
        /// <summary>HoldHandType (Jug 0, Pinch 1, Crimp 2, Pocket 3, Sloper 4) to its BoulderHoldType flag (Jug 1, Crimp 2, Pinch 4, Sloper 8, Pocket 16).</summary>
        public const string CopyUpSql =
            """
            UPDATE "Boulders" SET "HoldTypes" = CASE "DominantHoldType"
                WHEN 0 THEN 1 WHEN 2 THEN 2 WHEN 1 THEN 4 WHEN 4 THEN 8 WHEN 3 THEN 16 ELSE 0 END
            WHERE "DominantHoldType" IS NOT NULL;
            """;

        /// <summary>The set back to a HoldHandType where it is exactly one grip; any other set becomes null.</summary>
        public const string CopyDownSql =
            """
            UPDATE "Boulders" SET "DominantHoldType" = CASE "HoldTypes"
                WHEN 1 THEN 0 WHEN 2 THEN 2 WHEN 4 THEN 1 WHEN 8 THEN 4 WHEN 16 THEN 3 END;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HoldTypes",
                table: "Boulders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(CopyUpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CopyDownSql);

            migrationBuilder.DropColumn(
                name: "HoldTypes",
                table: "Boulders");
        }
    }
}
