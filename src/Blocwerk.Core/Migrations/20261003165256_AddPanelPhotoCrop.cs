using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPanelPhotoCrop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PhotoRevision",
                table: "WallPanels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "WallPanelCrops",
                columns: table => new
                {
                    WallPanelId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalPhoto = table.Column<byte[]>(type: "bytea", nullable: false),
                    OriginalPhotoContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Left = table.Column<double>(type: "double precision", nullable: false),
                    Top = table.Column<double>(type: "double precision", nullable: false),
                    Width = table.Column<double>(type: "double precision", nullable: false),
                    Height = table.Column<double>(type: "double precision", nullable: false),
                    CroppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CroppedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallPanelCrops", x => x.WallPanelId);
                    table.ForeignKey(
                        name: "FK_WallPanelCrops_WallPanels_WallPanelId",
                        column: x => x.WallPanelId,
                        principalTable: "WallPanels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallPanelCrops");

            migrationBuilder.DropColumn(
                name: "PhotoRevision",
                table: "WallPanels");
        }
    }
}
