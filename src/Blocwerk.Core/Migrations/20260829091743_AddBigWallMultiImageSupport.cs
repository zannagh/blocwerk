using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddBigWallMultiImageSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsesMultipleImages",
                table: "Walls",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "WallPanelId",
                table: "Holds",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HoldLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldAId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldBId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HoldLinks_Holds_HoldAId",
                        column: x => x.HoldAId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HoldLinks_Holds_HoldBId",
                        column: x => x.HoldBId,
                        principalTable: "Holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HoldLinks_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallPanels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Col = table.Column<int>(type: "integer", nullable: false),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    Photo = table.Column<byte[]>(type: "bytea", nullable: true),
                    PhotoContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StagedPhoto = table.Column<byte[]>(type: "bytea", nullable: true),
                    StagedPhotoContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StagedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StagedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallPanels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallPanels_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Holds_WallPanelId",
                table: "Holds",
                column: "WallPanelId");

            migrationBuilder.CreateIndex(
                name: "IX_HoldLinks_HoldAId_HoldBId",
                table: "HoldLinks",
                columns: new[] { "HoldAId", "HoldBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HoldLinks_HoldBId",
                table: "HoldLinks",
                column: "HoldBId");

            migrationBuilder.CreateIndex(
                name: "IX_HoldLinks_WallId",
                table: "HoldLinks",
                column: "WallId");

            migrationBuilder.CreateIndex(
                name: "IX_WallPanels_WallId_Col_Row_Generation",
                table: "WallPanels",
                columns: new[] { "WallId", "Col", "Row", "Generation" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Holds_WallPanels_WallPanelId",
                table: "Holds",
                column: "WallPanelId",
                principalTable: "WallPanels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Holds_WallPanels_WallPanelId",
                table: "Holds");

            migrationBuilder.DropTable(
                name: "HoldLinks");

            migrationBuilder.DropTable(
                name: "WallPanels");

            migrationBuilder.DropIndex(
                name: "IX_Holds_WallPanelId",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "UsesMultipleImages",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "WallPanelId",
                table: "Holds");
        }
    }
}
