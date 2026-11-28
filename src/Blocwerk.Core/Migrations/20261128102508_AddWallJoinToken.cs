using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// Splits the invite (join) token from the public share token. Until now one token did both; every existing wall
    /// starts with the invite equal to its share token, so invite links already handed out keep joining.
    /// </summary>
    public partial class AddWallJoinToken : Migration
    {
        /// <summary>Existing walls: the invite is the token their invite links already carry.</summary>
        public const string CopyShareTokenSql = "UPDATE \"Walls\" SET \"JoinToken\" = \"ShareToken\" WHERE \"ShareToken\" IS NOT NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JoinToken",
                table: "Walls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(CopyShareTokenSql);

            migrationBuilder.CreateIndex(
                name: "IX_Walls_JoinToken",
                table: "Walls",
                column: "JoinToken",
                unique: true,
                filter: "\"JoinToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Walls_JoinToken",
                table: "Walls");

            migrationBuilder.DropColumn(
                name: "JoinToken",
                table: "Walls");
        }
    }
}
