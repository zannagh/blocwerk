using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWallChangeFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedEpochs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FloorTxId = table.Column<long>(type: "bigint", nullable: true),
                    FloorSeq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedEpochs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WallChanges",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TxId = table.Column<long>(type: "bigint", nullable: true),
                    WallId = table.Column<Guid>(type: "uuid", nullable: true),
                    BoulderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Audience = table.Column<short>(type: "smallint", nullable: false),
                    AudienceUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DataJson = table.Column<string>(type: "jsonb", nullable: true),
                    SchemaVersion = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WallChanges", x => x.Seq);
                });

            // The feed cursor's transaction id, Postgres only (S9): the EF model has no default, so SQLite's EnsureCreated
            // gets a plain nullable column. The cast through text avoids relying on an Npgsql xid8 mapping.
            if (migrationBuilder.IsNpgsql())
            {
                migrationBuilder.Sql(
                    "ALTER TABLE \"WallChanges\" ALTER COLUMN \"TxId\" SET DEFAULT (pg_current_xact_id()::text::bigint), "
                    + "ALTER COLUMN \"TxId\" SET NOT NULL;");
            }

            migrationBuilder.CreateIndex(
                name: "IX_WallChanges_ActorUserId",
                table: "WallChanges",
                column: "ActorUserId",
                filter: "\"ActorUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WallChanges_AudienceUserId_TxId_Seq",
                table: "WallChanges",
                columns: new[] { "AudienceUserId", "TxId", "Seq" },
                filter: "\"AudienceUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WallChanges_CreatedAt",
                table: "WallChanges",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WallChanges_WallId_TxId_Seq",
                table: "WallChanges",
                columns: new[] { "WallId", "TxId", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedEpochs");

            migrationBuilder.DropTable(
                name: "WallChanges");
        }
    }
}
