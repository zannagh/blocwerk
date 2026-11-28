using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class UniqueOpenGradeProposal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two concurrent first proposals could leave two open ones on a boulder. Keep the newest open (by time, then
            // id) and resolve the others, so the unique index can be built on any existing data.
            migrationBuilder.Sql(@"
UPDATE ""GradeProposals"" AS g
SET ""IsResolved"" = TRUE
WHERE NOT g.""IsResolved""
  AND EXISTS (
    SELECT 1 FROM ""GradeProposals"" AS n
    WHERE n.""BoulderId"" = g.""BoulderId""
      AND NOT n.""IsResolved""
      AND (n.""CreatedAt"" > g.""CreatedAt"" OR (n.""CreatedAt"" = g.""CreatedAt"" AND n.""Id"" > g.""Id"")));");

            migrationBuilder.CreateIndex(
                name: "IX_GradeProposals_BoulderId_Open",
                table: "GradeProposals",
                column: "BoulderId",
                unique: true,
                filter: "NOT \"IsResolved\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GradeProposals_BoulderId_Open",
                table: "GradeProposals");
        }
    }
}
