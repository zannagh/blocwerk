using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class TombstoneHoldGenerationLinkEnds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HoldGenerationLinks_OldHoldId_NewHoldId",
                table: "HoldGenerationLinks");

            migrationBuilder.AlterColumn<Guid>(
                name: "OldHoldId",
                table: "HoldGenerationLinks",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "NewHoldId",
                table: "HoldGenerationLinks",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_HoldGenerationLinks_OldHoldId_NewHoldId",
                table: "HoldGenerationLinks",
                columns: new[] { "OldHoldId", "NewHoldId" },
                unique: true,
                filter: "\"OldHoldId\" IS NOT NULL AND \"NewHoldId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately refuses rather than reverting. Up() widens two FK columns so a lineage row
            // can outlive the hold it pointed at; by the time anyone reverts, rows with a NULL end may
            // exist. The generated inverse would set those to Guid.Empty and then re-add NOT NULL,
            // which the Restrict FK to Holds rejects part-way through — leaving the index dropped and
            // the columns half-migrated. Failing here instead keeps the schema consistent and makes
            // the real question explicit: reverting means deciding what to do with the tombstones,
            // which is a data decision, not a schema one.
            throw new NotSupportedException(
                "TombstoneHoldGenerationLinkEnds cannot be reverted automatically: lineage rows with a "
                + "NULL hold end cannot be made NOT NULL again. Decide what should happen to those rows "
                + "(delete them, or re-point them) and write a forward migration that does it.");
        }
    }
}
