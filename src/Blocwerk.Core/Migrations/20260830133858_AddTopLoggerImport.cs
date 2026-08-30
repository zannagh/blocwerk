using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTopLoggerImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExternalGymId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalGyms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Slug = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalGyms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TopLoggerConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccessTokenProtected = table.Column<string>(type: "text", nullable: false),
                    RefreshTokenProtected = table.Column<string>(type: "text", nullable: false),
                    AccessExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RefreshExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TopLoggerUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    NeedsReauth = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopLoggerConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TopLoggerConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserGradeMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawGradeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FontGrade = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGradeMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserGradeMappings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalAscents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClimbName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExternalGymId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoggedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Ticked = table.Column<bool>(type: "boolean", nullable: false),
                    Topped = table.Column<bool>(type: "boolean", nullable: true),
                    Points = table.Column<double>(type: "double precision", nullable: true),
                    RawGrade = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MappedGrade = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    NeedsGradeMapping = table.Column<bool>(type: "boolean", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalAscents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalAscents_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExternalAscents_ExternalGyms_ExternalGymId",
                        column: x => x.ExternalGymId,
                        principalTable: "ExternalGyms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExternalAscents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ExternalGymId",
                table: "Activities",
                column: "ExternalGymId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAscents_ActivityId",
                table: "ExternalAscents",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAscents_ExternalGymId",
                table: "ExternalAscents",
                column: "ExternalGymId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAscents_UserId_LoggedAt",
                table: "ExternalAscents",
                columns: new[] { "UserId", "LoggedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAscents_UserId_Source_ExternalId",
                table: "ExternalAscents",
                columns: new[] { "UserId", "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalGyms_Source_ExternalId",
                table: "ExternalGyms",
                columns: new[] { "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TopLoggerConnections_UserId",
                table: "TopLoggerConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserGradeMappings_UserId_RawGradeKey",
                table: "UserGradeMappings",
                columns: new[] { "UserId", "RawGradeKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_ExternalGyms_ExternalGymId",
                table: "Activities",
                column: "ExternalGymId",
                principalTable: "ExternalGyms",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_ExternalGyms_ExternalGymId",
                table: "Activities");

            migrationBuilder.DropTable(
                name: "ExternalAscents");

            migrationBuilder.DropTable(
                name: "TopLoggerConnections");

            migrationBuilder.DropTable(
                name: "UserGradeMappings");

            migrationBuilder.DropTable(
                name: "ExternalGyms");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ExternalGymId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ExternalGymId",
                table: "Activities");
        }
    }
}
