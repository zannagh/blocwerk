using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTopLoggerIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalAscents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClimbName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GymName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Grade = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    LoggedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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
                        name: "FK_ExternalAscents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TopLoggerConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UserUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TokenEncrypted = table.Column<string>(type: "text", nullable: false),
                    Backend = table.Column<int>(type: "integer", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
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

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAscents_ActivityId",
                table: "ExternalAscents",
                column: "ActivityId");

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
                name: "IX_TopLoggerConnections_UserId",
                table: "TopLoggerConnections",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalAscents");

            migrationBuilder.DropTable(
                name: "TopLoggerConnections");
        }
    }
}
