using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBoulderRulesWallSegmentsAndClientRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FootColorOnly",
                table: "Boulders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HandsFollowFeet",
                table: "Boulders",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // A boulder that already marked any hold with a non-default usage defined its
            // footholds by hand, so hands do not follow feet there.
            migrationBuilder.Sql(
                """
                UPDATE "Boulders" SET "HandsFollowFeet" = NOT EXISTS (
                    SELECT 1 FROM "BoulderHolds" bh
                    WHERE bh."BoulderId" = "Boulders"."Id" AND bh."Usage" <> 0);
                """);

            migrationBuilder.DropColumn(
                name: "FootholdMode",
                table: "Boulders");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "BoulderComments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "Attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Angle = table.Column<int>(type: "integer", nullable: false),
                    Points = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallSegments_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoulderComments_ClientRequestId",
                table: "BoulderComments",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_ClientRequestId",
                table: "Attempts",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WallSegments_WallId_SortOrder",
                table: "WallSegments",
                columns: new[] { "WallId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallSegments");

            migrationBuilder.DropIndex(
                name: "IX_BoulderComments_ClientRequestId",
                table: "BoulderComments");

            migrationBuilder.DropIndex(
                name: "IX_Attempts_ClientRequestId",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "BoulderComments");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "Attempts");

            migrationBuilder.AddColumn<int>(
                name: "FootholdMode",
                table: "Boulders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Collapse the rules back into the derived mode the column used to hold:
            // 1 = DefinedOnly, 0 = AllKickboard.
            migrationBuilder.Sql(
                """
                UPDATE "Boulders" SET "FootholdMode" = CASE
                    WHEN "HandsFollowFeet" = FALSE OR "FootColorOnly" IS NOT NULL THEN 1
                    ELSE 0 END;
                """);

            migrationBuilder.DropColumn(
                name: "FootColorOnly",
                table: "Boulders");

            migrationBuilder.DropColumn(
                name: "HandsFollowFeet",
                table: "Boulders");
        }
    }
}
