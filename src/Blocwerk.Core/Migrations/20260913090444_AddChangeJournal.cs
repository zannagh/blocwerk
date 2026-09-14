using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeJournalBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScopeKind = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceInstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeJournalBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JournalBlobs",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Len = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalBlobs", x => x.Sha256);
                });

            migrationBuilder.CreateTable(
                name: "ChangeJournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    KeyJson = table.Column<string>(type: "text", nullable: false),
                    Op = table.Column<int>(type: "integer", nullable: false),
                    BeforeJson = table.Column<string>(type: "text", nullable: true),
                    AfterJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeJournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeJournalEntries_ChangeJournalBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ChangeJournalBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeJournalBatches_CreatedAt",
                table: "ChangeJournalBatches",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeJournalBatches_ScopeKind_ScopeId",
                table: "ChangeJournalBatches",
                columns: new[] { "ScopeKind", "ScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeJournalEntries_BatchId_Seq",
                table: "ChangeJournalEntries",
                columns: new[] { "BatchId", "Seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeJournalEntries");

            migrationBuilder.DropTable(
                name: "JournalBlobs");

            migrationBuilder.DropTable(
                name: "ChangeJournalBatches");
        }
    }
}
