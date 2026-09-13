using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldGenerationLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HoldGenerationLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldHoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    NewHoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FromGeneration = table.Column<int>(type: "integer", nullable: false),
                    ToGeneration = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldGenerationLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HoldGenerationLinks_Holds_NewHoldId",
                        column: x => x.NewHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HoldGenerationLinks_Holds_OldHoldId",
                        column: x => x.OldHoldId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HoldGenerationLinks_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HoldGenerationLinks_NewHoldId",
                table: "HoldGenerationLinks",
                column: "NewHoldId");

            migrationBuilder.CreateIndex(
                name: "IX_HoldGenerationLinks_OldHoldId_NewHoldId",
                table: "HoldGenerationLinks",
                columns: new[] { "OldHoldId", "NewHoldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HoldGenerationLinks_WallId",
                table: "HoldGenerationLinks",
                column: "WallId");

            migrationBuilder.CreateIndex(
                name: "IX_HoldGenerationLinks_WallId_ToGeneration",
                table: "HoldGenerationLinks",
                columns: new[] { "WallId", "ToGeneration" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HoldGenerationLinks");
        }
    }
}
