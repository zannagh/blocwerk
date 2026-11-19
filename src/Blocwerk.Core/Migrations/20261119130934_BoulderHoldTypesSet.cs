using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// Replaces a boulder's single <c>DominantHoldType</c> (a <c>HoldHandType</c>, nullable) with the <c>HoldTypes</c> flags
    /// (<c>BoulderHoldType</c>), keeping every value: each stored type becomes its flag. Down maps back a set of exactly one
    /// grip; a set of several types, or one with a hook, has no single type and comes back as none.
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

        /// <summary>A set of exactly one grip back to its HoldHandType; anything else stays null.</summary>
        public const string CopyDownSql =
            """
            UPDATE "Boulders" SET "DominantHoldType" = CASE "HoldTypes"
                WHEN 1 THEN 0 WHEN 2 THEN 2 WHEN 4 THEN 1 WHEN 8 THEN 4 WHEN 16 THEN 3 END
            WHERE "HoldTypes" IN (1, 2, 4, 8, 16);
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

            migrationBuilder.DropColumn(
                name: "DominantHoldType",
                table: "Boulders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DominantHoldType",
                table: "Boulders",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(CopyDownSql);

            migrationBuilder.DropColumn(
                name: "HoldTypes",
                table: "Boulders");
        }
    }
}
