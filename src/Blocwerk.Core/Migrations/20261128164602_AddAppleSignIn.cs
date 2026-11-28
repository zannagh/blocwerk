using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAppleSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppleLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LinkedFrom = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppleLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppleLinks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppleSignInNonces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NonceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppleSignInNonces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppleLinks_Subject",
                table: "AppleLinks",
                column: "Subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppleLinks_UserId",
                table: "AppleLinks",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppleSignInNonces_ExpiresAt",
                table: "AppleSignInNonces",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AppleSignInNonces_NonceHash",
                table: "AppleSignInNonces",
                column: "NonceHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppleLinks");

            migrationBuilder.DropTable(
                name: "AppleSignInNonces");
        }
    }
}
