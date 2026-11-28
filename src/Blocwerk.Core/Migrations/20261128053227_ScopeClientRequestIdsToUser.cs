using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class ScopeClientRequestIdsToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BoulderComments_ClientRequestId",
                table: "BoulderComments");

            migrationBuilder.DropIndex(
                name: "IX_BoulderComments_UserId",
                table: "BoulderComments");

            migrationBuilder.DropIndex(
                name: "IX_Attempts_ClientRequestId",
                table: "Attempts");

            migrationBuilder.DropIndex(
                name: "IX_Attempts_UserId",
                table: "Attempts");

            migrationBuilder.CreateIndex(
                name: "IX_BoulderComments_UserId_ClientRequestId",
                table: "BoulderComments",
                columns: new[] { "UserId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_UserId_ClientRequestId",
                table: "Attempts",
                columns: new[] { "UserId", "ClientRequestId" },
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BoulderComments_UserId_ClientRequestId",
                table: "BoulderComments");

            migrationBuilder.DropIndex(
                name: "IX_Attempts_UserId_ClientRequestId",
                table: "Attempts");

            migrationBuilder.CreateIndex(
                name: "IX_BoulderComments_ClientRequestId",
                table: "BoulderComments",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BoulderComments_UserId",
                table: "BoulderComments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_ClientRequestId",
                table: "Attempts",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Attempts_UserId",
                table: "Attempts",
                column: "UserId");
        }
    }
}
