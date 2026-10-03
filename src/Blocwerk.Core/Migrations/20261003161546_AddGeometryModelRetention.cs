using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGeometryModelRetention : Migration
    {
        /// <summary>
        /// Every model already inactive counts as retired at the deploy, so each gets the full grace of the capture
        /// retention once (when it was really replaced is not known; ties are ranked by creation).
        /// </summary>
        public const string BackfillRetiredAtSql =
            "UPDATE \"WallGeometryModels\" SET \"RetiredAt\" = now() WHERE NOT \"IsActive\" AND \"RetiredAt\" IS NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FilesRemovedAt",
                table: "WallGeometryModels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                table: "WallGeometryModels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(BackfillRetiredAtSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilesRemovedAt",
                table: "WallGeometryModels");

            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "WallGeometryModels");
        }
    }
}
