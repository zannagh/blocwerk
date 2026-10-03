using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallCaptures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WallCaptures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WallId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Progress = table.Column<double>(type: "double precision", nullable: false),
                    Stage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DeclarationsJson = table.Column<string>(type: "text", nullable: true),
                    SolveJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TexturesJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallCaptures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallCaptures_Walls_WallId",
                        column: x => x.WallId,
                        principalTable: "Walls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallGeometryTextures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GeometryModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    FacetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StoredPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    AMin = table.Column<double>(type: "double precision", nullable: false),
                    AMax = table.Column<double>(type: "double precision", nullable: false),
                    BMin = table.Column<double>(type: "double precision", nullable: false),
                    BMax = table.Column<double>(type: "double precision", nullable: false),
                    WidthPx = table.Column<int>(type: "integer", nullable: false),
                    HeightPx = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallGeometryTextures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallGeometryTextures_WallGeometryModels_GeometryModelId",
                        column: x => x.GeometryModelId,
                        principalTable: "WallGeometryModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WallCapturePhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StoredPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Focal35mm = table.Column<double>(type: "double precision", nullable: true),
                    CameraGroup = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MarkersJson = table.Column<string>(type: "text", nullable: true),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallCapturePhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WallCapturePhotos_WallCaptures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "WallCaptures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WallCapturePhotos_CaptureId_Index",
                table: "WallCapturePhotos",
                columns: new[] { "CaptureId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WallCaptures_Status",
                table: "WallCaptures",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_WallCaptures_WallId_CreatedAt",
                table: "WallCaptures",
                columns: new[] { "WallId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WallGeometryTextures_GeometryModelId_FacetId",
                table: "WallGeometryTextures",
                columns: new[] { "GeometryModelId", "FacetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WallCapturePhotos");

            migrationBuilder.DropTable(
                name: "WallGeometryTextures");

            migrationBuilder.DropTable(
                name: "WallCaptures");
        }
    }
}
