using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallLookOver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LookOverCompletedAt",
                table: "Walls",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LookOverCompletedByUserId",
                table: "Walls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LookOverGeneration",
                table: "Walls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LookOverPendingSince",
                table: "Walls",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WallLookOverFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OldHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    OtherHoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    PanelId = table.Column<Guid>(type: "uuid", nullable: true),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Y = table.Column<double>(type: "double precision", nullable: false),
                    Radius = table.Column<double>(type: "double precision", nullable: false),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: true),
                    FacetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    A = table.Column<double>(type: "double precision", nullable: true),
                    B = table.Column<double>(type: "double precision", nullable: true),
                    PhotoScore = table.Column<double>(type: "double precision", nullable: true),
                    TextureScore = table.Column<double>(type: "double precision", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    BouldersAtApply = table.Column<int>(type: "integer", nullable: false),
                    FlaggedHold = table.Column<bool>(type: "boolean", nullable: false),
                    Converted = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Answer = table.Column<int>(type: "integer", nullable: true),
                    AnsweredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MergeBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallLookOverFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallLookOverFindings_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallLookOverFindings_WallId_Status",
                table: "WallLookOverFindings",
                columns: new[] { "WallId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "LookOverCompletedAt",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "LookOverCompletedByUserId",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "LookOverGeneration",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "LookOverPendingSince",
                table: "Walls");
        }
    }
}
